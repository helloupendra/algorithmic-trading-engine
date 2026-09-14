namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>The single- and multi-candle shapes the pattern alerts recognise.</summary>
/// <remarks>
/// Chart patterns that span dozens of bars (head and shoulders, double tops,
/// triangles) are deliberately absent: they need swing-point detection and a
/// tolerance model of their own, not a threshold on one to three candles.
/// </remarks>
public enum CandlePattern
{
    Doji,
    DragonflyDoji,
    GravestoneDoji,
    Hammer,
    InvertedHammer,
    HangingMan,
    ShootingStar,
    BullishEngulfing,
    BearishEngulfing,
    Marubozu,
    InsideBar,
    MorningStar,
    EveningStar,
}

/// <summary>Which way a pattern argues. Marubozu takes the colour of its own candle.</summary>
public enum PatternDirection
{
    Neutral,
    Bullish,
    Bearish,
}

/// <summary>What the console and the Telegram message say about one pattern.</summary>
/// <param name="Key">snake_case, the same names strategies/indicators.py uses in its PATTERNS table.</param>
/// <param name="Name">Lower-case display name, as it reads inside a sentence.</param>
/// <param name="Direction">The pattern's usual direction; <see cref="CandlePattern.Marubozu"/> is resolved per candle.</param>
/// <param name="Bars">How many consecutive candles the definition reads, the pattern candle included.</param>
/// <param name="Suggests">One line on what a trader usually reads into it.</param>
/// <param name="Definition">The exact rule, thresholds included.</param>
/// <param name="PortedFrom">The Python function whose thresholds this reproduces, when there is one.</param>
public sealed record CandlePatternInfo(
    CandlePattern Pattern,
    string Key,
    string Name,
    PatternDirection Direction,
    int Bars,
    string Suggests,
    string Definition,
    string? PortedFrom);

/// <summary>The one table of names, directions and definitions.</summary>
public static class CandlePatternCatalog
{
    public static readonly IReadOnlyList<CandlePatternInfo> All =
    [
        new(CandlePattern.Doji, "doji", "doji", PatternDirection.Neutral, 1,
            "Indecision: buyers and sellers ended where they started. Watch the next candle.",
            "Body ≤ 10% of the range (range > 0). Reported as dragonfly or gravestone instead when one of those also holds.",
            null),
        new(CandlePattern.DragonflyDoji, "dragonfly_doji", "dragonfly doji", PatternDirection.Bullish, 1,
            "Sellers pushed price down and lost it all by the close: possible support.",
            "A doji whose upper wick ≤ 10% of the range, so open and close sit at the high.",
            null),
        new(CandlePattern.GravestoneDoji, "gravestone_doji", "gravestone doji", PatternDirection.Bearish, 1,
            "Buyers pushed price up and lost it all by the close: possible resistance.",
            "A doji whose lower wick ≤ 10% of the range, so open and close sit at the low.",
            null),
        new(CandlePattern.Hammer, "hammer", "hammer", PatternDirection.Bullish, 1,
            "Lower prices were rejected. Strongest after a fall.",
            "Body > 0, lower wick ≥ 2 × body, upper wick ≤ body. Shape only: the trend is not read.",
            "is_hammer"),
        new(CandlePattern.InvertedHammer, "inverted_hammer", "inverted hammer", PatternDirection.Bullish, 1,
            "After a fall, buyers tested higher prices: an early sign the selling is tiring.",
            "The shooting-star shape after a fall: close below the average close of the previous 5 candles.",
            null),
        new(CandlePattern.HangingMan, "hanging_man", "hanging man", PatternDirection.Bearish, 1,
            "After a rise, sellers briefly took control inside the candle: a warning the rise may stall.",
            "The hammer shape after a rise: close above the average close of the previous 5 candles.",
            null),
        new(CandlePattern.ShootingStar, "shooting_star", "shooting star", PatternDirection.Bearish, 1,
            "Higher prices were rejected. Strongest after a rise.",
            "Body > 0, upper wick ≥ 2 × body, lower wick ≤ body. Shape only: the trend is not read.",
            "is_shooting_star"),
        new(CandlePattern.BullishEngulfing, "bullish_engulfing", "bullish engulfing", PatternDirection.Bullish, 2,
            "Buyers overwhelmed the previous candle's selling.",
            "Previous candle bearish, this one bullish, close ≥ previous open, open ≤ previous close, body > previous body.",
            "is_bullish_engulfing"),
        new(CandlePattern.BearishEngulfing, "bearish_engulfing", "bearish engulfing", PatternDirection.Bearish, 2,
            "Sellers overwhelmed the previous candle's buying.",
            "Previous candle bullish, this one bearish, close ≤ previous open, open ≥ previous close, body > previous body.",
            "is_bearish_engulfing"),
        new(CandlePattern.Marubozu, "marubozu", "marubozu", PatternDirection.Neutral, 1,
            "One side controlled the whole candle and closed near its extreme: momentum.",
            "Body ≥ 80% of the range. Bullish or bearish by the candle's own colour.",
            "is_marubozu"),
        new(CandlePattern.InsideBar, "inside_bar", "inside bar", PatternDirection.Neutral, 2,
            "The range contracted inside the previous candle: a pause before a break either way.",
            "High ≤ previous high and low ≥ previous low, with a strictly smaller range that is not zero.",
            null),
        new(CandlePattern.MorningStar, "morning_star", "morning star", PatternDirection.Bullish, 3,
            "A fall, a pause, then buyers took back half the fall: a three-candle bottom.",
            "1: bearish, body ≥ 50% of its range. 2: body ≤ 30% of candle 1's body, its body top below candle 1's body midpoint. 3: bullish, body > candle 2's body, close above candle 1's body midpoint.",
            null),
        new(CandlePattern.EveningStar, "evening_star", "evening star", PatternDirection.Bearish, 3,
            "A rise, a pause, then sellers took back half the rise: a three-candle top.",
            "1: bullish, body ≥ 50% of its range. 2: body ≤ 30% of candle 1's body, its body bottom above candle 1's body midpoint. 3: bearish, body > candle 2's body, close below candle 1's body midpoint.",
            null),
    ];

    private static readonly Dictionary<CandlePattern, CandlePatternInfo> ByPattern = All.ToDictionary(x => x.Pattern);

    private static readonly Dictionary<string, CandlePatternInfo> ByKey =
        All.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static CandlePatternInfo Info(CandlePattern pattern) => ByPattern[pattern];

    public static string Key(CandlePattern pattern) => ByPattern[pattern].Key;

    public static bool TryParse(string? key, out CandlePattern pattern)
    {
        if (key is not null && ByKey.TryGetValue(key.Trim(), out var info))
        {
            pattern = info.Pattern;
            return true;
        }

        pattern = default;
        return false;
    }

    public static string DirectionKey(PatternDirection direction) => direction switch
    {
        PatternDirection.Bullish => "bullish",
        PatternDirection.Bearish => "bearish",
        _ => "neutral",
    };
}
