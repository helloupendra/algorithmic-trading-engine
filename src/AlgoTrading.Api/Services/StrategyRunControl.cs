// src/AlgoTrading.Api/Services/StrategyRunControl.cs
using AlgoTrading.Api.Controllers;
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
///
/// One stop is different: the market close's (<see cref="StopAtMarketCloseAsync"/>)
/// first moves the legs the owner ticked "carry forward" into their manual
/// book, then flattens the rest. Every other trigger flattens every leg,
/// ticked or not — see <see cref="PositionCarryForward"/>.
///
/// Every change of a run's state — started, stopping, stopped, adopted, its
/// rules changed — is told to the console as a <c>run</c> desk event
/// (<see cref="PublishRunEvent"/>), once the row says so.
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
    private readonly IMarketReplayBook? _replayBook;
    private readonly IProcessSettingsStore _processSettings;
    private readonly StrategyProcessRegistry _registry;
    private readonly PositionCarryForward _carryForward;
    private readonly PythonEngineLocator _engine;
    private readonly IProcessProbe _probe;
    private readonly ISystemNotifier _notifier;
    private readonly ILogger<StrategyRunControl> _logger;
    private readonly IDeskEventPublisher? _deskEvents;

    /// <param name="deskEvents">Tells the console a run changed; null where nobody is watching (tests).</param>
    public StrategyRunControl(
        TradingDbContext dbContext,
        IPaperTradingService paperTradingService,
        IProcessSettingsStore processSettings,
        StrategyProcessRegistry registry,
        PositionCarryForward carryForward,
        PythonEngineLocator engine,
        IProcessProbe probe,
        ISystemNotifier notifier,
        ILogger<StrategyRunControl> logger,
        IDeskEventPublisher? deskEvents = null,
        IMarketReplayBook? replayBook = null)
    {
        _dbContext = dbContext;
        _paperTradingService = paperTradingService;
        _processSettings = processSettings;
        _registry = registry;
        _carryForward = carryForward;
        _engine = engine;
        _probe = probe;
        _notifier = notifier;
        _logger = logger;
        _deskEvents = deskEvents;
        _replayBook = replayBook;
    }

    /// <summary>
    /// The waits before a runner that could not be verified is probed again at
    /// startup: 2, 4 and 8 seconds, so a probe that failed under a moment's
    /// load gets 14 seconds to come right before the run is reported.
    /// </summary>
    public IReadOnlyList<TimeSpan> UnknownProbeRetryDelays { get; init; } =
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) };

    /// <param name="Carried">Legs moved to the owner's manual book (a stop at the market close only).</param>
    public sealed record StopResult(bool WasRunning, int Flattened, int Carried = 0);

    /// <param name="Unverified">Runs whose runner could be neither confirmed nor ruled out: left as they were, and reported.</param>
    public sealed record ReconcileResult(int Adopted, int Closed, int Unverified = 0);

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
    /// Stops a run because its market has closed (<see cref="MarketCloseRules"/>):
    /// the legs ticked "carry forward" are moved to the run owner's manual book
    /// at their entry, and everything else is squared off, as it always was.
    /// </summary>
    /// <remarks>
    /// The only stop that honours the tick. The owner's request (27 Sep) was to
    /// carry a leg past the close; a stop for any other reason — the Stop
    /// button, a risk rule, a runner that died — is a decision to get out, and
    /// leaving a leg behind would turn a protective stop into a partial one.
    /// A second caller that finds the stop already claimed waits for it, as
    /// with <see cref="StopAsync"/>, whoever claimed it.
    /// </remarks>
    public async Task<StopResult> StopAtMarketCloseAsync(
        long runId,
        string reason,
        DateTime closedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entry = _registry.Get(runId);
        if (entry is null)
        {
            return new StopResult(false, 0);
        }

        if (!entry.TryClaimStop())
        {
            _logger.LogInformation("Stop of run {RunId} ({Name} on {Underlying}) already in progress; waiting for it ({Reason}).",
                runId, entry.Name, entry.Underlying, reason);
            int flattenedByOwner = await entry.StopCompletion.Task.ConfigureAwait(false);
            return new StopResult(true, flattenedByOwner);
        }

        return await FinishStopAsync(entry, reason, MarketCloseBy, lastError: null, flatten: true,
            runnerAlreadyExited: false, carryAtCloseUtc: closedAtUtc);
    }

    /// <summary>Who a market-close stop is attributed to.</summary>
    public const string MarketCloseBy = "market-hours";

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

        await MarkRunStoppingAsync(runId, run.UserId, reason);

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
    /// is rebuilt from the row and its exit monitor attached), CLOSED as
    /// Stopped with <see cref="RestartReason"/>, flattening at last mark, when
    /// its runner is known to be gone — or, when its pid could not be verified
    /// either way, LEFT exactly as it is and reported.
    /// </summary>
    /// <remarks>
    /// "Could not verify" is not "gone". Until 28 Sep the probe folded the two
    /// together, and a runner still trading whose command line could not be
    /// read at that moment (ps timing out on a loaded box) had its run closed
    /// and its positions flattened under it. Such a pid is probed again after
    /// 2, 4 and 8 seconds, all of them together; one still unverified keeps
    /// its row and its pid, and the operator is told. The start endpoint
    /// refuses a second run beside it.
    /// </remarks>
    public async Task<ReconcileResult> ReconcileOrphanedRunsAsync(CancellationToken cancellationToken = default)
    {
        // A manual book is Running with no runner BY DESIGN — it is a container
        // for orders placed by hand, not a process. Left in this sweep it looks
        // exactly like an orphan, so every API restart closed the operator's
        // book and squared off the positions in it: a restart silently
        // liquidated hand-placed trades and the console then reported that
        // nothing had ever been traded by hand.
        var orphans = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Mode == LivePaperMode
                        && (x.Status == RunStatusRunning || x.Status == RunStatusStopping)
                        && x.StrategyName != ManualOrdersController.BookStrategyName)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        int adopted = 0, closed = 0;
        var unverified = new List<SimulationRun>();

        foreach (var run in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_registry.Contains(run.Id)) continue;

            switch (await ReconcileOneAsync(run, cancellationToken))
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

            _logger.LogWarning("Could not verify the runner of {Count} live run(s) ({RunIds}); probing again in {Seconds}s.",
                unverified.Count, string.Join(", ", unverified.Select(x => x.Id)), delay.TotalSeconds);
            await Task.Delay(delay, cancellationToken);

            var still = new List<SimulationRun>();
            foreach (var run in unverified)
            {
                switch (await ReconcileOneAsync(run, cancellationToken))
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
            await ReportUnverifiedAsync(run, cancellationToken);
        }

        return new ReconcileResult(adopted, closed, unverified.Count);
    }

    /// <summary>What reconciling one run came to.</summary>
    private enum Adoption
    {
        /// <summary>Its runner is alive and verified: the run is in the registry again.</summary>
        Adopted,

        /// <summary>Its runner is gone (or was ended): the run was closed.</summary>
        Closed,

        /// <summary>Its runner could not be verified either way: nothing was touched.</summary>
        Unverified,

        /// <summary>Something else registered it meanwhile: nothing to do.</summary>
        Skipped
    }

    private async Task<Adoption> ReconcileOneAsync(SimulationRun run, CancellationToken cancellationToken)
    {
        Adoption outcome;
        try
        {
            outcome = await TryAdoptAsync(run, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Adoption of strategy run {RunId} failed; closing it instead.", run.Id);
            outcome = Adoption.Closed;
        }

        if (outcome != Adoption.Closed) return outcome;

        try
        {
            var result = await StopOrphanAsync(run.Id, RestartReason, flatten: true, by: "api", noteMissingRunner: false);
            if (!result.WasRunning) return Adoption.Skipped;

            _logger.LogWarning("Strategy run {RunId} ({Strategy}) was {Status} with no live runner; closed as Stopped ({Flattened} position(s) squared off).",
                run.Id, run.StrategyName, run.Status, result.Flattened);
            return Adoption.Closed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not close orphaned strategy run {RunId}.", run.Id);
            return Adoption.Skipped;
        }
    }

    /// <summary>
    /// Adopts the run when its stored pid is a live execution_runner for it.
    /// A Stopping row is never adopted (the previous API was already ending it).
    /// Returns <see cref="Adoption.Closed"/> for a run to close — the caller
    /// closes it — and <see cref="Adoption.Unverified"/> when the pid is alive
    /// but could not be told apart from a recycled one.
    /// </summary>
    private async Task<Adoption> TryAdoptAsync(SimulationRun run, CancellationToken cancellationToken)
    {
        var key = SystemSettingKeys.StrategyRunPid(run.Id);
        var pid = await _processSettings.GetPidAsync(key, cancellationToken);
        if (pid is null)
        {
            _logger.LogInformation("Strategy run {RunId} has no stored runner pid; nothing to adopt.", run.Id);
            return Adoption.Closed;
        }

        var probe = _probe.Probe(pid.Value, ProcessProbe.StrategyRunnerMarker, run.Id);
        if (probe.IsUnknown) return Adoption.Unverified;
        if (!probe.IsAlive || probe.Process is not { } process) return Adoption.Closed;

        // A Stopping row was already being ended by the previous API process
        // (its signals are refused); the runner it left behind must not linger.
        if (!string.Equals(run.Status, RunStatusRunning, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Strategy run {RunId} was {Status} at restart with its runner pid {Pid} still alive; terminating it.",
                run.Id, run.Status, pid);
            try
            {
                await ProcessTerminator.StopAsync(process, pid.Value, _ => { }, _logger, $"stale strategy run {run.Id}", adopted: true);
            }
            finally
            {
                process.Dispose();
            }
            return Adoption.Closed;
        }

        var p = LiveRunParameters.Parse(run.ParametersJson);
        var underlying = (p.Underlying
                          ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                          ?? UnderlyingCatalog.InferUnderlying(run.Symbol)).Trim().ToUpperInvariant();
        var spotSymbol = string.IsNullOrWhiteSpace(run.Symbol) ? UnderlyingCatalog.SpotSymbolFor(underlying) : run.Symbol;

        // Who started it, as the row recorded at the start. Only rows from
        // before 28 Sep have none and fall back to the owner — which is what
        // every adopted run used to claim, and why runs #234 and #235 changed
        // from admin to coderforchange at the 24 Sep restart.
        var startedBy = !string.IsNullOrWhiteSpace(run.StartedByName)
            ? run.StartedByName
            : await _dbContext.AppUsers.AsNoTracking()
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
            Adopted = true,
            // No pipes to read: its console comes from the log it keeps itself.
            OutputLogPath = RunnerOutputLog.PathFor(_engine.EngineLogDirectory, run.Id, pid.Value)
        };

        if (!_registry.TryAdd(entry))
        {
            // Registered meanwhile: it has a runner, and closing it would flatten under it.
            process.Dispose();
            return Adoption.Skipped;
        }

        _logger.LogWarning("Adopted strategy run {RunId} ({Strategy} on {Underlying}) pid {Pid} after API restart; its output is read from {LogFile}.",
            run.Id, run.StrategyName, underlying, pid, entry.OutputLogPath);
        PublishRunEvent(run.Id, run.UserId, "Adopted after an API restart: watched and stoppable again");
        return Adoption.Adopted;
    }

    /// <summary>
    /// A run whose runner could not be verified after every retry: its row and
    /// its stored pid stay as they are — neither adopted nor closed — and the
    /// operator is told, because nothing guards it until someone decides.
    /// </summary>
    private async Task ReportUnverifiedAsync(SimulationRun run, CancellationToken cancellationToken)
    {
        var pid = await _processSettings.GetPidAsync(SystemSettingKeys.StrategyRunPid(run.Id), cancellationToken);
        var underlying = LiveRunParameters.Parse(run.ParametersJson).Underlying
                         ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                         ?? UnderlyingCatalog.InferUnderlying(run.Symbol);

        _logger.LogError(
            "Strategy run {RunId} ({Strategy} on {Underlying}) is {Status} with runner pid {Pid} alive, but its command line could not be read "
            + "after {Tries} tries; it was neither adopted nor closed. No risk guard watches it until it is dealt with by hand.",
            run.Id, run.StrategyName, underlying, run.Status, pid, UnknownProbeRetryDelays.Count + 1);

        try
        {
            await _notifier.NotifyAsync(
                NotificationCategory.StrategyRun,
                NotificationSeverity.Error,
                $"Run #{run.Id} could not be verified after the API restart",
                $"{run.StrategyName} on {underlying}: pid {pid} is alive, but the API could not confirm it is this run's runner, "
                    + "so the run was neither adopted nor closed. Nothing watches its risk rules. Check the process on the server "
                    + $"(ps -o args= -p {pid}); if it is the runner, restart the API to adopt it, otherwise stop the run from its page.",
                underlying: underlying,
                symbol: run.Symbol,
                simulationRunId: run.Id,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send the alert for unverified strategy run {RunId}.", run.Id);
        }
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
        bool runnerAlreadyExited,
        DateTime? carryAtCloseUtc = null)
    {
        int strategyId = entry.StrategyId;
        long runId = entry.RunId;
        int flattened = 0;
        int carried = 0;

        try
        {
            _registry.AppendLog(runId, $"stopping: {reason}");

            // Closes the signal endpoint for this run before the flatten starts:
            // an in-flight OPEN_GROUP/CLOSE_GROUP from the runner is rejected
            // instead of racing the square-off.
            await MarkRunStoppingAsync(entry.RunId, entry.UserId, reason);

            if (!runnerAlreadyExited)
            {
                await StopProcessAsync(entry);
            }

            // At the close only, and after the runner is gone (it can no longer
            // close or add to a leg): the ticked legs leave for the owner's
            // book before the flatten, which then finds only what stays. A leg
            // that fails to move is still open, so the flatten squares it off —
            // the day ends for it exactly as it did before the tick existed.
            if (carryAtCloseUtc is { } closedAtUtc)
            {
                try
                {
                    carried = await _carryForward.CarryTickedLegsAsync(runId, closedAtUtc, CancellationToken.None);
                    if (carried > 0)
                    {
                        _registry.AppendLog(runId, $"carried {carried} ticked leg(s) forward to the owner's manual book");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Carry forward failed for strategy {StrategyId} run {RunId}; its ticked legs are squared off with the rest.",
                        strategyId, runId);
                    _registry.AppendLog(runId, $"carry forward failed: {ex.Message}; squaring off every leg");
                }
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

            _logger.LogInformation("Strategy {StrategyId} ({Name}) run {RunId} on {Underlying} stopped: {Reason} (by {By}, flattened {Flattened}, carried {Carried})",
                strategyId, entry.Name, runId, entry.Underlying, reason, by, flattened, carried);

            return new StopResult(true, flattened, carried);
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

        // On the activity timeline a recap's stop belongs to the replayed
        // session, after the trades it ends; CompletedUtc above stays the moment
        // it actually stopped.
        var marketNow = run is null ? null : await RecapClock.NowAsync(_dbContext, run, cancellationToken, _replayBook);

        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = runId,
            StrategyName = strategyName,
            SignalType = RunStoppedSignalType,
            TimestampUtc = marketNow ?? now,
            GroupId = string.Empty,
            MetadataJson = metadata,
            CreatedUtc = now
        }, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);

        PublishRunEvent(runId, run?.UserId, $"Stopped: {reason}");
    }

    /// <summary>
    /// Tells the console that run <paramref name="runId"/> changed: its owner's
    /// connections and every admin's. Call it once the row says so. Never throws.
    /// </summary>
    /// <remarks>
    /// Here rather than in each caller so that "run" events have one source:
    /// the start endpoint, the stop pipeline, the startup reconcile and the
    /// risk-rules edit all come through it.
    /// </remarks>
    public void PublishRunEvent(long runId, long? ownerUserId, string detail)
        => _deskEvents.TryPublish(new DeskEvent(DeskEventKinds.Run, runId, ownerUserId, Symbol: null, DateTime.UtcNow, detail));

    /// <summary>
    /// Flips a Running run to Stopping with a single conditional UPDATE (no
    /// tracking, no read), so PaperTradingService rejects new signals for it.
    /// The console is told, so a card says "stopping" during the seconds the
    /// runner takes to exit and the legs to square off, not only after.
    /// </summary>
    private async Task MarkRunStoppingAsync(long runId, long? ownerUserId, string reason)
    {
        try
        {
            int marked = await _dbContext.SimulationRuns
                .Where(x => x.Id == runId && x.Status == RunStatusRunning)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RunStatusStopping), CancellationToken.None);
            if (marked > 0)
            {
                PublishRunEvent(runId, ownerUserId, $"Stopping: {reason}");
            }
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
    /// adopted handle too (the signals go by pid), and an adopted runner, not
    /// being our child, is given longer and watched by pid.
    /// </summary>
    private Task StopProcessAsync(RunningStrategy entry)
        => ProcessTerminator.StopAsync(
            entry.Process,
            entry.ProcessId,
            line => _registry.AppendLog(entry.RunId, line),
            _logger,
            $"strategy {entry.StrategyId} run {entry.RunId}",
            adopted: entry.Adopted);

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
