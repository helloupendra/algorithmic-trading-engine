// src/AlgoTrading.Api/Services/BacktestRunControl.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Ends backtest runs the same way from every trigger (UI stop, runner exit):
/// stop the runner, square off whatever it left open at the last stored mark
/// (so a stopped run still reads as a closed ledger), set the run status and
/// persist a RUN_STOPPED signal so the reason survives an API restart.
/// Scoped because it needs the DbContext. The stop is claimed atomically on
/// the registry entry, so a UI stop racing the exit monitor waits for the owner.
/// </summary>
public sealed class BacktestRunControl
{
    public const string RunStoppedSignalType = StrategyRunControl.RunStoppedSignalType;
    public const string RunStatusPending = "Pending";
    public const string RunStatusRunning = "Running";
    public const string RunStatusStopped = "Stopped";
    public const string RunStatusCompleted = "Completed";
    public const string RunStatusFailed = "Failed";
    public const string OfflineReplayMode = "OfflineReplay";

    /// <summary>What the runner prints right before it exits on SIGTERM/SIGINT.</summary>
    public const string RunnerSignalMarker = "[RUNNER] stopping:";

    private static readonly TimeSpan MonitorSettleTimeout = TimeSpan.FromSeconds(2);

    private readonly TradingDbContext _dbContext;
    private readonly IPaperTradingService _paperTradingService;
    private readonly IProcessSettingsStore _processSettings;
    private readonly BacktestProcessRegistry _registry;
    private readonly IProcessProbe _probe;
    private readonly ILogger<BacktestRunControl> _logger;

    public BacktestRunControl(
        TradingDbContext dbContext,
        IPaperTradingService paperTradingService,
        IProcessSettingsStore processSettings,
        BacktestProcessRegistry registry,
        IProcessProbe probe,
        ILogger<BacktestRunControl> logger)
    {
        _dbContext = dbContext;
        _paperTradingService = paperTradingService;
        _processSettings = processSettings;
        _registry = registry;
        _probe = probe;
        _logger = logger;
    }

    /// <summary>
    /// The waits before a runner that could not be verified is probed again at
    /// startup: 2, 4 and 8 seconds, as for a live run's runner.
    /// </summary>
    public IReadOnlyList<TimeSpan> UnknownProbeRetryDelays { get; init; } =
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };

    public sealed record StopResult(bool WasRunning, int Flattened);

    /// <param name="Unverified">Runs whose runner could be neither confirmed nor ruled out: left as they were.</param>
    public sealed record ReconcileResult(int Adopted, int Closed, int Unverified = 0);

    public static bool IsOpenStatus(string? status) => status is RunStatusRunning or RunStatusPending;

    /// <summary>
    /// Stops one running backtest: SIGTERM then kill, mark the run Stopped
    /// (+ CompletedUtc) and persist RUN_STOPPED { reason, by }. A run whose row
    /// is still Running/Pending but has no runner process behind it (the API
    /// restarted, or the exit monitor failed) is closed the same way without a
    /// process to kill, so it never stays stuck. The cancellation token is
    /// deliberately not propagated: a client that disconnects after the stop
    /// has been claimed must not leave the run half-stopped.
    /// </summary>
    public async Task<StopResult> StopAsync(long runId, string reason, string by, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = _registry.Get(runId);
        if (entry is null)
        {
            return await StopOrphanAsync(runId, reason, by);
        }

        if (!entry.TryClaimStop())
        {
            _logger.LogInformation("Stop of backtest run {RunId} already in progress; waiting for it ({Reason}).", runId, reason);
            await entry.StopCompletion.Task.ConfigureAwait(false);
            return new StopResult(true, 0);
        }

        return await FinishAsync(entry, reason, by, RunStatusStopped, lastError: null, runnerAlreadyExited: false);
    }

    /// <summary>
    /// Called by the exit monitor after it has claimed the stop for a runner that
    /// exited on its own. Exit code 0 with the run already closed through
    /// /complete keeps that verdict; exit code 0 while the run is still open
    /// means the runner never reported completion (an external SIGTERM/SIGINT,
    /// or a crash on the way out) and is recorded as Stopped or Failed, never
    /// as a fake Completed. Non-zero: Failed with LastError = the last stderr
    /// line, plus a RUN_STOPPED signal.
    /// </summary>
    public Task<StopResult> HandleExitAsync(RunningBacktest entry, int exitCode, string? lastStderr)
    {
        if (exitCode == 0)
        {
            return FinishAsync(entry, "Runner exited (code 0)", by: "runner", RunStatusCompleted, lastError: null, runnerAlreadyExited: true);
        }

        var error = string.IsNullOrWhiteSpace(lastStderr)
            ? $"Backtest runner exited (code {exitCode})"
            : lastStderr.Trim();

        return FinishAsync(entry, $"Runner exited (code {exitCode}): {error}", by: "runner", RunStatusFailed, lastError: error, runnerAlreadyExited: true);
    }

    /// <summary>
    /// Records a runner's pid durably so a restarted API can adopt or stop it.
    /// Best effort: a failure is logged and never fails the start.
    /// </summary>
    public async Task RecordRunnerPidAsync(long runId, int processId, string? by)
    {
        if (processId <= 0) return;
        try
        {
            await _processSettings.SetPidAsync(SystemSettingKeys.BacktestRunPid(runId), processId, by, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the runner pid {Pid} of backtest run {RunId}.", processId, runId);
        }
    }

    /// <summary>
    /// Once at startup: every OfflineReplay run left Running/Pending with no
    /// registry entry is either ADOPTED (its stored pid is alive and is a
    /// backtest_runner for that run — the entry is rebuilt from the row and its
    /// exit monitor attached), squared off at last mark and marked Failed with
    /// <paramref name="reason"/> when its runner is known to be gone — or, when
    /// its pid could not be verified either way, LEFT exactly as it is.
    /// </summary>
    /// <remarks>
    /// "Could not verify" is not "gone", for a backtest runner as for a live
    /// one (<see cref="StrategyRunControl.ReconcileOrphanedRunsAsync"/>, 28 Sep).
    /// Until then a runner whose command line could not be read at that moment
    /// (ps timing out on a loaded box) had its run failed and squared off while
    /// the replay was still writing to it. Such a pid is probed again after 2,
    /// 4 and 8 seconds, all of them together; one still unverified keeps its row
    /// and its pid. A runner that is the run's own finishes the replay and
    /// closes the row through /complete (a refused progress report only warns
    /// it); one that is not is probed again at the next start, and the Stop
    /// button closes the row meanwhile.
    /// </remarks>
    public async Task<ReconcileResult> ReconcileOrphanedRunsAsync(string reason, CancellationToken cancellationToken = default)
    {
        var orphans = await _dbContext.SimulationRuns
            .Where(x => x.Mode == OfflineReplayMode && (x.Status == RunStatusRunning || x.Status == RunStatusPending))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        int adopted = 0, closed = 0;
        var unverified = new List<SimulationRun>();

        foreach (var run in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_registry.Contains(run.Id)) continue;

            switch (await ReconcileOneAsync(run, reason, cancellationToken))
            {
                case Adoption.Adopted: adopted++; break;
                case Adoption.Closed: closed++; break;
                case Adoption.Unverified: unverified.Add(run); break;
            }
        }

        // Probed again together, so a box that cannot read command lines costs
        // the startup 14 seconds, not 14 per run.
        foreach (var delay in UnknownProbeRetryDelays)
        {
            if (unverified.Count == 0) break;

            _logger.LogWarning("Could not verify the runner of {Count} backtest run(s) ({RunIds}); probing again in {Seconds}s.",
                unverified.Count, string.Join(", ", unverified.Select(x => x.Id)), delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);

            var still = new List<SimulationRun>();
            foreach (var run in unverified)
            {
                // The runner may have finished the replay meanwhile, closed its
                // row through /complete and exited: a probe now reads "gone",
                // and failing the row would overwrite its real verdict.
                await _dbContext.Entry(run).ReloadAsync(cancellationToken);
                if (!IsOpenStatus(run.Status) || _registry.Contains(run.Id)) continue;

                switch (await ReconcileOneAsync(run, reason, cancellationToken))
                {
                    case Adoption.Adopted: adopted++; break;
                    case Adoption.Closed: closed++; break;
                    case Adoption.Unverified: still.Add(run); break;
                }
            }
            unverified = still;
        }

        foreach (var run in unverified)
        {
            var pid = await _processSettings.GetPidAsync(SystemSettingKeys.BacktestRunPid(run.Id), cancellationToken);
            _logger.LogError(
                "Backtest run {RunId} ({Strategy}) is {Status} with runner pid {Pid} alive, but its command line could not be read "
                + "after {Tries} tries; it was neither adopted nor failed. If that pid is its runner, the run closes itself when the "
                + "replay ends; if not, stop the run from its page.",
                run.Id, run.StrategyName, run.Status, pid, UnknownProbeRetryDelays.Count + 1);
        }

        return new ReconcileResult(adopted, closed, unverified.Count);
    }

    /// <summary>What reconciling one run came to.</summary>
    private enum Adoption
    {
        /// <summary>Its runner is alive and verified: the run is in the registry again.</summary>
        Adopted,

        /// <summary>Its runner is gone: the run was squared off and marked Failed.</summary>
        Closed,

        /// <summary>Its runner could not be verified either way: nothing was touched.</summary>
        Unverified,

        /// <summary>Something else registered it meanwhile: nothing to do.</summary>
        Skipped
    }

    private async Task<Adoption> ReconcileOneAsync(SimulationRun run, string reason, CancellationToken cancellationToken)
    {
        Adoption outcome;
        try
        {
            outcome = await TryAdoptAsync(run, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adoption of backtest run {RunId} failed; marking it Failed instead.", run.Id);
            outcome = Adoption.Closed;
        }

        if (outcome != Adoption.Closed) return outcome;

        try
        {
            int flattened = await _paperTradingService.FlattenRunAsync(run.Id, reason, cancellationToken);
            if (flattened > 0)
            {
                _logger.LogInformation("Squared off {Count} open position(s) of orphaned backtest run {RunId} at last mark.", flattened, run.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flatten failed for orphaned backtest run {RunId}.", run.Id);
        }

        await RecordRunEndedAsync(run, run.StrategyName, RunStatusFailed, reason, by: "api", lastError: reason);
        await ClearRunnerPidAsync(run.Id);
        _logger.LogWarning("Backtest run {RunId} ({Strategy}) was left open with no runner process; marked Failed: {Reason}",
            run.Id, run.StrategyName, reason);
        return Adoption.Closed;
    }

    /// <summary>
    /// Adopts the run when its stored pid is a live backtest_runner for it.
    /// Returns <see cref="Adoption.Closed"/> for a run to close (no stored pid,
    /// or one that is gone or recycled) — the caller closes it — and
    /// <see cref="Adoption.Unverified"/> when the pid is alive but could not be
    /// told apart from a recycled one.
    /// </summary>
    private async Task<Adoption> TryAdoptAsync(SimulationRun run, CancellationToken cancellationToken)
    {
        var pid = await _processSettings.GetPidAsync(SystemSettingKeys.BacktestRunPid(run.Id), cancellationToken);
        if (pid is null)
        {
            return Adoption.Closed;
        }

        var probe = _probe.Probe(pid.Value, ProcessProbe.BacktestRunnerMarker, run.Id);
        if (probe.IsUnknown) return Adoption.Unverified;
        if (!probe.IsAlive || probe.Process is not { } process) return Adoption.Closed;

        var p = BacktestRunParameters.Parse(run.ParametersJson);
        var underlying = p.Underlying
                         ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                         ?? UnderlyingCatalog.InferUnderlying(run.Symbol);

        // As for live runs: the recorded starter, the owner only for rows
        // from before 28 Sep that recorded none.
        var startedBy = !string.IsNullOrWhiteSpace(run.StartedByName)
            ? run.StartedByName
            : await _dbContext.AppUsers.AsNoTracking()
                .Where(x => x.Id == run.UserId)
                .Select(x => x.UserName)
                .FirstOrDefaultAsync(cancellationToken) ?? "unknown";

        var entry = new RunningBacktest(
            run.Id,
            StrategyCatalogService.StableId(run.StrategyName),
            run.StrategyName,
            process,
            startedBy,
            run.UserId,
            run.StartedUtc ?? run.CreatedUtc,
            underlying,
            run.Symbol,
            Math.Max(1, p.Lots ?? 1),
            p.StopLoss,
            p.Target,
            ResolutionCodes.ToCandle(run.Resolution),
            run.FromUtc ?? DateTime.MinValue,
            run.ToUtc ?? DateTime.MinValue)
        {
            Adopted = true
        };

        if (!_registry.TryAdd(entry))
        {
            // Registered meanwhile: it has a runner, and failing it would square off under it.
            process.Dispose();
            return Adoption.Skipped;
        }

        _logger.LogWarning("Adopted backtest run {RunId} ({Strategy} on {Underlying}) pid {Pid} after API restart — output not captured.",
            run.Id, run.StrategyName, underlying, pid);
        return Adoption.Adopted;
    }

    /// <summary>
    /// A Running/Pending row with no registry entry: nothing to kill, but the
    /// row must still be closed (flatten at last mark, Stopped + RUN_STOPPED).
    /// </summary>
    private async Task<StopResult> StopOrphanAsync(long runId, string reason, string by)
    {
        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(x => x.Id == runId && x.Mode == OfflineReplayMode);
        if (run is null || !IsOpenStatus(run.Status))
        {
            return new StopResult(false, 0);
        }

        _logger.LogWarning("Backtest run {RunId} is {Status} but has no runner process; closing it without a process to stop.", runId, run.Status);

        int flattened = 0;
        try
        {
            flattened = await _paperTradingService.FlattenRunAsync(runId, reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flatten failed for orphaned backtest run {RunId}.", runId);
        }

        await RecordRunEndedAsync(run, run.StrategyName, RunStatusStopped, $"{reason} (runner process was not found)", by, lastError: null);
        await ClearRunnerPidAsync(runId);
        return new StopResult(true, flattened);
    }

    /// <summary>
    /// The single close pipeline, run only by whoever won the claim: stop the
    /// runner → flatten at last mark → set status → RUN_STOPPED → registry
    /// bookkeeping → dispose. Every step is isolated so a failure in one never
    /// skips the rest; whatever happens, the registry entry is removed, the
    /// process disposed and StopCompletion resolved, so a DB hiccup cannot leak
    /// a concurrency slot or leave the row un-stoppable.
    /// </summary>
    private async Task<StopResult> FinishAsync(
        RunningBacktest entry,
        string reason,
        string by,
        string finalStatus,
        string? lastError,
        bool runnerAlreadyExited)
    {
        long runId = entry.RunId;
        int flattened = 0;
        SimulationRun? run = null;
        bool runWasOpen = false;

        try
        {
            _registry.AppendLog(runId, $"stopping: {reason}");

            if (!runnerAlreadyExited)
            {
                try
                {
                    await ProcessTerminator.StopAsync(
                        entry.Process, entry.ProcessId, line => _registry.AppendLog(runId, line), _logger, $"backtest run {runId}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Terminating the runner of backtest run {RunId} failed.", runId);
                }
            }

            try
            {
                run = await _dbContext.SimulationRuns.FirstOrDefaultAsync(x => x.Id == runId);
                runWasOpen = run is not null && IsOpenStatus(run.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not load backtest run {RunId} while ending it.", runId);
                _registry.AppendLog(runId, $"could not load the run row: {ex.Message}");
            }

            // A clean exit is only a completion when the runner said so through
            // /complete. An open row at exit code 0 means it was cut short: by a
            // signal it logged (external SIGTERM/SIGINT) or by a silent death.
            if (runnerAlreadyExited && finalStatus == RunStatusCompleted && runWasOpen)
            {
                if (entry.LastStdoutLine is { } lastStdoutLine
                    && lastStdoutLine.IndexOf(RunnerSignalMarker, StringComparison.Ordinal) is var markerIndex
                    && markerIndex >= 0)
                {
                    var signalName = lastStdoutLine[(markerIndex + RunnerSignalMarker.Length)..].Trim();
                    finalStatus = RunStatusStopped;
                    reason = $"Runner stopped by {(signalName.Length > 0 ? signalName : "a signal")} before completing the replay";
                }
                else
                {
                    finalStatus = RunStatusFailed;
                    reason = "Runner exited before reporting completion";
                    lastError = reason;
                }
                _registry.AppendLog(runId, reason);
            }

            // Positions the runner left open are squared off at the last stored
            // bar-close mark (never today's LTP), so the ledger is complete and
            // nothing is silently left dangling.
            if (run is not null)
            {
                try
                {
                    flattened = await _paperTradingService.FlattenRunAsync(runId, reason, CancellationToken.None);
                    if (flattened > 0)
                    {
                        _registry.AppendLog(runId, $"squared off {flattened} open position(s) at last mark");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Flatten failed for backtest run {RunId}.", runId);
                    _registry.AppendLog(runId, $"flatten failed: {ex.Message}");
                }
            }

            try
            {
                // A run the runner already completed (or failed) through
                // /complete keeps that status: only an open run is closed here.
                // A Stop that lands after the final POST therefore never relabels
                // a fully replayed run as Stopped.
                if (runWasOpen)
                {
                    await RecordRunEndedAsync(run, entry.StrategyName, finalStatus, reason, by, lastError);
                }
                else if (run is not null)
                {
                    _registry.AppendLog(runId, $"runner already reported {run.Status}; keeping it");
                    _logger.LogInformation("Backtest run {RunId} already {Status} when the stop ran ({Reason}); status kept.", runId, run.Status, reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist end of backtest run {RunId}.", runId);
            }

            _logger.LogInformation("Backtest run {RunId} ({Name}) ended: {Reason} (by {By}, status {Status}, flattened {Flattened})",
                runId, entry.StrategyName, reason, by, runWasOpen ? finalStatus : run?.Status ?? finalStatus, flattened);

            return new StopResult(true, flattened);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ending backtest run {RunId} failed unexpectedly.", runId);
            await TryMarkFailedAsync(runId, entry.StrategyName, $"Ending the run failed: {ex.Message}");
            return new StopResult(true, flattened);
        }
        finally
        {
            _registry.Remove(runId);
            await ClearRunnerPidAsync(runId);
            await DisposeProcessAsync(entry, runnerAlreadyExited);
            entry.StopCompletion.TrySetResult(true);
        }
    }

    /// <summary>Best-effort Failed verdict for a run whose close pipeline blew up.</summary>
    private async Task TryMarkFailedAsync(long runId, string strategyName, string error)
    {
        try
        {
            var run = await _dbContext.SimulationRuns.FirstOrDefaultAsync(x => x.Id == runId);
            if (run is null || !IsOpenStatus(run.Status)) return;
            await RecordRunEndedAsync(run, strategyName, RunStatusFailed, error, by: "api", lastError: error);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not mark backtest run {RunId} failed after its close pipeline failed.", runId);
        }
    }

    /// <summary>Drops the persisted runner pid of a run that is closed (best effort).</summary>
    private async Task ClearRunnerPidAsync(long runId)
    {
        try
        {
            await _processSettings.DeleteAsync(SystemSettingKeys.BacktestRunPid(runId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the stored runner pid of backtest run {RunId}.", runId);
        }
    }

    /// <summary>
    /// Sets the final status and persists a RUN_STOPPED signal for Stopped and
    /// Failed endings. Idempotent on CompletedUtc.
    /// </summary>
    private async Task RecordRunEndedAsync(
        SimulationRun? run,
        string strategyName,
        string finalStatus,
        string reason,
        string by,
        string? lastError)
    {
        var now = DateTime.UtcNow;

        if (run is null)
        {
            _logger.LogWarning("Backtest run row missing while recording its end ({Reason}); nothing persisted.", reason);
            return;
        }

        run.Status = finalStatus;
        run.CompletedUtc ??= now;
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            run.LastError = lastError;
        }

        if (finalStatus != RunStatusCompleted)
        {
            var metadata = JsonSerializer.Serialize(new { reason, by });
            await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
            {
                SimulationRunId = run.Id,
                StrategyName = strategyName,
                SignalType = RunStoppedSignalType,
                TimestampUtc = now,
                GroupId = string.Empty,
                MetadataJson = metadata,
                CreatedUtc = now
            });
        }

        await _dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Releases the Process (stdout/stderr readers, exit-event registration)
    /// once the exit monitor has observed the exit, so neither side touches a
    /// disposed handle. On the runner-exit path the monitor IS the caller.
    /// </summary>
    private async Task DisposeProcessAsync(RunningBacktest entry, bool runnerAlreadyExited)
    {
        try
        {
            if (!runnerAlreadyExited && entry.ExitMonitor is { IsCompleted: false } monitor)
            {
                await Task.WhenAny(monitor, Task.Delay(MonitorSettleTimeout));
            }

            entry.Process.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the process of backtest run {RunId} failed.", entry.RunId);
        }
    }
}
