// src/AlgoTrading.Api/Services/BacktestRunControl.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
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
    private readonly BacktestProcessRegistry _registry;
    private readonly ILogger<BacktestRunControl> _logger;

    public BacktestRunControl(
        TradingDbContext dbContext,
        IPaperTradingService paperTradingService,
        BacktestProcessRegistry registry,
        ILogger<BacktestRunControl> logger)
    {
        _dbContext = dbContext;
        _paperTradingService = paperTradingService;
        _registry = registry;
        _logger = logger;
    }

    public sealed record StopResult(bool WasRunning, int Flattened);

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
    /// Closes every OfflineReplay run left Running/Pending with no process
    /// behind it (there is none after a restart: the registry is in-memory).
    /// Called once at startup; returns the number of runs closed.
    /// </summary>
    public async Task<int> FailOrphanedRunsAsync(string reason, CancellationToken cancellationToken = default)
    {
        var orphans = await _dbContext.SimulationRuns
            .Where(x => x.Mode == OfflineReplayMode && (x.Status == RunStatusRunning || x.Status == RunStatusPending))
            .ToListAsync(cancellationToken);

        foreach (var run in orphans)
        {
            if (_registry.Contains(run.Id)) continue;

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
            _logger.LogWarning("Backtest run {RunId} ({Strategy}) was {Status} with no runner process; marked Failed: {Reason}",
                run.Id, run.StrategyName, run.Status, reason);
        }

        return orphans.Count;
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
                var signalled = entry.LastStdoutLine is not null
                                && entry.LastStdoutLine.Contains(RunnerSignalMarker, StringComparison.Ordinal);
                if (signalled)
                {
                    var signalName = entry.LastStdoutLine![(entry.LastStdoutLine.IndexOf(RunnerSignalMarker, StringComparison.Ordinal) + RunnerSignalMarker.Length)..].Trim();
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
