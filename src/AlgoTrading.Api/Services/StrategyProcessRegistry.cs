// src/AlgoTrading.Api/Services/StrategyProcessRegistry.cs
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AlgoTrading.Api.Services;

/// <summary>
/// A strategy runner process the API launched, with the run configuration it
/// was launched with and ring buffers of its recent output and signals.
/// </summary>
public sealed record RunningStrategy(
    int StrategyId,
    string Name,
    Process Process,
    string StartedBy,
    long UserId,
    DateTime StartedUtc,
    long RunId,
    string Underlying,
    string SpotSymbol,
    int Lots,
    decimal? StopLoss,
    decimal? Target)
{
    public const int LogCapacity = 300;
    public const int SignalCapacity = 100;

    internal readonly object LogLock = new();
    internal readonly Queue<string> Logs = new();
    internal readonly object SignalLock = new();
    internal readonly List<object> Signals = new();

    /// <summary>Captured at attach time so it stays readable after the process exits.</summary>
    public int ProcessId { get; internal set; }

    public DateTime? LastLogUtc { get; internal set; }

    /// <summary>Last stderr line, for the "Runner exited" reason.</summary>
    public string? LastStderrLine { get; internal set; }

    /// <summary>The exit monitor task, so a deliberate stop can wait for it before disposing the process.</summary>
    internal Task? ExitMonitor { get; set; }

    /// <summary>
    /// Completed (with the number of positions flattened) once whoever owns the
    /// stop has finished its bookkeeping. Concurrent stoppers await this instead
    /// of flattening a second time.
    /// </summary>
    internal readonly TaskCompletionSource<int> StopCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0 = running, 1 = a stop (deliberate or runner exit) has been claimed.
    private int _stopState;

    /// <summary>
    /// True once a stop has been claimed, whether by StrategyRunControl or by the
    /// exit monitor. Only the claimant does the flatten/persist bookkeeping.
    /// </summary>
    public bool StopRequested => Volatile.Read(ref _stopState) != 0;

    /// <summary>
    /// Atomically claims the stop. Exactly one caller gets <c>true</c>; every
    /// other stopper (UI stop racing the risk guard, market close racing both,
    /// the exit monitor racing any of them) gets <c>false</c> and must wait on
    /// <see cref="StopCompletion"/> rather than flatten again.
    /// </summary>
    internal bool TryClaimStop() => Interlocked.CompareExchange(ref _stopState, 1, 0) == 0;
}

/// <summary>
/// Why and when a strategy's last run ended, kept in memory after the entry is
/// removed so the list can show "Stopped · reason" until the next start.
/// </summary>
public sealed record LastExit(
    int StrategyId,
    string Name,
    long RunId,
    string Reason,
    DateTime AtUtc,
    string Underlying,
    string SpotSymbol,
    int Lots,
    decimal? StopLoss,
    decimal? Target,
    string StartedBy,
    DateTime StartedUtc);

/// <summary>
/// In-process registry of running strategy processes, keyed by strategy id.
///
/// This state is lost if the API restarts, which orphans any running Python
/// process — it keeps trading but can no longer be stopped from here. The
/// database (SimulationRun + RUN_STOPPED signals) carries what needs to survive.
/// </summary>
public sealed class StrategyProcessRegistry
{
    private readonly ConcurrentDictionary<int, RunningStrategy> _running = new();
    private readonly ConcurrentDictionary<int, LastExit> _lastExits = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StrategyProcessRegistry> _logger;

    public StrategyProcessRegistry(IServiceScopeFactory scopeFactory, ILogger<StrategyProcessRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Serializes start attempts so two overlapping POSTs cannot both spawn a
    /// process for the same strategy (the second would be untracked).
    /// </summary>
    public object StartLock { get; } = new();

    public int Count => _running.Count;

    public bool Contains(int strategyId) => _running.ContainsKey(strategyId);

    /// <summary>
    /// Registers a freshly started process: wires stdout/stderr draining (the
    /// pipes MUST be read or the Python process blocks on its next print) and an
    /// exit monitor that records "Runner exited" when the process dies on its own.
    /// </summary>
    public bool TryAdd(RunningStrategy entry)
    {
        if (!_running.TryAdd(entry.StrategyId, entry))
        {
            return false;
        }

        var process = entry.Process;
        try
        {
            entry.ProcessId = process.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read process id for strategy {StrategyId}.", entry.StrategyId);
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | {e.Data}");
            _logger.LogInformation("[strategy:{Name}] {Line}", entry.Name, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            entry.LastStderrLine = e.Data;
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
            _logger.LogWarning("[strategy:{Name}:err] {Line}", entry.Name, e.Data);
        };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to begin draining output for strategy {StrategyId}.", entry.StrategyId);
        }

        Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | runner started (pid {entry.ProcessId}, run {entry.RunId}, {entry.Underlying} x{entry.Lots})");

        entry.ExitMonitor = Task.Run(() => MonitorExitAsync(entry));
        return true;
    }

    public RunningStrategy? Get(int strategyId)
        => _running.TryGetValue(strategyId, out var entry) ? entry : null;

    public IReadOnlyList<RunningStrategy> List()
        => _running.Values.OrderBy(x => x.StartedUtc).ToList();

    public bool Remove(int strategyId) => _running.TryRemove(strategyId, out _);

    /// <summary>
    /// Snapshots the running entry as its LastExit. No-op when the strategy is not running.
    /// </summary>
    public void RecordExit(int strategyId, string reason)
    {
        if (_running.TryGetValue(strategyId, out var entry))
        {
            RecordExit(entry, reason);
        }
    }

    public void RecordExit(RunningStrategy entry, string reason)
    {
        _lastExits[entry.StrategyId] = new LastExit(
            entry.StrategyId, entry.Name, entry.RunId, reason, DateTime.UtcNow,
            entry.Underlying, entry.SpotSymbol, entry.Lots, entry.StopLoss, entry.Target,
            entry.StartedBy, entry.StartedUtc);
    }

    public LastExit? GetLastExit(int strategyId)
        => _lastExits.TryGetValue(strategyId, out var exit) ? exit : null;

    public void ClearLastExit(int strategyId) => _lastExits.TryRemove(strategyId, out _);

    public void AppendLog(int strategyId, string line)
    {
        if (_running.TryGetValue(strategyId, out var entry))
        {
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | {line}");
        }
    }

    /// <summary>The most recent <paramref name="take"/> lines, oldest first.</summary>
    public IReadOnlyList<string> GetLogs(int strategyId, int take)
    {
        if (!_running.TryGetValue(strategyId, out var entry)) return Array.Empty<string>();

        lock (entry.LogLock)
        {
            var lines = entry.Logs.ToArray();
            int count = Math.Clamp(take, 1, RunningStrategy.LogCapacity);
            return lines.Skip(Math.Max(0, lines.Length - count)).ToList();
        }
    }

    public bool AddSignal(int strategyId, object signal)
    {
        if (!_running.TryGetValue(strategyId, out var entry)) return false;

        lock (entry.SignalLock)
        {
            entry.Signals.Insert(0, signal);
            if (entry.Signals.Count > RunningStrategy.SignalCapacity)
            {
                entry.Signals.RemoveRange(RunningStrategy.SignalCapacity, entry.Signals.Count - RunningStrategy.SignalCapacity);
            }
        }
        return true;
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<object> GetSignals(int strategyId)
    {
        if (!_running.TryGetValue(strategyId, out var entry)) return Array.Empty<object>();

        lock (entry.SignalLock)
        {
            return entry.Signals.ToList();
        }
    }

    private static void Append(RunningStrategy entry, string line)
    {
        lock (entry.LogLock)
        {
            entry.Logs.Enqueue(line);
            while (entry.Logs.Count > RunningStrategy.LogCapacity)
            {
                entry.Logs.Dequeue();
            }
            entry.LastLogUtc = DateTime.UtcNow;
        }
    }

    private async Task MonitorExitAsync(RunningStrategy entry)
    {
        var process = entry.Process;
        int exitCode = -1;

        try
        {
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exit monitor for strategy {StrategyId} failed.", entry.StrategyId);
        }

        Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | runner exited with code {exitCode}");

        // A deliberate stop (StrategyRunControl) owns the bookkeeping and disposes
        // the process once it is done with it; the monitor only reports the exit.
        if (!entry.TryClaimStop())
        {
            return;
        }

        var reason = $"Runner exited (code {exitCode})";
        if (exitCode != 0 && !string.IsNullOrWhiteSpace(entry.LastStderrLine))
        {
            reason += $": {entry.LastStderrLine.Trim()}";
        }

        _logger.LogWarning("Strategy {StrategyId} ({Name}) exited on its own: {Reason}", entry.StrategyId, entry.Name, reason);

        // The runner died with its positions still open: nothing else guards them
        // (the risk guard only watches registry entries), so square them off here
        // before the run is closed, exactly as a stop would.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
            await control.HandleRunnerExitAsync(entry, reason, exitCode == 0 ? null : reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finish exit of strategy {StrategyId} run {RunId}.", entry.StrategyId, entry.RunId);

            // Never leave the registry pointing at a dead process.
            RecordExit(entry, reason);
            Remove(entry.StrategyId);
            entry.StopCompletion.TrySetResult(0);
            try { process.Dispose(); } catch { /* already gone */ }
        }
    }
}
