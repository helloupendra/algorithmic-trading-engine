// src/AlgoTrading.Api/Services/StrategyRunControl.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Stops strategy runs the same way from every trigger (UI stop, stop-loss /
/// target, market close, runner crash): stop the runner, square off open
/// positions, close the SimulationRun and persist a RUN_STOPPED signal so the
/// reason survives an API restart. Scoped because it needs the DbContext.
///
/// Ordering: the runner is stopped BEFORE the flatten and the run is marked
/// "Stopping" first, so a runner that is still consuming ticks cannot post an
/// OPEN_GROUP/CLOSE_GROUP into the window in which positions are being squared
/// off (which would leave an ownerless or reversed position on a stopped run).
/// The stop itself is claimed atomically on the registry entry, so concurrent
/// stoppers wait for the owner instead of flattening twice.
/// </summary>
public sealed class StrategyRunControl
{
    public const string RunStoppedSignalType = "RUN_STOPPED";
    public const string RunStatusRunning = "Running";
    public const string RunStatusStopping = "Stopping";
    public const string RunStatusStopped = "Stopped";
    public const string LivePaperMode = "LivePaper";

    /// <summary>Reason recorded for a Running row whose runner is gone after an API restart.</summary>
    public const string RestartReason = "API restarted; runner not found";

    private static readonly TimeSpan MonitorSettleTimeout = TimeSpan.FromSeconds(2);

    private readonly TradingDbContext _dbContext;
    private readonly IPaperTradingService _paperTradingService;
    private readonly IProcessSettingsStore _processSettings;
    private readonly StrategyProcessRegistry _registry;
    private readonly ILogger<StrategyRunControl> _logger;

    public StrategyRunControl(
        TradingDbContext dbContext,
        IPaperTradingService paperTradingService,
        IProcessSettingsStore processSettings,
        StrategyProcessRegistry registry,
        ILogger<StrategyRunControl> logger)
    {
        _dbContext = dbContext;
        _paperTradingService = paperTradingService;
        _processSettings = processSettings;
        _registry = registry;
        _logger = logger;
    }

    public sealed record StopResult(bool WasRunning, int Flattened);

    public sealed record ReconcileResult(int Adopted, int Closed);

    /// <summary>
    /// Stops one running strategy run (by SimulationRun id). <paramref name="by"/>
    /// is recorded in the RUN_STOPPED metadata (user name, "risk-guard",
    /// "market-hours"...). The cancellation token is deliberately NOT propagated
    /// into the stop pipeline: a client that disconnects (or a host that is
    /// shutting down) after the stop has been claimed must not leave the run
    /// half-stopped with open positions and a "Running" status.
    /// Returns WasRunning=false when the run is not in the registry — see
    /// <see cref="StopOrphanAsync"/> for a row left open with no process behind it.
    /// </summary>
    public async Task<StopResult> StopAsync(
        long runId,
        string reason,
        bool flatten,
        string by,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = _registry.Get(runId);
        if (entry is null)
        {
            return new StopResult(false, 0);
        }

        // Exactly one stopper owns the shutdown; everyone else shares its outcome.
        if (!entry.TryClaimStop())
        {
            _logger.LogInformation("Stop of run {RunId} ({Name} on {Underlying}) already in progress; waiting for it ({Reason}).",
                runId, entry.Name, entry.Underlying, reason);
            int flattenedByOwner = await entry.StopCompletion.Task.ConfigureAwait(false);
            return new StopResult(true, flattenedByOwner);
        }

        return await FinishStopAsync(entry, reason, by, lastError: null, flatten, runnerAlreadyExited: false);
    }

    /// <summary>
    /// A LivePaper run whose row is still Running/Stopping but has no registry
    /// entry (the API restarted, or the exit monitor failed): nothing to kill,
    /// but the row must still be closed — flatten at last mark (when asked),
    /// Stopped + RUN_STOPPED — so it never stays stuck. Returns WasRunning=false
    /// when the row is missing or already closed. With
    /// <paramref name="noteMissingRunner"/> the reason gets "(runner process was
    /// not found)" appended; pass false when the reason already says so.
    /// </summary>
    public async Task<StopResult> StopOrphanAsync(long runId, string reason, bool flatten, string by, bool noteMissingRunner = true)
    {
        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId && x.Mode == LivePaperMode);
        if (run is null || !IsOpenStatus(run.Status))
        {
            return new StopResult(false, 0);
        }

        _logger.LogWarning("Strategy run {RunId} ({Strategy}) is {Status} but has no runner process; closing it without a process to stop.",
            runId, run.StrategyName, run.Status);

        await MarkRunStoppingAsync(runId);

        int flattened = 0;
        if (flatten)
        {
            try
            {
                flattened = await _paperTradingService.FlattenRunAsync(runId, reason, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Flatten failed for orphaned strategy run {RunId}.", runId);
            }
        }

        var recorded = noteMissingRunner ? $"{reason} (runner process was not found)" : reason;
        await RecordRunStoppedAsync(runId, run.StrategyName, recorded, by, lastError: null, CancellationToken.None);
        await ClearRunnerPidAsync(runId);
        return new StopResult(true, flattened);
    }

    public static bool IsOpenStatus(string? status)
        => string.Equals(status, RunStatusRunning, StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, RunStatusStopping, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops every running strategy. Returns how many were stopped.</summary>
    public async Task<int> StopAllAsync(string reason, bool flatten, string by = "system", CancellationToken cancellationToken = default)
    {
        int stopped = 0;
        foreach (var entry in _registry.List())
        {
            try
            {
                var result = await StopAsync(entry.RunId, reason, flatten, by, cancellationToken);
                if (result.WasRunning) stopped++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StopAll: failed to stop strategy {StrategyId} run {RunId}.", entry.StrategyId, entry.RunId);
            }
        }
        return stopped;
    }

    /// <summary>
    /// Called by the exit monitor after it has claimed the stop for a runner that
    /// died on its own: squares off whatever the dead runner left open (nothing
    /// else would — the risk guard only watches registry entries), closes the
    /// run and releases the process handle.
    /// </summary>
    public Task<StopResult> HandleRunnerExitAsync(RunningStrategy entry, string reason, string? lastError)
        => FinishStopAsync(entry, reason, by: "runner", lastError, flatten: true, runnerAlreadyExited: true);

    /// <summary>
    /// Records a runner's pid durably so a restarted API can adopt or stop it.
    /// Best effort: a failure is logged and never fails the start.
    /// </summary>
    public async Task RecordRunnerPidAsync(long runId, int processId, string? by)
    {
        if (processId <= 0) return;
        try
        {
            await _processSettings.SetPidAsync(SystemSettingKeys.StrategyRunPid(runId), processId, by, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the runner pid {Pid} of strategy run {RunId}.", processId, runId);
        }
    }

    /// <summary>
    /// Once at startup, after migrations: every LivePaper run left
    /// Running/Stopping by the previous API process is either ADOPTED (its
    /// stored pid is alive and is an execution_runner for that run — the entry
    /// is rebuilt from the row and its exit monitor attached) or CLOSED as
    /// Stopped with <see cref="RestartReason"/>, flattening at last mark.
    /// </summary>
    public async Task<ReconcileResult> ReconcileOrphanedRunsAsync(CancellationToken cancellationToken = default)
    {
        var orphans = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Mode == LivePaperMode && (x.Status == RunStatusRunning || x.Status == RunStatusStopping))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        int adopted = 0, closed = 0;

        foreach (var run in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_registry.Contains(run.Id)) continue;

            bool wasAdopted = false;
            try
            {
                wasAdopted = await TryAdoptAsync(run, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Adoption of strategy run {RunId} failed; closing it instead.", run.Id);
            }

            if (wasAdopted)
            {
                adopted++;
                continue;
            }

            try
            {
                var result = await StopOrphanAsync(run.Id, RestartReason, flatten: true, by: "api", noteMissingRunner: false);
                if (result.WasRunning)
                {
                    closed++;
                    _logger.LogWarning("Strategy run {RunId} ({Strategy}) was {Status} with no live runner; closed as Stopped ({Flattened} position(s) squared off).",
                        run.Id, run.StrategyName, run.Status, result.Flattened);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not close orphaned strategy run {RunId}.", run.Id);
            }
        }

        return new ReconcileResult(adopted, closed);
    }

    /// <summary>
    /// Adopts the run when its stored pid is a live execution_runner for it.
    /// A Stopping row is never adopted (the previous API was already ending it).
    /// </summary>
    private async Task<bool> TryAdoptAsync(SimulationRun run, CancellationToken cancellationToken)
    {
        var key = SystemSettingKeys.StrategyRunPid(run.Id);
        var pid = await _processSettings.GetPidAsync(key, cancellationToken);
        if (pid is null)
        {
            _logger.LogInformation("Strategy run {RunId} has no stored runner pid; nothing to adopt.", run.Id);
            return false;
        }

        var process = ProcessProbe.TryGetAlive(pid.Value, ProcessProbe.StrategyRunnerMarker, run.Id, _logger);
        if (process is null)
        {
            return false;
        }

        // A Stopping row was already being ended by the previous API process
        // (its signals are refused); the runner it left behind must not linger.
        if (!string.Equals(run.Status, RunStatusRunning, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Strategy run {RunId} was {Status} at restart with its runner pid {Pid} still alive; terminating it.",
                run.Id, run.Status, pid);
            try
            {
                await ProcessTerminator.StopAsync(process, pid.Value, _ => { }, _logger, $"stale strategy run {run.Id}");
            }
            finally
            {
                process.Dispose();
            }
            return false;
        }

        var p = LiveRunParameters.Parse(run.ParametersJson);
        var underlying = (p.Underlying
                          ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                          ?? UnderlyingCatalog.InferUnderlying(run.Symbol)).Trim().ToUpperInvariant();
        var spotSymbol = string.IsNullOrWhiteSpace(run.Symbol) ? UnderlyingCatalog.SpotSymbolFor(underlying) : run.Symbol;

        var startedBy = await _dbContext.AppUsers.AsNoTracking()
            .Where(x => x.Id == run.UserId)
            .Select(x => x.UserName)
            .FirstOrDefaultAsync(cancellationToken) ?? "unknown";

        var entry = new RunningStrategy(
            StrategyCatalogService.StableId(run.StrategyName),
            run.StrategyName,
            process,
            startedBy,
            run.UserId,
            run.StartedUtc ?? run.CreatedUtc,
            run.Id,
            underlying,
            spotSymbol,
            Math.Max(1, p.Lots ?? 1),
            p.Risk)
        {
            Adopted = true
        };

        if (!_registry.TryAdd(entry))
        {
            process.Dispose();
            return false;
        }

        _logger.LogWarning("Adopted strategy run {RunId} ({Strategy} on {Underlying}) pid {Pid} after API restart — output not captured.",
            run.Id, run.StrategyName, underlying, pid);
        return true;
    }

    /// <summary>
    /// The single stop pipeline, run only by whoever won the claim on the entry:
    /// mark the run Stopping → stop the runner → flatten → persist RUN_STOPPED →
    /// registry bookkeeping → dispose. Every step is isolated so a failure in one
    /// never skips the rest, and the entry's StopCompletion is always resolved.
    /// </summary>
    private async Task<StopResult> FinishStopAsync(
        RunningStrategy entry,
        string reason,
        string by,
        string? lastError,
        bool flatten,
        bool runnerAlreadyExited)
    {
        int strategyId = entry.StrategyId;
        long runId = entry.RunId;
        int flattened = 0;

        try
        {
            _registry.AppendLog(runId, $"stopping: {reason}");

            // Closes the signal endpoint for this run before the flatten starts:
            // an in-flight OPEN_GROUP/CLOSE_GROUP from the runner is rejected
            // instead of racing the square-off.
            await MarkRunStoppingAsync(entry.RunId);

            if (!runnerAlreadyExited)
            {
                await StopProcessAsync(entry);
            }

            if (flatten)
            {
                try
                {
                    flattened = await _paperTradingService.FlattenRunAsync(entry.RunId, reason, CancellationToken.None);
                    if (flattened > 0)
                    {
                        _registry.AppendLog(runId, $"squared off {flattened} open position(s) at last mark");
                        if (runnerAlreadyExited)
                        {
                            _logger.LogWarning("Strategy {StrategyId} run {RunId}: runner exited with {Count} open position(s); squared off.",
                                strategyId, entry.RunId, flattened);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Flatten failed for strategy {StrategyId} run {RunId}.", strategyId, entry.RunId);
                    _registry.AppendLog(runId, $"flatten failed: {ex.Message}");
                }
            }

            try
            {
                await RecordRunStoppedAsync(entry.RunId, entry.Name, reason, by, lastError, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist stop of strategy {StrategyId} run {RunId}.", strategyId, entry.RunId);
            }

            await ClearRunnerPidAsync(runId);

            _registry.RecordExit(entry, reason);
            _registry.Remove(runId);

            await DisposeProcessAsync(entry, runnerAlreadyExited);

            _logger.LogInformation("Strategy {StrategyId} ({Name}) run {RunId} on {Underlying} stopped: {Reason} (by {By}, flattened {Flattened})",
                strategyId, entry.Name, runId, entry.Underlying, reason, by, flattened);

            return new StopResult(true, flattened);
        }
        finally
        {
            entry.StopCompletion.TrySetResult(flattened);
        }
    }

    /// <summary>
    /// Marks the SimulationRun stopped and persists the RUN_STOPPED signal.
    /// Idempotent: a run that is already closed keeps its first CompletedUtc.
    /// </summary>
    public async Task RecordRunStoppedAsync(
        long runId,
        string strategyName,
        string reason,
        string by,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);

        if (run is not null)
        {
            run.Status = RunStatusStopped;
            run.CompletedUtc ??= now;
            if (!string.IsNullOrWhiteSpace(lastError))
            {
                run.LastError = lastError;
            }
        }

        var metadata = JsonSerializer.Serialize(new { reason, by });

        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = runId,
            StrategyName = strategyName,
            SignalType = RunStoppedSignalType,
            TimestampUtc = now,
            GroupId = string.Empty,
            MetadataJson = metadata,
            CreatedUtc = now
        }, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Flips a Running run to Stopping with a single conditional UPDATE (no
    /// tracking, no read), so PaperTradingService rejects new signals for it.
    /// </summary>
    private async Task MarkRunStoppingAsync(long runId)
    {
        try
        {
            await _dbContext.SimulationRuns
                .Where(x => x.Id == runId && x.Status == RunStatusRunning)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RunStatusStopping), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not mark run {RunId} as stopping.", runId);
        }
    }

    /// <summary>Drops the persisted runner pid of a run that is closed (best effort).</summary>
    private async Task ClearRunnerPidAsync(long runId)
    {
        try
        {
            await _processSettings.DeleteAsync(SystemSettingKeys.StrategyRunPid(runId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the stored runner pid of strategy run {RunId}.", runId);
        }
    }

    // ------------------------------------------------------------------
    // Process control
    // ------------------------------------------------------------------

    /// <summary>
    /// SIGTERM, graceful wait, then SIGKILL of the whole tree — shared with the
    /// backtest control through <see cref="ProcessTerminator"/>. Works on an
    /// adopted handle too (the signals go by pid).
    /// </summary>
    private Task StopProcessAsync(RunningStrategy entry)
        => ProcessTerminator.StopAsync(
            entry.Process,
            entry.ProcessId,
            line => _registry.AppendLog(entry.RunId, line),
            _logger,
            $"strategy {entry.StrategyId} run {entry.RunId}");

    /// <summary>
    /// Releases the Process (stdout/stderr readers, exit-event registration)
    /// once the exit monitor has observed the exit, so neither side touches a
    /// disposed handle. On the runner-exit path the monitor IS the caller.
    /// </summary>
    private async Task DisposeProcessAsync(RunningStrategy entry, bool runnerAlreadyExited)
    {
        if (!runnerAlreadyExited && entry.ExitMonitor is { IsCompleted: false } monitor)
        {
            await Task.WhenAny(monitor, Task.Delay(MonitorSettleTimeout));
        }

        try
        {
            entry.Process.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disposing the process of strategy {StrategyId} run {RunId} failed.", entry.StrategyId, entry.RunId);
        }
    }
}
