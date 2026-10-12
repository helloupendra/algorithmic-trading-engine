using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiActions;

/// <summary>A live strategy run to stop when its account is closed, as the proposal saw it: its open legs and its net after charges.</summary>
public sealed record IraCloseRun(long RunId, string Strategy, string Underlying, int OpenLegs, decimal? Net);

/// <summary>A manual-book position to square off when its account is closed, as the proposal saw it.</summary>
/// <param name="Contract">As the console labels it: "NIFTY 25100 CE · 27 Oct".</param>
/// <param name="Side">BUY or SELL.</param>
/// <param name="Unrealized">Marked to market when proposed, before charges; null when it could not be marked.</param>
public sealed record IraClosePosition(long PositionId, string Symbol, string Contract, string Side, int Lots, decimal? Unrealized);

/// <summary>
/// Everything open in one account, exactly as the owner was shown it: its live strategy runs and its manual book's open
/// positions. Stored whole in the action's parameters; the summary lists only the first few.
/// </summary>
public sealed record IraCloseAccount(long AccountUserId, string Account, IReadOnlyList<IraCloseRun> Runs, long? BookRunId, IReadOnlyList<IraClosePosition> Positions);

/// <summary>How one item of an account close ended.</summary>
public enum IraCloseState
{
    /// <summary>Stopped, or squared off, now.</summary>
    Done,

    /// <summary>It had closed by the time the owner confirmed (or while the close ran): nothing to do.</summary>
    AlreadyClosed,

    /// <summary>It is still open: the desk refused, or something went wrong.</summary>
    Failed,
}

/// <summary>One item of an account close and how it ended: a run (with the legs its stop squared off) or a manual position.</summary>
/// <param name="Type"><see cref="RunType"/> or <see cref="PositionType"/>.</param>
/// <param name="Label">How the owner reads it: "run 512" or "NIFTY 25100 CE · 27 Oct ×2".</param>
/// <param name="Detail">The rest of what names it: "Ghost Tangent Crossings on NIFTY", "BUY".</param>
/// <param name="Reason">Why it failed, or how it had closed; null when done.</param>
public sealed record IraCloseItem(string Type, long Id, string Label, string Detail, IraCloseState State, string? Reason = null, int Flattened = 0, decimal? Net = null)
{
    public const string RunType = "run";
    public const string PositionType = "position";
}

/// <summary>What an account close came to: the owner's line, and each item.</summary>
public sealed record IraCloseResult(IraOutcome Outcome, IReadOnlyList<IraCloseItem> Items);

/// <summary>
/// Carries out a confirmed account close (owner, 12 Oct 2026: "If I tell Ira to close all trades of a particular account,
/// everything should close together").
/// </summary>
/// <remarks>
/// <para>
/// Each run still open is stopped through <see cref="IIraActionHands.StopRunAsync"/>, the single stop's own path (the
/// Desk's Stop button: <see cref="StrategyRunControl.StopAsync"/> squaring off, or
/// <see cref="StrategyRunControl.StopOrphanAsync"/> for a run with no runner); the manual positions still open are
/// squared off together through <see cref="IIraActionHands.SquareOffAsync"/>, the book's reduce-only close.
/// </para>
/// <para>
/// They run at the same time, at most <see cref="Parallelism"/> at once: the server has two CPUs and was overloaded on
/// the day this was asked for, and a stop is a runner process to end plus a square-off to fill. Each item takes its own
/// DI scope (its own database context: one context cannot be used by two stops at once); without a scope factory
/// (a test's hands) they run one after another. Every item ends on its own: one that fails or throws is reported and
/// the others go on. What had closed before the PIN is skipped and reported, not stopped again.
/// </para>
/// </remarks>
public sealed class IraAccountCloser(
    TradingDbContext db,
    IIraActionHands hands,
    IServiceScopeFactory? scopes,
    ILogger logger,
    TimeProvider time)
{
    /// <summary>How many items close at once, at most.</summary>
    public const int Parallelism = 4;

    private const string OpenStatus = "Open";

    public async Task<IraCloseResult> CloseAsync(IraOwner owner, IraCloseAccount target)
    {
        // What is still open now: whatever closed after the proposal is skipped, not stopped again.
        var runIds = target.Runs.Select(r => r.RunId).ToList();
        var openRuns = (await db.SimulationRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id)
                            && (r.Status == StrategyRunControl.RunStatusRunning || r.Status == StrategyRunControl.RunStatusStopping))
                .Select(r => r.Id)
                .ToListAsync(CancellationToken.None))
            .ToHashSet();
        var positionIds = target.Positions.Select(p => p.PositionId).ToList();
        var openPositions = target.BookRunId is long book && positionIds.Count > 0
            ? (await db.PaperPositions.AsNoTracking()
                    .Where(p => p.SimulationRunId == book && p.Status == OpenStatus && positionIds.Contains(p.Id))
                    .Select(p => p.Id)
                    .ToListAsync(CancellationToken.None))
                .ToHashSet()
            : [];

        var runItems = new IraCloseItem[target.Runs.Count];
        var jobs = new List<Func<Task>>();
        for (int i = 0; i < target.Runs.Count; i++)
        {
            int index = i;
            var run = target.Runs[i];
            if (openRuns.Contains(run.RunId)) jobs.Add(async () => runItems[index] = await StopAsync(owner, target, run));
            else runItems[index] = RunItem(run, IraCloseState.AlreadyClosed, "it had stopped before the PIN");
        }

        var toSquareOff = target.Positions.Where(p => openPositions.Contains(p.PositionId)).Select(p => p.PositionId).ToList();
        IraOutcome? squareOff = null;
        if (toSquareOff.Count > 0) jobs.Add(async () => squareOff = await SquareOffAsync(owner, target, toSquareOff));

        await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = scopes is null ? 1 : Parallelism },
            async (job, _) => await job());

        var positionItems = await PositionItemsAsync(target, openPositions, squareOff);
        var items = runItems.Concat(positionItems).ToList();
        decimal? realized = squareOff?.Result is IraSquaredOff s ? s.Realized : null;

        decimal? dayNet = null;
        try
        {
            dayNet = await hands.DayNetAsync(target.Account, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The account is closed either way: the reply says so without the figure.
            logger.LogWarning(ex, "Ira actions: the day net of {Account} could not be read after its close", target.Account);
        }

        var now = time.GetUtcNow().UtcDateTime;
        bool acted = items.Any(i => i.State == IraCloseState.Done);
        bool failed = items.Any(i => i.State == IraCloseState.Failed);
        string message = IraActionText.AccountClosed(target.Account, now, items, realized, dayNet);
        var result = new
        {
            account = target.Account,
            accountUserId = target.AccountUserId,
            runsStopped = items.Count(i => i is { Type: IraCloseItem.RunType, State: IraCloseState.Done }),
            strategyPositionsSquaredOff = items.Where(i => i is { Type: IraCloseItem.RunType, State: IraCloseState.Done }).Sum(i => i.Flattened),
            manualPositionsSquaredOff = items.Count(i => i is { Type: IraCloseItem.PositionType, State: IraCloseState.Done }),
            alreadyClosed = items.Count(i => i.State == IraCloseState.AlreadyClosed),
            failed = items.Count(i => i.State == IraCloseState.Failed),
            realizedManual = realized,
            dayNet,
            items = items.Select(i => new { type = i.Type, id = i.Id, state = StateWord(i.State), reason = i.Reason, flattened = i.Flattened, net = i.Net }),
        };
        return new IraCloseResult(new IraOutcome(acted && !failed, message, result, Gone: !acted && !failed), items);
    }

    /// <summary>"done", "already-closed" or "failed": how the activity log and the result name a state.</summary>
    public static string StateWord(IraCloseState state) => state switch
    {
        IraCloseState.Done => "done",
        IraCloseState.AlreadyClosed => "already-closed",
        _ => "failed",
    };

    private async Task<IraCloseItem> StopAsync(IraOwner owner, IraCloseAccount target, IraCloseRun run)
    {
        try
        {
            var stop = new IraStopRun(run.RunId, run.Strategy, run.Underlying, target.AccountUserId, target.Account);
            var outcome = await WithHandsAsync(h => h.StopRunAsync(owner, stop, CancellationToken.None));
            if (outcome.Done)
            {
                var stopped = outcome.Result as IraRunStopped;
                return RunItem(run, IraCloseState.Done, null) with { Flattened = stopped?.Flattened ?? 0, Net = stopped?.Net };
            }

            return RunItem(run, outcome.Gone ? IraCloseState.AlreadyClosed : IraCloseState.Failed, Why(outcome.Message));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ira actions: stopping run {RunId} of {Account}'s account failed on the server", run.RunId, target.Account);
            return RunItem(run, IraCloseState.Failed, $"it failed on the server ({ex.GetType().Name}); see api.log");
        }
    }

    private async Task<IraOutcome> SquareOffAsync(IraOwner owner, IraCloseAccount target, IReadOnlyList<long> positionIds)
    {
        try
        {
            var squareOff = new IraSquareOff(target.BookRunId!.Value, target.AccountUserId, target.Account, positionIds, All: true);
            return await WithHandsAsync(h => h.SquareOffAsync(owner, squareOff, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ira actions: squaring off {Account}'s manual positions failed on the server", target.Account);
            return IraOutcome.Refused($"it failed on the server ({ex.GetType().Name}); see api.log");
        }
    }

    /// <summary>
    /// Each manual position: squared off by this close, closed before the PIN or in the meantime, or still open (failed,
    /// with the square-off's words). Read after every stop has ended, on this scope's own context.
    /// </summary>
    private async Task<List<IraCloseItem>> PositionItemsAsync(IraCloseAccount target, IReadOnlySet<long> openAtConfirm, IraOutcome? squareOff)
    {
        var squared = squareOff?.Result is IraSquaredOff s ? s.PositionIds.ToHashSet() : [];
        var unsure = target.Positions.Select(p => p.PositionId).Where(id => openAtConfirm.Contains(id) && !squared.Contains(id)).ToList();
        var stillOpen = unsure.Count == 0
            ? []
            : (await db.PaperPositions.AsNoTracking()
                    .Where(p => unsure.Contains(p.Id) && p.Status == OpenStatus)
                    .Select(p => p.Id)
                    .ToListAsync(CancellationToken.None))
                .ToHashSet();

        return target.Positions.Select(p =>
        {
            if (!openAtConfirm.Contains(p.PositionId)) return PositionItem(p, IraCloseState.AlreadyClosed, "it had closed before the PIN");
            if (squared.Contains(p.PositionId)) return PositionItem(p, IraCloseState.Done, null);
            if (!stillOpen.Contains(p.PositionId)) return PositionItem(p, IraCloseState.AlreadyClosed, "it closed in the meantime");
            string why = squareOff is { Done: false } ? Why(squareOff.Message) : "it is still open after the square-off";
            return PositionItem(p, IraCloseState.Failed, why);
        }).ToList();
    }

    /// <summary>Hands of their own for one item: a fresh DI scope, so concurrent items never share a database context.</summary>
    private async Task<T> WithHandsAsync<T>(Func<IIraActionHands, Task<T>> work)
    {
        if (scopes is null) return await work(hands);
        await using var scope = scopes.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<IIraActionHands>());
    }

    private static IraCloseItem RunItem(IraCloseRun run, IraCloseState state, string? reason) =>
        new(IraCloseItem.RunType, run.RunId, $"run {run.RunId}", $"{IraActionText.Strategy(run.Strategy)} on {run.Underlying}", state, reason);

    private static IraCloseItem PositionItem(IraClosePosition p, IraCloseState state, string? reason) =>
        new(IraCloseItem.PositionType, p.PositionId, IraActionText.PositionLabel(p), p.Side, state, reason);

    /// <summary>A desk refusal's reason without its "Not done: " head and full stop.</summary>
    private static string Why(string message)
    {
        string text = message.StartsWith("Not done: ", StringComparison.Ordinal) ? message["Not done: ".Length..] : message;
        return text.Trim().TrimEnd('.');
    }
}
