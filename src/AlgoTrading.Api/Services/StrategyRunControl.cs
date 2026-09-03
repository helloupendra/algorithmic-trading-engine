// src/AlgoTrading.Api/Services/StrategyRunControl.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
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

    private static readonly TimeSpan MonitorSettleTimeout = TimeSpan.FromSeconds(2);

    private readonly TradingDbContext _dbContext;
    private readonly IPaperTradingService _paperTradingService;
    private readonly StrategyProcessRegistry _registry;
    private readonly ILogger<StrategyRunControl> _logger;

    public StrategyRunControl(
        TradingDbContext dbContext,
        IPaperTradingService paperTradingService,
        StrategyProcessRegistry registry,
        ILogger<StrategyRunControl> logger)
    {
        _dbContext = dbContext;
        _paperTradingService = paperTradingService;
        _registry = registry;
        _logger = logger;
    }

    public sealed record StopResult(bool WasRunning, int Flattened);

    /// <summary>
    /// Stops one running strategy. <paramref name="by"/> is recorded in the
    /// RUN_STOPPED metadata (user name, "risk-guard", "market-hours"...).
    /// The cancellation token is deliberately NOT propagated into the stop
    /// pipeline: a client that disconnects (or a host that is shutting down)
    /// after the stop has been claimed must not leave the run half-stopped
    /// with open positions and a "Running" status.
    /// </summary>
    public async Task<StopResult> StopAsync(
        int strategyId,
        string reason,
        bool flatten,
        string by,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = _registry.Get(strategyId);
        if (entry is null)
        {
            return new StopResult(false, 0);
        }

        // Exactly one stopper owns the shutdown; everyone else shares its outcome.
        if (!entry.TryClaimStop())
        {
            _logger.LogInformation("Stop of strategy {StrategyId} already in progress; waiting for it ({Reason}).", strategyId, reason);
            int flattenedByOwner = await entry.StopCompletion.Task.ConfigureAwait(false);
            return new StopResult(true, flattenedByOwner);
        }

        return await FinishStopAsync(entry, reason, by, lastError: null, flatten, runnerAlreadyExited: false);
    }

    /// <summary>Stops every running strategy. Returns how many were stopped.</summary>
    public async Task<int> StopAllAsync(string reason, bool flatten, string by = "system", CancellationToken cancellationToken = default)
    {
        int stopped = 0;
        foreach (var entry in _registry.List())
        {
            try
            {
                var result = await StopAsync(entry.StrategyId, reason, flatten, by, cancellationToken);
                if (result.WasRunning) stopped++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StopAll: failed to stop strategy {StrategyId}.", entry.StrategyId);
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
        int flattened = 0;

        try
        {
            _registry.AppendLog(strategyId, $"stopping: {reason}");

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
                        _registry.AppendLog(strategyId, $"squared off {flattened} open position(s) at last mark");
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
                    _registry.AppendLog(strategyId, $"flatten failed: {ex.Message}");
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

            _registry.RecordExit(entry, reason);
            _registry.Remove(strategyId);

            await DisposeProcessAsync(entry, runnerAlreadyExited);

            _logger.LogInformation("Strategy {StrategyId} ({Name}) stopped: {Reason} (by {By}, flattened {Flattened})",
                strategyId, entry.Name, reason, by, flattened);

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

    // ------------------------------------------------------------------
    // Process control
    // ------------------------------------------------------------------

    /// <summary>
    /// SIGTERM, graceful wait, then SIGKILL of the whole tree — shared with the
    /// backtest control through <see cref="ProcessTerminator"/>.
    /// </summary>
    private Task StopProcessAsync(RunningStrategy entry)
        => ProcessTerminator.StopAsync(
            entry.Process,
            entry.ProcessId,
            line => _registry.AppendLog(entry.StrategyId, line),
            _logger,
            $"strategy {entry.StrategyId}");

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
            _logger.LogDebug(ex, "Disposing the process of strategy {StrategyId} failed.", entry.StrategyId);
        }
    }
}
