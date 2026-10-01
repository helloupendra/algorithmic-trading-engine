using System.Globalization;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The AI Trader's rules, enforced by code and never by the model (owner, 1 Oct 2026). The model's JSON
/// only proposes. Every plan is checked here before anything exists, and a refusal names the rule it broke.
/// </summary>
/// <remarks>
/// Pure: the caller gathers the facts (<see cref="AiTraderBook"/>, the contract it resolved and priced) and
/// this decides. So every rule has a test, and a replayed day is judged exactly like a live one.
/// </remarks>
public static class AiTraderGuard
{
    public static AiTraderVerdict Check(AiTraderPlan plan, AiTraderBook book, AiTraderRules rules, AiTraderContract? contract = null)
    {
        if (book.KillSwitch) return Refuse("kill-switch", "The desk's kill switch is on: nothing new is placed.");

        return plan.Action switch
        {
            AiTraderPlan.None => AiTraderVerdict.Ok("Nothing to do."),
            AiTraderPlan.Buy => CheckBuy(plan, book, rules, contract),
            AiTraderPlan.Exit => book.Open.Any(p => p.PositionId == plan.PositionId)
                ? AiTraderVerdict.Ok("Closing its own position.")
                : Refuse("own-book", $"Position {plan.PositionId?.ToString(CultureInfo.InvariantCulture) ?? "(none named)"} is not one of its open positions."),
            AiTraderPlan.StartStrategy => CheckStart(plan, book, rules),
            AiTraderPlan.StopStrategy => book.Runs.Any(r => r.RunId == plan.RunId)
                ? AiTraderVerdict.Ok("Stopping its own run.")
                : Refuse("own-runs", $"Run {plan.RunId?.ToString(CultureInfo.InvariantCulture) ?? "(none named)"} is not one of its running strategy runs."),
            _ => Refuse("action", $"\"{plan.Action}\" is not an action it may take (none, buy, exit, start_strategy, stop_strategy)."),
        };
    }

    private static AiTraderVerdict CheckBuy(AiTraderPlan plan, AiTraderBook book, AiTraderRules rules, AiTraderContract? contract)
    {
        if (OpeningClosed(book, rules) is { } hours) return hours;
        if (book.NetToday <= -rules.DailyLossLimit)
        {
            return Refuse("daily-loss", $"Today's net is {Rupees(book.NetToday)}, at or past the −{Rupees(rules.DailyLossLimit)} limit: nothing new opens today.");
        }

        if (book.OpenedToday >= rules.MaxTradesPerDay) return Refuse("trades-a-day", $"{book.OpenedToday} trades opened today, the most a day is {rules.MaxTradesPerDay}.");
        if (!rules.Underlyings.Contains(plan.Underlying ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return Refuse("instrument", $"Only {string.Join(", ", rules.Underlyings)} options; not \"{plan.Underlying}\".");
        }

        if (plan.Option is not ("CE" or "PE")) return Refuse("instrument", "The option is CE or PE.");
        if (contract is null) return Refuse("contract", "No listed contract with a price matches the plan's underlying, option and strike.");
        if (plan.Lots is not int lots || lots < 1 || lots > rules.MaxLotsPerTrade)
        {
            return Refuse("size", $"Lots must be 1 to {rules.MaxLotsPerTrade}; the plan says {plan.Lots?.ToString(CultureInfo.InvariantCulture) ?? "nothing"}.");
        }

        decimal premium = contract.Ask * lots * contract.LotSize;
        if (premium > rules.MaxPremiumPerTrade)
        {
            return Refuse("size", $"{lots} lot(s) of {contract.Symbol} at {Rupees(contract.Ask)} is {Rupees(premium)} of premium; the most a trade is {Rupees(rules.MaxPremiumPerTrade)}.");
        }

        if (book.Open.Count >= rules.MaxOpenPositions) return Refuse("open-positions", $"{book.Open.Count} positions are open, the most is {rules.MaxOpenPositions}.");
        decimal inUse = book.Open.Sum(p => p.PremiumInUse);
        if (inUse + premium > rules.Capital)
        {
            return Refuse("capital", $"{Rupees(inUse)} in use plus {Rupees(premium)} would pass the account's {Rupees(rules.Capital)}.");
        }

        if (plan.StopLoss is not decimal stop || stop <= 0 || stop >= contract.Ask)
        {
            return Refuse("stop", $"A stop-loss premium below the entry ({Rupees(contract.Ask)}) is required on every buy.");
        }

        decimal floor = contract.Ask * (1 - rules.MaxStopFraction);
        if (stop < floor)
        {
            return Refuse("stop", $"The stop {Rupees(stop)} is more than {rules.MaxStopFraction:P0} below the entry {Rupees(contract.Ask)}; the lowest is {Rupees(floor)}.");
        }

        if (plan.Target is not decimal target || target <= contract.Ask)
        {
            return Refuse("target", $"A target premium above the entry ({Rupees(contract.Ask)}) is required on every buy.");
        }

        return AiTraderVerdict.Ok($"Buy {lots} lot(s) of {contract.Symbol} at about {Rupees(contract.Ask)} ({Rupees(premium)}), stop {Rupees(stop)}, target {Rupees(target)}.");
    }

    private static AiTraderVerdict CheckStart(AiTraderPlan plan, AiTraderBook book, AiTraderRules rules)
    {
        if (OpeningClosed(book, rules) is { } hours) return hours;
        if (book.NetToday <= -rules.DailyLossLimit)
        {
            return Refuse("daily-loss", $"Today's net is {Rupees(book.NetToday)}, at or past the −{Rupees(rules.DailyLossLimit)} limit: no strategy starts today.");
        }

        if (!rules.Strategies.Contains(plan.Strategy ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return Refuse("strategy-list", $"Only {string.Join(", ", rules.Strategies)} may be started; not \"{plan.Strategy}\".");
        }

        if (!rules.Underlyings.Contains(plan.Underlying ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return Refuse("instrument", $"Only {string.Join(", ", rules.Underlyings)}; not \"{plan.Underlying}\".");
        }

        if (book.Runs.Count >= rules.MaxStrategyRuns) return Refuse("strategy-runs", $"{book.Runs.Count} of its runs are running, the most is {rules.MaxStrategyRuns}.");
        return AiTraderVerdict.Ok($"Start {plan.Strategy} on {plan.Underlying?.ToUpperInvariant()}.");
    }

    /// <summary>New positions and starts only on a trading day, between the opening and closing times (IST).</summary>
    private static AiTraderVerdict? OpeningClosed(AiTraderBook book, AiTraderRules rules)
    {
        var ist = TimeOnly.FromDateTime(IstTime.ToIst(book.ClockUtc));
        if (!book.TradingDay || ist < rules.OpenFrom || ist > rules.OpenUntil)
        {
            return Refuse("hours", $"New positions open only {rules.OpenFrom:HH:mm}–{rules.OpenUntil:HH:mm} IST on a trading day; it is {ist:HH:mm}{(book.TradingDay ? string.Empty : " on a day with no session")}.");
        }

        return null;
    }

    private static AiTraderVerdict Refuse(string rule, string why) => new(false, rule, why);

    public static string Rupees(decimal value) =>
        (value < 0 ? "−₹" : "₹") + Math.Abs(value).ToString("#,##0.##", CultureInfo.GetCultureInfo("en-IN"));
}

/// <summary>What the model proposed, read from its JSON. Nothing in it is trusted.</summary>
public sealed record AiTraderPlan(
    string Action, string? Underlying, string? Option, string? Strike, int? Lots, decimal? StopLoss, decimal? Target,
    string? Strategy, long? PositionId, long? RunId, string Reason, double? Confidence)
{
    public const string None = "none";
    public const string Buy = "buy";
    public const string Exit = "exit";
    public const string StartStrategy = "start_strategy";
    public const string StopStrategy = "stop_strategy";
}

/// <summary>The facts the rules are judged on, gathered by code at the decision's moment (live, or a replay's).</summary>
public sealed record AiTraderBook(
    DateTime ClockUtc, bool TradingDay, bool KillSwitch, decimal NetToday, int OpenedToday,
    IReadOnlyList<AiTraderOpenPosition> Open, IReadOnlyList<AiTraderRun> Runs);

public sealed record AiTraderOpenPosition(long PositionId, string Symbol, int Lots, decimal Entry, decimal PremiumInUse, decimal? Mark,
    decimal? StopLoss, decimal? Target, decimal UnrealizedPnl);

public sealed record AiTraderRun(long RunId, string Strategy, string Underlying, decimal NetPnl);

/// <summary>The contract a buy resolves to, with the price it would be bought at (the ask, else the last trade).</summary>
public sealed record AiTraderContract(string Symbol, string Underlying, string OptionType, decimal Strike, DateOnly Expiry, decimal Ask, int LotSize);

public sealed record AiTraderVerdict(bool Allowed, string Rule, string Why)
{
    public static AiTraderVerdict Ok(string why) => new(true, "ok", why);
}

/// <summary>The owner's limits (1 Oct 2026). Settings, so a change needs no deploy; these are the defaults.</summary>
public sealed class AiTraderRules
{
    public IReadOnlyList<string> Underlyings { get; init; } = ["NIFTY", "BANKNIFTY", "SENSEX"];
    public int MaxLotsPerTrade { get; init; } = 2;
    public decimal MaxPremiumPerTrade { get; init; } = 50_000m;
    public int MaxOpenPositions { get; init; } = 3;
    public decimal Capital { get; init; } = 500_000m;

    /// <summary>The stop may sit at most this far below the entry premium.</summary>
    public decimal MaxStopFraction { get; init; } = 0.40m;

    /// <summary>Net of charges: at or past it, nothing new opens that day.</summary>
    public decimal DailyLossLimit { get; init; } = 10_000m;

    public TimeOnly OpenFrom { get; init; } = new(9, 20);
    public TimeOnly OpenUntil { get; init; } = new(14, 45);
    public int MaxTradesPerDay { get; init; } = 10;
    public IReadOnlyList<string> Strategies { get; init; } = ["GhostTangentCrossings", "ChainFlowBuy"];
    public int MaxStrategyRuns { get; init; } = 3;
}
