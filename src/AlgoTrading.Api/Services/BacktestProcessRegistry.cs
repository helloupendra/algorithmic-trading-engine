// src/AlgoTrading.Api/Services/BacktestProcessRegistry.cs
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The runner's latest progress report (POST /api/Simulator/runs/{id}/progress).
/// Replaced atomically as a whole so readers never see a half-updated report.
/// </summary>
public sealed record BacktestProgressState(
    decimal Percent,
    long BarsProcessed,
    long TotalBars,
    DateTime? CurrentUtc,
    int Trades,
    string? Message,
    DateTime UpdatedUtc);

/// <summary>
/// A backtest runner process the API launched, with the run configuration it
/// was launched with, a ring buffer of its recent output and its progress.
/// </summary>
public sealed record RunningBacktest(
    long RunId,
    int StrategyId,
    string StrategyName,
    Process Process,
    string StartedBy,
    long UserId,
    DateTime StartedUtc,
    string Underlying,
    string SpotSymbol,
    int Lots,
    decimal? StopLoss,
    decimal? Target,
    string Resolution,
    DateTime FromUtc,
    DateTime ToUtc)
{
    public const int LogCapacity = 400;

    internal readonly object LogLock = new();
    internal readonly Queue<string> Logs = new();

    private BacktestProgressState _progress = new(0m, 0, 0, null, 0, "Starting runner", DateTime.UtcNow);

    /// <summary>Captured at attach time so it stays readable after the process exits.</summary>
    public int ProcessId { get; internal set; }

    public DateTime? LastLogUtc { get; internal set; }

    /// <summary>Last stderr line, for the "Runner exited" reason.</summary>
    public string? LastStderrLine { get; internal set; }

    /// <summary>Last stdout line; "[RUNNER] stopping: SIGTERM" tells a signalled exit from a silent one.</summary>
    public string? LastStdoutLine { get; internal set; }

    /// <summary>Mutable progress; see <see cref="UpdateProgress"/>.</summary>
    public BacktestProgressState Progress => Volatile.Read(ref _progress);

    public void UpdateProgress(decimal percent, long barsProcessed, long totalBars, DateTime? currentUtc, int trades, string? message)
    {
        var clamped = Math.Clamp(percent, 0m, 100m);
        Volatile.Write(ref _progress, new BacktestProgressState(
            clamped, Math.Max(0, barsProcessed), Math.Max(0, totalBars), currentUtc, Math.Max(0, trades), message, DateTime.UtcNow));
    }

    /// <summary>The exit monitor task, so a deliberate stop can wait for it before disposing the process.</summary>
    internal Task? ExitMonitor { get; set; }

    /// <summary>
    /// Completed once whoever owns the stop has finished its bookkeeping.
    /// Concurrent stoppers await this instead of closing the run twice.
    /// </summary>
    internal readonly TaskCompletionSource<bool> StopCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0 = running, 1 = a stop (deliberate or runner exit) has been claimed.
    private int _stopState;

    public bool StopRequested => Volatile.Read(ref _stopState) != 0;

    /// <summary>
    /// Atomically claims the stop. Exactly one caller gets <c>true</c>; the
    /// other (UI stop racing the exit monitor) waits on <see cref="StopCompletion"/>.
    /// </summary>
    internal bool TryClaimStop() => Interlocked.CompareExchange(ref _stopState, 1, 0) == 0;
}

/// <summary>
/// In-process registry of running backtest processes, keyed by run id. Mirrors
/// <see cref="StrategyProcessRegistry"/>: output is drained through
/// OutputDataReceived/ErrorDataReceived (never ReadToEnd) and an exit monitor
/// closes the run when the process dies on its own. Lost on API restart — the
/// database (SimulationRun + signals) carries what needs to survive.
/// </summary>
public sealed class BacktestProcessRegistry
{
    private readonly ConcurrentDictionary<long, RunningBacktest> _running = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestProcessRegistry> _logger;

    public BacktestProcessRegistry(IServiceScopeFactory scopeFactory, ILogger<BacktestProcessRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Serializes start attempts so the concurrency cap cannot be overrun by overlapping POSTs.</summary>
    public object StartLock { get; } = new();

    public int Count => _running.Count;

    public bool Contains(long runId) => _running.ContainsKey(runId);

    public RunningBacktest? Get(long runId)
        => _running.TryGetValue(runId, out var entry) ? entry : null;

    public IReadOnlyList<RunningBacktest> List()
        => _running.Values.OrderBy(x => x.StartedUtc).ToList();

    // Output of runs that already exited, so the run page can still show why a
    // backtest failed or what it fetched. Bounded: the newest FinishedLogCapacity
    // runs only.
    private const int FinishedLogCapacity = 30;
    private readonly ConcurrentDictionary<long, string[]> _finishedLogs = new();
    private readonly ConcurrentQueue<long> _finishedOrder = new();

    public bool Remove(long runId)
    {
        if (!_running.TryRemove(runId, out var entry))
        {
            return false;
        }

        string[] snapshot;
        lock (entry.LogLock)
        {
            snapshot = entry.Logs.ToArray();
        }

        _finishedLogs[runId] = snapshot;
        _finishedOrder.Enqueue(runId);
        while (_finishedOrder.Count > FinishedLogCapacity && _finishedOrder.TryDequeue(out var oldest))
        {
            _finishedLogs.TryRemove(oldest, out _);
        }

        return true;
    }

    /// <summary>
    /// Registers a freshly started process: wires stdout/stderr draining (the
    /// pipes MUST be read or the Python process blocks on its next print) and an
    /// exit monitor that closes the run when the process dies on its own.
    /// </summary>
    public bool TryAdd(RunningBacktest entry)
    {
        if (!_running.TryAdd(entry.RunId, entry))
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
            _logger.LogWarning(ex, "Could not read process id for backtest run {RunId}.", entry.RunId);
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            entry.LastStdoutLine = e.Data;
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | {e.Data}");
            _logger.LogInformation("[backtest:{RunId}] {Line}", entry.RunId, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            entry.LastStderrLine = e.Data;
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
            _logger.LogWarning("[backtest:{RunId}:err] {Line}", entry.RunId, e.Data);
        };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to begin draining output for backtest run {RunId}.", entry.RunId);
        }

        Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | backtest runner started (pid {entry.ProcessId}, run {entry.RunId}, {entry.StrategyName} on {entry.Underlying} @{entry.Resolution} x{entry.Lots})");

        entry.ExitMonitor = Task.Run(() => MonitorExitAsync(entry));
        return true;
    }

    public void AppendLog(long runId, string line)
    {
        if (_running.TryGetValue(runId, out var entry))
        {
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | {line}");
        }
    }

    /// <summary>The most recent <paramref name="take"/> lines, oldest first.</summary>
    public IReadOnlyList<string> GetLogs(long runId, int take)
    {
        int count = Math.Clamp(take, 1, RunningBacktest.LogCapacity);

        if (_running.TryGetValue(runId, out var entry))
        {
            lock (entry.LogLock)
            {
                var lines = entry.Logs.ToArray();
                return lines.Skip(Math.Max(0, lines.Length - count)).ToList();
            }
        }

        if (_finishedLogs.TryGetValue(runId, out var finished))
        {
            return finished.Skip(Math.Max(0, finished.Length - count)).ToList();
        }

        return Array.Empty<string>();
    }

    /// <summary>Stores the runner's progress report. False when the run is not in the registry.</summary>
    public bool UpdateProgress(long runId, decimal percent, long barsProcessed, long totalBars, DateTime? currentUtc, int trades, string? message)
    {
        if (!_running.TryGetValue(runId, out var entry)) return false;
        entry.UpdateProgress(percent, barsProcessed, totalBars, currentUtc, trades, message);
        return true;
    }

    private static void Append(RunningBacktest entry, string line)
    {
        lock (entry.LogLock)
        {
            entry.Logs.Enqueue(line);
            while (entry.Logs.Count > RunningBacktest.LogCapacity)
            {
                entry.Logs.Dequeue();
            }
            entry.LastLogUtc = DateTime.UtcNow;
        }
    }

    private async Task MonitorExitAsync(RunningBacktest entry)
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
            _logger.LogWarning(ex, "Exit monitor for backtest run {RunId} failed.", entry.RunId);
        }

        Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | runner exited with code {exitCode}");

        // A deliberate stop (BacktestRunControl) owns the bookkeeping and disposes
        // the process once it is done with it; the monitor only reports the exit.
        if (!entry.TryClaimStop())
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<BacktestRunControl>();
            await control.HandleExitAsync(entry, exitCode, entry.LastStderrLine);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finish exit of backtest run {RunId}.", entry.RunId);

            // Never leave the registry pointing at a dead process.
            Remove(entry.RunId);
            entry.StopCompletion.TrySetResult(true);
            try { process.Dispose(); } catch { /* already gone */ }
        }
    }
}
