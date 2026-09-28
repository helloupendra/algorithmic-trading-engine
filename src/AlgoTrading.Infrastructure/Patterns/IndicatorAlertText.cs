using System.Globalization;
using System.Net;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>One rule that fired on one closed candle of one symbol: everything a row or a message needs.</summary>
public sealed record IndicatorOccurrence(
    string Symbol,
    int TimeframeMinutes,
    DateTime BarStartUtc,
    DateTime BarEndUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    int MinutesWithData,
    int MinutesExpected,
    IndicatorRule Rule,
    bool Up,
    IReadOnlyList<IndicatorValue> Values)
{
    /// <summary>"indicators:NSE:NIFTY50-INDEX:15m:20260915T0500Z:rsi-above(14,70)" — one alert per rule per candle, ever.</summary>
    public string DedupeKey =>
        $"indicators:{Symbol}:{TimeframeMinutes}m:{BarStartUtc.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture)}:{Rule.Key}";

    public string Direction => Up ? "up" : "down";

    public double? Value(string name)
    {
        foreach (var v in Values) if (v.Name == name) return v.Value;
        return null;
    }

    public static IndicatorOccurrence From(string symbol, IndicatorHit hit)
    {
        var b = hit.Candle;
        return new IndicatorOccurrence(symbol, b.TimeframeMinutes, b.StartUtc, b.EndUtc, b.Open, b.High, b.Low, b.Close,
            b.MinutesWithData, b.MinutesExpected, hit.Rule, hit.Up, hit.Values);
    }
}

/// <summary>
/// The words for indicator alerts, in the pattern alerts' voice: what happened,
/// on which candle, with the numbers that show it. Never what to do about it —
/// "RSI crossed above 70" is a fact about a candle; "overbought, sell" is not.
/// </summary>
public static class IndicatorAlertText
{
    /// <summary>"RSI(14) crossed above 70", "EMA(9) crossed below EMA(21)", "Supertrend(10, 3) flipped up", "Close crossed above VWAP".</summary>
    public static string What(IndicatorOccurrence o) => o.Rule.Kind switch
    {
        IndicatorRuleKind.RsiAbove => $"RSI({o.Rule.Period}) crossed above {IndicatorRule.Number(o.Rule.Level)}",
        IndicatorRuleKind.RsiBelow => $"RSI({o.Rule.Period}) crossed below {IndicatorRule.Number(o.Rule.Level)}",
        IndicatorRuleKind.EmaCross => $"EMA({o.Rule.Period}) crossed {(o.Up ? "above" : "below")} EMA({o.Rule.SlowPeriod})",
        IndicatorRuleKind.SupertrendFlip => $"Supertrend({o.Rule.Period}, {IndicatorRule.Number(o.Rule.Multiplier)}) flipped {(o.Up ? "up" : "down")}",
        _ => $"Close crossed {(o.Up ? "above" : "below")} VWAP",
    };

    /// <summary>The numbers behind it: "RSI 68.4 → 71.2", "EMA(9) 25,061.30, EMA(21) 25,058.90".</summary>
    public static string Numbers(IndicatorOccurrence o) => o.Rule.Kind switch
    {
        IndicatorRuleKind.RsiAbove or IndicatorRuleKind.RsiBelow =>
            $"RSI {Rsi(o.Value("rsiBefore"))} → {Rsi(o.Value("rsi"))}",
        IndicatorRuleKind.EmaCross =>
            $"EMA({o.Rule.Period}) {Level(o.Value("emaFast"))}, EMA({o.Rule.SlowPeriod}) {Level(o.Value("emaSlow"))}",
        IndicatorRuleKind.SupertrendFlip =>
            $"closed {(o.Up ? "above" : "below")} its band at {Level(o.Value("through"))}, line now {Level(o.Value("line"))}",
        _ => $"VWAP {Level(o.Value("vwap"))}",
    };

    /// <summary>"NIFTY 15m RSI(14) crossed above 70".</summary>
    public static string Title(IndicatorOccurrence o) =>
        $"{PatternAlertText.DisplayName(o.Symbol)} {o.TimeframeMinutes}m {What(o)}";

    /// <summary>
    /// "NIFTY 15m RSI(14) crossed above 70 at 10:30 IST — close 25,072; RSI 68.4 → 71.2".
    /// The time is the candle's start, as on a chart and in the pattern alerts.
    /// </summary>
    public static string Message(IndicatorOccurrence o) =>
        $"{Title(o)} at {PatternAlertText.IstClock(o.BarStartUtc)} IST — close {PatternAlertText.Price(o.Close)}; {Numbers(o)}{Partial(o)}";

    /// <summary>
    /// One Telegram message for every alert on candles that closed at the same
    /// minute, in parse_mode HTML: one line per symbol and candle, with every
    /// rule that fired on that candle on the same line.
    /// </summary>
    public static string TelegramMessage(DateTime closedAtUtc, IReadOnlyList<IndicatorOccurrence> occurrences)
    {
        var lines = new List<string>
        {
            $"<b>Indicator alerts · candles closed {PatternAlertText.IstClock(closedAtUtc)} IST</b>",
        };

        var candles = occurrences
            .GroupBy(o => (o.Symbol, o.TimeframeMinutes, o.BarStartUtc))
            .ToList();

        foreach (var candle in candles.Take(PatternAlertText.MaxTelegramLines))
        {
            // Only the pieces are escaped (a symbol can hold "&", as M&M does);
            // the separators are the message's own.
            var first = candle.First();
            var head = $"{PatternAlertText.DisplayName(first.Symbol)} {first.TimeframeMinutes}m at {PatternAlertText.IstClock(first.BarStartUtc)} IST — " +
                       $"close {PatternAlertText.Price(first.Close)}";
            var rules = candle.Select(o => WebUtility.HtmlEncode($"{What(o)} ({Numbers(o)})"));
            lines.Add($"{WebUtility.HtmlEncode(head)} · {string.Join(" · ", rules)}{WebUtility.HtmlEncode(Partial(first))}");
        }

        if (candles.Count > PatternAlertText.MaxTelegramLines)
        {
            lines.Add($"+{candles.Count - PatternAlertText.MaxTelegramLines} more on the Pattern alerts page.");
        }

        return string.Join('\n', lines);
    }

    private static string Partial(IndicatorOccurrence o) =>
        o.MinutesWithData < o.MinutesExpected ? $" ({o.MinutesWithData} of {o.MinutesExpected} minutes had data)" : string.Empty;

    private static string Rsi(double? value) =>
        value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    /// <summary>A price-like level, always to the paisa: an EMA of 25,061.3036 is 25,061.30.</summary>
    private static string Level(double? value)
    {
        if (value is not { } v || double.IsNaN(v) || double.IsInfinity(v)) return "—";
        var d = Math.Round((decimal)v, 2, MidpointRounding.AwayFromZero);
        var text = PatternAlertText.Price(d);
        return text.Contains('.') ? text : text + ".00";
    }
}
