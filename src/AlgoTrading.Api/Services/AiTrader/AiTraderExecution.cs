using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The AI Trader's hands in live mode (owner, 10 Oct 2026): what it may do in its own account and nowhere else. A buy
/// is a paper order in its own manual book through the order ticket's own path (<see cref="ManualOrderService"/>), its
/// stop and target on the position, intraday; an exit closes its own position on the path the risk guard uses
/// (<see cref="IPaperTradingService.ClosePositionsAsync"/>); a strategy start goes through the same checks as a
/// person's (<see cref="StrategyStartService"/>), and a stop stops only its own run.
/// </summary>
/// <remarks>
/// Nothing here decides: <see cref="AiTraderAgent"/> calls it under <see cref="AiTraderAgent.BookGate"/> once the rules
/// (and, for a buy or a start, the Validator) have allowed the plan, and re-judges a buy on the price it would pay.
/// </remarks>
public sealed class AiTraderExecution(
    TradingDbContext db,
    ManualOrderService orders,
    IPaperTradingService paperTrading,
    IAiTraderRuns runs,
    AiTraderValidator validator,
    AiTraderRiskMemo riskMemo,
    TimeProvider? time = null)
{
    /// <summary>A quote older than this is not bought on: the price it shows is not one it could have had.</summary>
    public static readonly TimeSpan QuoteFreshFor = TimeSpan.FromMinutes(3);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public AiTraderValidator Validator => validator;

    public AiTraderRiskMemo RiskMemo => riskMemo;

    /// <summary>
    /// Buys <paramref name="plan"/>'s lots of <paramref name="contract"/> in its own manual book at the ask, with the
    /// plan's stop and target on the position, intraday. <paramref name="recheck"/> judges the rules on the price it
    /// would pay, just before the order is booked; a refusal there books nothing.
    /// </summary>
    public async Task<AiTraderBuy> BuyAsync(AppUser account, AiTraderDecision decision, AiTraderPlan plan, AiTraderContract contract,
        Func<decimal, AiTraderVerdict> recheck, CancellationToken cancellationToken)
    {
        AiTraderVerdict? refused = null;
        string? stale = null;
        var now = _time.GetUtcNow().UtcDateTime;
        var outcome = await orders.PlaceAsync(new ManualOrderCommand(
            account.Id,
            AiTraderAccount.UserName,
            contract.Symbol,
            "BUY",
            plan.Lots ?? 0,
            null,
            plan.StopLoss,
            plan.Target,
            CarryForward: false,
            Source: new { agent = AiTraderAccount.UserName, decisionId = decision.Id },
            Confirm: price =>
            {
                if (price.QuoteUtc is not DateTime at || now - at > QuoteFreshFor)
                {
                    stale = price.QuoteUtc is DateTime old
                        ? $"The quote for {contract.Symbol} is {(int)(now - old).TotalSeconds} s old (more than {(int)QuoteFreshFor.TotalMinutes} min): not bought on it."
                        : $"No quote for {contract.Symbol}: nothing to buy at.";
                    return stale;
                }

                var verdict = recheck(price.Price);
                if (verdict.Allowed) return null;
                refused = verdict;
                return verdict.Why;
            }), cancellationToken);

        return new AiTraderBuy(outcome.Fill, refused, refused is null && stale is null ? outcome.RefusalMessage : stale);
    }

    /// <summary>
    /// Closes its own open position <paramref name="positionId"/> at the bid, the risk guard's way. The bid it saw just
    /// before is kept, so the Execution Reporter can say what the fill cost against it. Null when it was no longer open.
    /// </summary>
    public async Task<AiTraderExit?> ExitAsync(AppUser account, AiTraderDecision decision, long positionId, string reason, CancellationToken cancellationToken)
    {
        var book = await ManualBook.FindAsync(db, account.Id, cancellationToken);
        if (book is null) return null;
        var position = await db.PaperPositions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == positionId && p.SimulationRunId == book.Id && p.Status == "Open", cancellationToken);
        if (position is null) return null;

        var quote = await db.LiveQuotesLatest.AsNoTracking().FirstOrDefaultAsync(q => q.Symbol == position.Symbol, cancellationToken);
        long lastOrder = await db.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == book.Id).MaxAsync(o => (long?)o.Id, cancellationToken) ?? 0;

        int closed = await paperTrading.ClosePositionsAsync(book.Id, [positionId],
            Cut($"AI Trader exit (decision #{decision.Id}): {reason}", 300), AiTraderAccount.UserName, CancellationToken.None);
        if (closed == 0) return null;

        var order = await db.PaperOrders.AsNoTracking()
            .Where(o => o.SimulationRunId == book.Id && o.Id > lastOrder && o.Symbol == position.Symbol && o.GroupId == position.GroupId)
            .OrderByDescending(o => o.Id)
            .FirstOrDefaultAsync(CancellationToken.None);
        return new AiTraderExit(book.Id, positionId, position.Symbol, order?.Id, order?.FillPrice, quote?.BidPrice, quote?.UpdatedUtc);
    }

    /// <summary>Starts <paramref name="strategy"/> on <paramref name="underlying"/> in its own account, as a person's start would.</summary>
    public Task<AiTraderRunStart> StartAsync(AppUser account, string strategy, string underlying, int lots, CancellationToken cancellationToken) =>
        runs.StartAsync(account, strategy, underlying, lots, cancellationToken);

    /// <summary>Stops its own run <paramref name="runId"/>, squaring off its positions; another account's run is refused.</summary>
    public Task<AiTraderRunStop> StopAsync(AppUser account, long runId, string reason, CancellationToken cancellationToken) =>
        runs.StopAsync(account, runId, reason, cancellationToken);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>A buy's outcome: the fill, or the rules' refusal on the price it would pay, or why else nothing was booked.</summary>
public sealed record AiTraderBuy(ManualOrderFill? Fill, AiTraderVerdict? Refused, string? Error);

/// <summary>An exit's fill: its book, the position, the order, the price, and the bid seen just before.</summary>
public sealed record AiTraderExit(long BookRunId, long PositionId, string Symbol, long? OrderId, decimal? Price, decimal? BidSeen, DateTime? QuoteUtc);

public sealed record AiTraderRunStart(bool Started, long? RunId, string Message);

public sealed record AiTraderRunStop(bool Stopped, int Flattened, string Message);

/// <summary>Starts and stops the AI Trader's strategy runs (<see cref="AiTraderRuns"/>; a fake in tests).</summary>
public interface IAiTraderRuns
{
    Task<AiTraderRunStart> StartAsync(AppUser account, string strategy, string underlying, int lots, CancellationToken cancellationToken);

    Task<AiTraderRunStop> StopAsync(AppUser account, long runId, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Its strategy runs through the desk's own start and stop: <see cref="StrategyStartService"/> with every check a
/// person's start meets (the market's hours, the duplicate and capacity checks, the host's memory, its account's grants
/// and run cap), and <see cref="StrategyRunControl.StopAsync"/> for a run of its own.
/// </summary>
public sealed class AiTraderRuns(
    StrategyStartService starts,
    StrategyCatalogService catalog,
    StrategyRunControl control,
    StrategyProcessRegistry registry,
    IRiskLimitsStore limits,
    IDerivativesInstrumentService derivatives,
    IHostMemory memory,
    IMarketSessionService? sessions) : IAiTraderRuns
{
    public async Task<AiTraderRunStart> StartAsync(AppUser account, string strategy, string underlying, int lots, CancellationToken cancellationToken)
    {
        var entry = await catalog.FindByNameAsync(strategy, cancellationToken);
        if (entry is null) return new AiTraderRunStart(false, null, $"{strategy} is not in the strategy catalog.");

        var outcome = await starts.StartAsync(entry.Id, new StartStrategyRequest { Underlying = underlying, Lots = lots },
            new StrategyStarter(account.Id, account.UserName, IsAdmin: false), limits, derivatives, memory, sessions, cancellationToken);
        return outcome.Started
            ? new AiTraderRunStart(true, outcome.RunId, outcome.Message)
            : new AiTraderRunStart(false, outcome.RunId, outcome.Message);
    }

    public async Task<AiTraderRunStop> StopAsync(AppUser account, long runId, string reason, CancellationToken cancellationToken)
    {
        var running = registry.Get(runId);
        if (running is null || running.UserId != account.Id)
        {
            return new AiTraderRunStop(false, 0, $"Run {runId} is not one of its running strategy runs.");
        }

        var result = await control.StopAsync(runId, reason, flatten: true, by: AiTraderAccount.UserName, cancellationToken);
        return result.WasRunning
            ? new AiTraderRunStop(true, result.Flattened, $"Stopped run {runId}; squared off {result.Flattened} position(s).")
            : new AiTraderRunStop(false, 0, $"Run {runId} was no longer running.");
    }
}

/// <summary>
/// What a live decision placed, read back from its result (<see cref="AiTraderDecision.ResultJson"/>) for the console,
/// Today and the digest.
/// </summary>
public static class AiTraderResults
{
    /// <summary>The order, exit, start or stop a decision placed, with where to see it; null when it placed nothing.</summary>
    public static AiTraderPlacementView? Placement(AiTraderDecision d) => d.Executed ? Placement(d.ResultJson) : null;

    public static AiTraderPlacementView? Placement(string? resultJson)
    {
        if (Parse(resultJson) is not JsonObject o) return null;
        if (o["placed"] is JsonObject p)
        {
            long? order = Long(p["orderId"]);
            long? position = Long(p["positionId"]);
            long? book = Long(p["bookRunId"]);
            string text = $"Order #{order}: bought {Int(p["lots"])} lot(s) of {Str(p["symbol"])} at {Price(p["price"])}" +
                          $" (position #{position}, stop {Price(p["stopLoss"])}, target {Price(p["target"])})";
            return new AiTraderPlacementView("order", text, book is long b ? $"/trade/runs/{b}" : null, order, position, book);
        }

        if (o["exit"] is JsonObject x)
        {
            long? order = Long(x["orderId"]);
            long? position = Long(x["positionId"]);
            long? book = Long(x["bookRunId"]);
            string text = $"Order #{order}: sold position #{position} ({Str(x["symbol"])}) at {Price(x["price"])}";
            return new AiTraderPlacementView("exit", text, book is long b ? $"/trade/runs/{b}" : null, order, position, book);
        }

        if (o["started"] is JsonObject s && Long(s["runId"]) is long started)
        {
            return new AiTraderPlacementView("run-start", $"Run #{started}: {Str(s["strategy"])} on {Str(s["underlying"])}, {Int(s["lots"])} lot(s)",
                $"/trade/runs/{started}", null, null, started);
        }

        if (o["stopped"] is JsonObject t && Long(t["runId"]) is long stopped)
        {
            return new AiTraderPlacementView("run-stop", $"Run #{stopped} stopped, {Int(t["flattened"])} position(s) squared off",
                $"/trade/runs/{stopped}", null, null, stopped);
        }

        return null;
    }

    /// <summary>Whether the Risk Memo tightened the plan a decision placed or was about to place.</summary>
    public static bool Tightened(string? resultJson) =>
        Parse(resultJson) is JsonObject o && o["riskMemo"] is JsonObject m && m["tightened"] is JsonValue v && v.TryGetValue(out bool t) && t;

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? Long(JsonNode? n) => n is JsonValue v && v.TryGetValue(out long l) ? l : null;

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue(out int i) ? i : 0;

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? string.Empty : string.Empty;

    private static string Price(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue(out decimal d) ? "₹" + d.ToString("0.00", CultureInfo.InvariantCulture) : "—";
}

/// <summary>What a live decision placed, in a line, with the console page to see it on (its manual book, or its run).</summary>
/// <param name="Kind">order, exit, run-start or run-stop.</param>
public sealed record AiTraderPlacementView(string Kind, string Text, string? Link, long? OrderId, long? PositionId, long? RunId);
