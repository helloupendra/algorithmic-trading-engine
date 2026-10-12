using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiActions;

/// <summary>The owner an action runs for: an active admin's console account.</summary>
public sealed record IraOwner(long UserId, string UserName);

/// <summary>A run to stop, as the proposal resolved it.</summary>
public sealed record IraStopRun(long RunId, string Strategy, string Underlying, long AccountUserId, string Account);

/// <summary>A start, as the proposal resolved it: the catalog's strategy, the underlying, the lots and the account.</summary>
public sealed record IraStartRun(int StrategyId, string Strategy, string Underlying, int Lots, long AccountUserId, string Account);

/// <summary>Manual-book positions to square off: the book and exactly the positions the owner was shown.</summary>
public sealed record IraSquareOff(long BookRunId, long AccountUserId, string Account, IReadOnlyList<long> PositionIds, bool All);

/// <summary>An agent's switch to set.</summary>
public sealed record IraAgentSwitch(string Agent, string AgentName, bool On, string? Reason);

/// <summary>
/// What carrying out an action came to: done or not, the owner's line, and the ids it touched. <paramref name="Gone"/>
/// marks a refusal because the target had already closed (a run no longer running, positions no longer open): nothing
/// went wrong, there was nothing left to do.
/// </summary>
public sealed record IraOutcome(bool Done, string Message, object? Result = null, bool Gone = false)
{
    public static IraOutcome Refused(string why) => new(false, IraActionText.NotDone(why));

    /// <summary>Not done because the target had closed already.</summary>
    public static IraOutcome AlreadyClosed(string why) => new(false, IraActionText.NotDone(why), Gone: true);
}

/// <summary>A stop's result: the run, the legs it squared off, and its net after charges once stopped (null when unknown).</summary>
public sealed record IraRunStopped(long RunId, int Flattened, decimal? Net);

/// <summary>A square-off's result: the book, how many closed, which ones, and what they realized before charges.</summary>
public sealed record IraSquaredOff(long BookRunId, int Closed, IReadOnlyList<long> PositionIds, decimal Realized);

/// <summary>
/// The hands that carry out a confirmed action, through the services the console's own buttons use (a fake in tests).
/// Never a broker: every one of them acts on the paper desk.
/// </summary>
public interface IIraActionHands
{
    Task<IraOutcome> StopRunAsync(IraOwner owner, IraStopRun target, CancellationToken cancellationToken);

    Task<IraOutcome> StartRunAsync(IraOwner owner, IraStartRun target, CancellationToken cancellationToken);

    Task<IraOutcome> SquareOffAsync(IraOwner owner, IraSquareOff target, CancellationToken cancellationToken);

    Task<IraOutcome> SwitchAgentAsync(IraOwner owner, IraAgentSwitch target, CancellationToken cancellationToken);

    /// <summary>
    /// The account's day net after charges as the Today page states it (its live runs started today, recaps left out);
    /// null when it cannot be read or the account has no run today.
    /// </summary>
    Task<decimal?> DayNetAsync(string account, CancellationToken cancellationToken);
}

/// <summary>
/// A confirmed action carried out as the owner's own click would be, as "<c>owner via Ira</c>":
/// <list type="bullet">
/// <item>a stop through <see cref="StrategyRunControl.StopAsync"/> (or <see cref="StrategyRunControl.StopOrphanAsync"/>
/// for a run left open with no runner), squaring off, as the Desk's Stop button;</item>
/// <item>a start through <see cref="StrategyStartService"/>, with every check the Start button meets: the market's
/// hours, the duplicate and open-row checks, the run cap, the host's memory and the account's package;</item>
/// <item>a square-off through <see cref="IPaperTradingService.ClosePositionsAsync"/> on the manual book, the path the
/// book's square-off button and the risk guard take;</item>
/// <item>an agent's switch through <see cref="AiSettingsStore.SetAgentEnabledAsync"/>, as AI → Agents.</item>
/// </list>
/// An account close is the stop and the square-off above, item by item, each on hands of its own scope
/// (<see cref="IraAccountCloser"/>); its reply reads the account's day net as Today states it.
/// The strategies module grant is checked for the first three, as <c>[RequireModule]</c> checks it on their endpoints.
/// </summary>
public sealed class IraDeskHands(
    TradingDbContext db,
    StrategyRunControl control,
    StrategyProcessRegistry registry,
    StrategyStartService starts,
    IPaperTradingService paperTrading,
    AiSettingsStore store,
    IUserAdminService users,
    IRiskLimitsStore limits,
    IDerivativesInstrumentService derivatives,
    IHostMemory memory,
    ISystemNotifier notifier,
    IMarketSessionService? sessions = null,
    RunPnl? pnl = null,
    Today.TodayBuilder? today = null,
    TimeProvider? time = null) : IIraActionHands
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private const string NoStrategiesGrant = "your account does not have access to the strategies module";

    public async Task<IraOutcome> StopRunAsync(IraOwner owner, IraStopRun target, CancellationToken cancellationToken)
    {
        if (!await users.IsModuleAllowedAsync(owner.UserId, PlatformModules.Strategies, cancellationToken)) return IraOutcome.Refused(NoStrategiesGrant);

        string by = IraActionText.Actor(owner.UserName);
        string reason = $"Stopped by {by}";
        var running = registry.Get(target.RunId);
        StrategyRunControl.StopResult result;
        if (running is not null)
        {
            // The Stop button's path: the runner stopped first, every leg squared off, RUN_STOPPED recorded.
            result = await control.StopAsync(target.RunId, reason, flatten: true, by, cancellationToken);
            if (result.WasRunning)
            {
                await notifier.NotifyAsync(
                    NotificationCategory.StrategyRun,
                    NotificationSeverity.Warning,
                    $"{running.Name} stopped on {running.Underlying}",
                    $"Run #{running.RunId} stopped by {by}; squared off {result.Flattened} open position(s).",
                    underlying: running.Underlying,
                    simulationRunId: running.RunId,
                    cancellationToken: CancellationToken.None);
            }
        }
        else
        {
            // A row still open with no runner behind it (the API restarted): closed as the Stop button closes it.
            result = await control.StopOrphanAsync(target.RunId, reason, flatten: true, by);
        }

        if (!result.WasRunning) return IraOutcome.AlreadyClosed($"run {target.RunId} is not running any more, so there was nothing to stop");

        decimal? net = null;
        if (pnl is not null)
        {
            var figures = await pnl.FiguresAsync([target.RunId], new HashSet<long>(), CancellationToken.None);
            if (figures.TryGetValue(target.RunId, out var f)) net = f.Net;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        return new IraOutcome(true, IraActionText.Stopped(target, now, result.Flattened, net), new IraRunStopped(target.RunId, result.Flattened, net));
    }

    public async Task<IraOutcome> StartRunAsync(IraOwner owner, IraStartRun target, CancellationToken cancellationToken)
    {
        if (!await users.IsModuleAllowedAsync(owner.UserId, PlatformModules.Strategies, cancellationToken)) return IraOutcome.Refused(NoStrategiesGrant);

        // The Start button's own path and checks; the run records "owner via Ira" as who pressed it.
        var caller = new StrategyStarter(owner.UserId, IraActionText.Actor(owner.UserName), IsAdmin: true,
            AccountName: target.AccountUserId == owner.UserId ? owner.UserName : null);
        var outcome = await starts.StartAsync(
            target.StrategyId,
            new StartStrategyRequest { Underlying = target.Underlying, Lots = target.Lots, OwnerUserId = target.AccountUserId },
            caller, limits, derivatives, memory, sessions, cancellationToken);

        if (!outcome.Started) return IraOutcome.Refused(outcome.Message);

        var now = _time.GetUtcNow().UtcDateTime;
        return new IraOutcome(true, IraActionText.Started(target, outcome.RunId!.Value, now),
            new { runId = outcome.RunId, strategy = outcome.Strategy, underlying = outcome.Underlying, lots = outcome.Lots, account = target.Account });
    }

    public async Task<IraOutcome> SquareOffAsync(IraOwner owner, IraSquareOff target, CancellationToken cancellationToken)
    {
        if (!await users.IsModuleAllowedAsync(owner.UserId, PlatformModules.Strategies, cancellationToken)) return IraOutcome.Refused(NoStrategiesGrant);

        var book = await db.SimulationRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == target.BookRunId && r.UserId == target.AccountUserId
                                      && r.StrategyName == Controllers.ManualOrdersController.BookStrategyName
                                      && r.Status == StrategyRunControl.RunStatusRunning, cancellationToken);
        if (book is null) return IraOutcome.AlreadyClosed($"{target.Account}'s manual book is not open any more");

        // Exactly the positions the owner was shown, and only those still open in that book.
        var open = await db.PaperPositions.AsNoTracking()
            .Where(p => p.SimulationRunId == book.Id && p.Status == "Open" && target.PositionIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (open.Count == 0) return IraOutcome.AlreadyClosed("none of those positions is open any more: nothing to square off");

        string by = IraActionText.Actor(owner.UserName);
        int closed = await paperTrading.ClosePositionsAsync(book.Id, open, $"Squared off by {by}", by, CancellationToken.None);
        if (closed == 0) return IraOutcome.AlreadyClosed("none of those positions could be squared off: they closed in the meantime");

        var rows = await db.PaperPositions.AsNoTracking()
            .Where(p => open.Contains(p.Id))
            .Select(p => new { p.Id, p.Symbol, p.RealizedPnl, p.Status })
            .ToListAsync(CancellationToken.None);
        var done = rows.Where(r => r.Status != "Open").ToList();
        decimal realized = done.Sum(r => r.RealizedPnl);
        var now = _time.GetUtcNow().UtcDateTime;
        return new IraOutcome(true,
            IraActionText.SquaredOff(target, closed, target.PositionIds.Count, now, done.Select(r => r.Symbol).ToList(), realized),
            new IraSquaredOff(book.Id, closed, done.Select(r => r.Id).ToList(), realized));
    }

    public async Task<IraOutcome> SwitchAgentAsync(IraOwner owner, IraAgentSwitch target, CancellationToken cancellationToken)
    {
        var state = await store.LoadAsync(cancellationToken);
        var agent = state.Agent(target.Agent);
        if (agent is null || !agent.Def.Built) return IraOutcome.Refused($"the {target.AgentName} is not built");
        if (agent.Enabled == target.On) return IraOutcome.Refused($"the {target.AgentName} was already {(target.On ? "on" : "off")}");

        // AI → Agents' own store and row: the Agents tab shows who switched it, and why.
        string by = IraActionText.Actor(owner.UserName);
        await store.SetAgentEnabledAsync(target.Agent, target.On, by, target.Reason ?? $"Switched {(target.On ? "on" : "off")} by {by}", cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        return new IraOutcome(true, IraActionText.Switched(target, now), new { agent = target.Agent, enabled = target.On });
    }

    public async Task<decimal?> DayNetAsync(string account, CancellationToken cancellationToken)
    {
        if (today is null) return null;
        // Today's own figure, so the reply and the Today page (and Ira's close briefing) never differ.
        var trading = await today.TradingAsync(IstTime.DateOf(_time.GetUtcNow().UtcDateTime), cancellationToken);
        return trading.Accounts.FirstOrDefault(a => string.Equals(a.UserName, account, StringComparison.OrdinalIgnoreCase))?.Net;
    }
}
