// src/AlgoTrading.Api/Services/StrategyProcessRegistry.cs
using AlgoTrading.Contracts.Strategies;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The mutable, process-lifetime part of a registry entry: output ring buffer,
/// recent signals, trailing-stop peaks, exit monitor and the stop claim. Kept
/// in one object that a <c>with</c> copy of <see cref="RunningStrategy"/>
/// shares by reference, so replacing the entry (e.g. new risk rules) never
/// forks its logs or lets a second stopper claim the same run.
/// </summary>
internal sealed class RunSharedState
{
    public readonly object LogLock = new();
    public readonly Queue<string> Logs = new();
    public readonly object SignalLock = new();
    public readonly List<object> Signals = new();

    /// <summary>Trailing-stop peaks of the run, its groups and its open legs. Never persisted.</summary>
    public readonly RiskTrailState Trail = new();

    public readonly TaskCompletionSource<int> StopCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 0 = running, 1 = a stop (deliberate or runner exit) has been claimed.
    public int StopState;

    public int ProcessId;
    public DateTime? LastLogUtc;
    public string? LastStderrLine;
    public Task? ExitMonitor;
}

/// <summary>
/// A strategy runner process the API launched (or adopted after a restart),
/// with the run configuration it runs with and ring buffers of its recent
/// output and signals. The positional part is immutable configuration; a
/// changed rule set produces a <c>with</c> copy that shares the live state.
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
    RiskRulesDto Risk)
{
    public const int LogCapacity = 300;
    public const int SignalCapacity = 100;

    /// <summary>Overall rupee stop-loss — the shorthand the list view shows.</summary>
    public decimal? StopLoss => Risk.OverallStopLoss;

    /// <summary>Overall rupee target — the shorthand the list view shows.</summary>
    public decimal? Target => Risk.OverallTarget;

    /// <summary>
    /// True when the process was found alive after an API restart and taken
    /// over by pid: no stdout/stderr pipes, so its output is not captured.
    /// </summary>
    public bool Adopted { get; init; }

    internal RunSharedState Shared { get; } = new();

    internal object LogLock => Shared.LogLock;
    internal Queue<string> Logs => Shared.Logs;
    internal object SignalLock => Shared.SignalLock;
    internal List<object> Signals => Shared.Signals;

    /// <summary>
    /// Live trailing-stop peaks the risk guard keeps for this run. In memory
    /// only: a run adopted after an API restart starts with empty peaks, so its
    /// trails re-arm from the P&amp;L of the guard's next sweep.
    /// </summary>
    internal RiskTrailState Trail => Shared.Trail;

    /// <summary>Captured at attach time so it stays readable after the process exits.</summary>
    public int ProcessId
    {
        get => Volatile.Read(ref Shared.ProcessId);
        internal set => Volatile.Write(ref Shared.ProcessId, value);
    }

    public DateTime? LastLogUtc
    {
        get { lock (Shared.LogLock) return Shared.LastLogUtc; }
        internal set { lock (Shared.LogLock) Shared.LastLogUtc = value; }
    }

    /// <summary>Last stderr line, for the "Runner exited" reason.</summary>
    public string? LastStderrLine
    {
        get => Volatile.Read(ref Shared.LastStderrLine);
        internal set => Volatile.Write(ref Shared.LastStderrLine, value);
    }

    /// <summary>The exit monitor task, so a deliberate stop can wait for it before disposing the process.</summary>
    internal Task? ExitMonitor
    {
        get => Volatile.Read(ref Shared.ExitMonitor);
        set => Volatile.Write(ref Shared.ExitMonitor, value);
    }

    /// <summary>
    /// Completed (with the number of positions flattened) once whoever owns the
    /// stop has finished its bookkeeping. Concurrent stoppers await this instead
    /// of flattening a second time.
    /// </summary>
    internal TaskCompletionSource<int> StopCompletion => Shared.StopCompletion;

    /// <summary>
    /// True once a stop has been claimed, whether by StrategyRunControl or by the
    /// exit monitor. Only the claimant does the flatten/persist bookkeeping.
    /// </summary>
    public bool StopRequested => Volatile.Read(ref Shared.StopState) != 0;

    /// <summary>
    /// Atomically claims the stop. Exactly one caller gets <c>true</c>; every
    /// other stopper (UI stop racing the risk guard, market close racing both,
    /// the exit monitor racing any of them) gets <c>false</c> and must wait on
    /// <see cref="StopCompletion"/> rather than flatten again.
    /// </summary>
    internal bool TryClaimStop() => Interlocked.CompareExchange(ref Shared.StopState, 1, 0) == 0;
}

/// <summary>
/// Why and when a run of a strategy ended, kept in memory after the entry is
/// removed so the list can show "Stopped · reason" until it is dismissed or
/// pushed out by newer exits.
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
    DateTime StartedUtc,
    RiskRulesDto Risk);

/// <summary>
/// In-process registry of running strategy processes, keyed by run id
/// (SimulationRun.Id). The same strategy may run on several underlyings at
/// once; each run is its own entry with its own logs, signals and stop claim.
///
/// The registry itself is lost when the API restarts; the pid of every runner
/// is persisted (system_settings) so <see cref="LiveRunStartupReconciler"/>
/// can adopt the ones still alive and close the rest.
/// </summary>
public sealed class StrategyProcessRegistry
{
    /// <summary>Exits remembered per strategy, newest first.</summary>
    public const int ExitsPerStrategy = 5;

    /// <summary>The single log line an adopted entry starts with.</summary>
    public const string AdoptedLogLine = "adopted after API restart — output not captured";

    // Output of runs that already exited, so a run page can still show what the
    // runner printed. Bounded: the newest FinishedLogCapacity runs only. Both
    // structures live under _finishedLock and the order list holds each run id
    // at most once (oldest first), so a redeployed run id — a stopped wizard run
    // can be deployed again under the same SimulationRun.Id — is never evicted
    // by a stale earlier position of itself.
    private const int FinishedLogCapacity = 30;

    private readonly ConcurrentDictionary<long, RunningStrategy> _running = new();
    private readonly ConcurrentDictionary<int, List<LastExit>> _lastExits = new();
    private readonly object _finishedLock = new();
    private readonly Dictionary<long, string[]> _finishedLogs = new();
    private readonly LinkedList<long> _finishedOrder = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StrategyProcessRegistry> _logger;

    public StrategyProcessRegistry(IServiceScopeFactory scopeFactory, ILogger<StrategyProcessRegistry> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Serializes start attempts so two overlapping POSTs cannot both spawn a
    /// process for the same strategy + underlying (the second would be untracked)
    /// or overrun the concurrency cap.
    /// </summary>
    public object StartLock { get; } = new();

    public int Count => _running.Count;

    public bool Contains(long runId) => _running.ContainsKey(runId);

    /// <summary>
    /// Registers a freshly started process: wires stdout/stderr draining (the
    /// pipes MUST be read or the Python process blocks on its next print) and an
    /// exit monitor that records "Runner exited" when the process dies on its own.
    /// An adopted entry has no pipes; it only gets the exit monitor.
    /// </summary>
    public bool TryAdd(RunningStrategy entry)
    {
        if (!_running.TryAdd(entry.RunId, entry))
        {
            return false;
        }

        // A restarted run id must not serve the previous run's output, and its
        // old place in the eviction order goes with it.
        ForgetFinished(entry.RunId);

        var process = entry.Process;
        try
        {
            entry.ProcessId = process.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read process id for strategy {StrategyId} run {RunId}.", entry.StrategyId, entry.RunId);
        }

        if (entry.Adopted)
        {
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | {AdoptedLogLine} (pid {entry.ProcessId}, run {entry.RunId}, {entry.Underlying} x{entry.Lots})");
            if (entry.Risk.HasAnyTrailingRule)
            {
                // The peaks lived in the previous API process; this entry starts
                // with none, so say so rather than let the trail look continuous.
                Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | trailing stops re-arm from the current P&L (peaks were lost with the API restart)");
            }
            entry.ExitMonitor = Task.Run(() => MonitorExitAsync(entry));
            return true;
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | {e.Data}");
            _logger.LogInformation("[strategy:{Name}:{Underlying}] {Line}", entry.Name, entry.Underlying, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            entry.LastStderrLine = e.Data;
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} ! {e.Data}");
            _logger.LogWarning("[strategy:{Name}:{Underlying}:err] {Line}", entry.Name, entry.Underlying, e.Data);
        };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to begin draining output for strategy {StrategyId} run {RunId}.", entry.StrategyId, entry.RunId);
        }

        Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | runner started (pid {entry.ProcessId}, run {entry.RunId}, {entry.Underlying} x{entry.Lots}, {entry.Risk.Describe()})");

        entry.ExitMonitor = Task.Run(() => MonitorExitAsync(entry));
        return true;
    }

    public RunningStrategy? Get(long runId)
        => _running.TryGetValue(runId, out var entry) ? entry : null;

    /// <summary>
    /// Replaces the run's risk rules atomically (a <c>with</c> copy that shares
    /// the entry's live state) so the next guard sweep enforces the new rules.
    /// Changing any trailing value resets the trailing peaks, so every trail
    /// re-arms from the P&amp;L of the next sweep instead of carrying a peak that
    /// belonged to a different rule. Returns the new entry, or null when the run
    /// is not running.
    /// </summary>
    public RunningStrategy? UpdateRisk(long runId, RiskRulesDto rules)
    {
        while (true)
        {
            if (!_running.TryGetValue(runId, out var current))
            {
                return null;
            }

            bool trailChanged = !RiskRulesDto.SameTrailing(current.Risk, rules);

            var updated = current with { Risk = rules };
            if (_running.TryUpdate(runId, updated, current))
            {
                if (trailChanged)
                {
                    updated.Trail.Reset();
                    if (rules.HasAnyTrailingRule)
                    {
                        Append(updated, $"{DateTime.UtcNow:HH:mm:ss} | trailing stops re-arm from the current P&L (rules changed)");
                    }
                }
                return updated;
            }
        }
    }

    /// <summary>Every active run of the strategy, oldest first.</summary>
    public IReadOnlyList<RunningStrategy> GetByStrategy(int strategyId)
        => _running.Values
            .Where(x => x.StrategyId == strategyId)
            .OrderBy(x => x.StartedUtc)
            .ThenBy(x => x.RunId)
            .ToList();

    /// <summary>The active run of the strategy on the underlying, if any (case-insensitive).</summary>
    public RunningStrategy? Find(int strategyId, string underlying)
        => _running.Values.FirstOrDefault(x =>
            x.StrategyId == strategyId
            && string.Equals(x.Underlying, underlying, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every active run, oldest first.</summary>
    public IReadOnlyList<RunningStrategy> List()
        => _running.Values.OrderBy(x => x.StartedUtc).ThenBy(x => x.RunId).ToList();

    /// <summary>
    /// Removes the entry and keeps a snapshot of its output so the run's logs
    /// stay readable after it has finished.
    /// </summary>
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

        lock (_finishedLock)
        {
            // Re-adding moves the run to the newest position instead of leaving
            // a duplicate behind that would evict this snapshot early.
            _finishedOrder.Remove(runId);
            _finishedOrder.AddLast(runId);
            _finishedLogs[runId] = snapshot;
            while (_finishedOrder.Count > FinishedLogCapacity)
            {
                var oldest = _finishedOrder.First!.Value;
                _finishedOrder.RemoveFirst();
                _finishedLogs.Remove(oldest);
            }
        }

        return true;
    }

    /// <summary>Drops the retained output (and eviction slot) of a finished run.</summary>
    private void ForgetFinished(long runId)
    {
        lock (_finishedLock)
        {
            _finishedLogs.Remove(runId);
            _finishedOrder.Remove(runId);
        }
    }

    /// <summary>
    /// Snapshots the running entry as one of its strategy's recent exits
    /// (newest first, at most <see cref="ExitsPerStrategy"/>). A second record
    /// for the same run replaces the first. The rules recorded are the run's
    /// CURRENT ones (the caller may hold a copy from before a risk update).
    /// </summary>
    public void RecordExit(RunningStrategy entry, string reason)
    {
        var current = Get(entry.RunId) ?? entry;

        var exit = new LastExit(
            entry.StrategyId, entry.Name, entry.RunId, reason, DateTime.UtcNow,
            entry.Underlying, entry.SpotSymbol, entry.Lots, current.StopLoss, current.Target,
            entry.StartedBy, entry.StartedUtc, current.Risk);

        var exits = _lastExits.GetOrAdd(entry.StrategyId, _ => new List<LastExit>());
        lock (exits)
        {
            exits.RemoveAll(x => x.RunId == entry.RunId);
            exits.Insert(0, exit);
            if (exits.Count > ExitsPerStrategy)
            {
                exits.RemoveRange(ExitsPerStrategy, exits.Count - ExitsPerStrategy);
            }
        }
    }

    /// <summary>Recent exits of the strategy, newest first.</summary>
    public IReadOnlyList<LastExit> GetLastExits(int strategyId)
    {
        if (!_lastExits.TryGetValue(strategyId, out var exits)) return Array.Empty<LastExit>();

        lock (exits)
        {
            return exits.ToList();
        }
    }

    /// <summary>The newest exit of the strategy, if any.</summary>
    public LastExit? GetLastExit(int strategyId)
    {
        if (!_lastExits.TryGetValue(strategyId, out var exits)) return null;

        lock (exits)
        {
            return exits.Count > 0 ? exits[0] : null;
        }
    }

    /// <summary>The remembered exit of one run, if it is still kept.</summary>
    public LastExit? GetExitByRun(long runId)
    {
        foreach (var exits in _lastExits.Values)
        {
            lock (exits)
            {
                var match = exits.FirstOrDefault(x => x.RunId == runId);
                if (match is not null) return match;
            }
        }
        return null;
    }

    /// <summary>
    /// Forgets the exit of one run (<paramref name="runId"/> given) or every
    /// remembered exit of the strategy (null).
    /// </summary>
    public void ClearLastExit(int strategyId, long? runId = null)
    {
        if (runId is null)
        {
            _lastExits.TryRemove(strategyId, out _);
            return;
        }

        if (!_lastExits.TryGetValue(strategyId, out var exits)) return;

        lock (exits)
        {
            exits.RemoveAll(x => x.RunId == runId.Value);
        }
    }

    public void AppendLog(long runId, string line)
    {
        if (_running.TryGetValue(runId, out var entry))
        {
            Append(entry, $"{DateTime.UtcNow:HH:mm:ss} | {line}");
        }
    }

    /// <summary>
    /// The most recent <paramref name="take"/> lines, oldest first — from the
    /// live entry, or from the retained snapshot of a finished run.
    /// </summary>
    public IReadOnlyList<string> GetLogs(long runId, int take)
    {
        int count = Math.Clamp(take, 1, RunningStrategy.LogCapacity);

        if (_running.TryGetValue(runId, out var entry))
        {
            lock (entry.LogLock)
            {
                var lines = entry.Logs.ToArray();
                return lines.Skip(Math.Max(0, lines.Length - count)).ToList();
            }
        }

        lock (_finishedLock)
        {
            if (_finishedLogs.TryGetValue(runId, out var finished))
            {
                return finished.Skip(Math.Max(0, finished.Length - count)).ToList();
            }
        }

        return Array.Empty<string>();
    }

    public bool AddSignal(long runId, object signal)
    {
        if (!_running.TryGetValue(runId, out var entry)) return false;

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
    public IReadOnlyList<object> GetSignals(long runId)
    {
        if (!_running.TryGetValue(runId, out var entry)) return Array.Empty<object>();

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
            entry.Shared.LastLogUtc = DateTime.UtcNow;
        }
    }

    private async Task MonitorExitAsync(RunningStrategy entry)
    {
        var process = entry.Process;
        int exitCode = -1;
        bool exitCodeKnown = false;

        try
        {
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
            exitCodeKnown = true;
        }
        catch (Exception ex)
        {
            // An adopted (non-child) process reports no exit code on Unix; that
            // is expected, not a monitor failure.
            if (entry.Adopted)
            {
                _logger.LogDebug(ex, "Exit code of adopted strategy run {RunId} is not available.", entry.RunId);
            }
            else
            {
                _logger.LogWarning(ex, "Exit monitor for strategy {StrategyId} run {RunId} failed.", entry.StrategyId, entry.RunId);
            }
        }

        Append(entry, exitCodeKnown
            ? $"{DateTime.UtcNow:HH:mm:ss} | runner exited with code {exitCode}"
            : $"{DateTime.UtcNow:HH:mm:ss} | runner exited (exit code unknown)");

        // A deliberate stop (StrategyRunControl) owns the bookkeeping and disposes
        // the process once it is done with it; the monitor only reports the exit.
        if (!entry.TryClaimStop())
        {
            return;
        }

        var reason = exitCodeKnown
            ? $"Runner exited (code {exitCode})"
            : entry.Adopted
                ? "Runner exited (adopted after API restart; exit code unknown)"
                : "Runner exited (exit code unknown)";
        if (exitCodeKnown && exitCode != 0 && !string.IsNullOrWhiteSpace(entry.LastStderrLine))
        {
            reason += $": {entry.LastStderrLine.Trim()}";
        }

        _logger.LogWarning("Strategy {StrategyId} ({Name}) run {RunId} on {Underlying} exited on its own: {Reason}",
            entry.StrategyId, entry.Name, entry.RunId, entry.Underlying, reason);

        // The runner died with its positions still open: nothing else guards them
        // (the risk guard only watches registry entries), so square them off here
        // before the run is closed, exactly as a stop would.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
            await control.HandleRunnerExitAsync(entry, reason, exitCodeKnown && exitCode == 0 ? null : reason);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finish exit of strategy {StrategyId} run {RunId}.", entry.StrategyId, entry.RunId);

            // Never leave the registry pointing at a dead process.
            RecordExit(entry, reason);
            Remove(entry.RunId);
            entry.StopCompletion.TrySetResult(0);
            try { process.Dispose(); } catch { /* already gone */ }
        }
    }
}
