namespace AlgoTrading.Api.Services;

/// <summary>
/// The names a person reads for a strategy. A strategy's id (its class name: what runs, backtests, the morning plan,
/// the AI Trader's grants and docs/strategies key on) never changes; only what a message shows does.
/// 28 Nov 2026, the owner's names for the trend family: TrendRider is Opener, TrendRiderV2 Breaker, TrendRiderV2BE
/// Guard, TrendRiderV2OB Sniper. The same list is in web/src/lib/strategyNames.ts and scripts/telegram_notifier.py.
/// </summary>
public static class StrategyNames
{
    private static readonly IReadOnlyDictionary<string, string> Owner = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TrendRider"] = "Opener",
        ["TrendRiderV2"] = "Breaker",
        ["TrendRiderV2BE"] = "Guard",
        ["TrendRiderV2OB"] = "Sniper",
    };

    /// <summary>The owner's name for <paramref name="id"/>, or null when it has none.</summary>
    public static string? OwnName(string? id) => id is not null && Owner.TryGetValue(id.Trim(), out var name) ? name : null;

    /// <summary>The owner's name when the strategy has one, else the id unchanged.</summary>
    public static string Display(string? id) => OwnName(id) ?? id ?? string.Empty;
}
