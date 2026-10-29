using System.Globalization;
using System.Net;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.LevelAlerts;

/// <summary>The words of a level alert: its title, the line the console shows and the Telegram message.</summary>
/// <remarks>
/// Like the other market alerts, they say what happened ("NIFTY crossed above
/// 24,800"), never what to do about it.
/// </remarks>
public static class LevelAlertText
{
    /// <summary>"NIFTY" for NSE:NIFTY50-INDEX, "NIFTY PCR" for a chain, "CRUDEOIL26NOVFUT" for a future.</summary>
    public static string Subject(string kind, string target) =>
        kind == LevelAlertKinds.Pcr ? $"{target} PCR" : PatternAlertText.DisplayName(target);

    /// <summary>A price in the market's grouping (24,800 · 1,23,450.50), a PCR to two decimals.</summary>
    public static string Value(string kind, decimal value) =>
        kind == LevelAlertKinds.Pcr
            ? value.ToString("0.00", CultureInfo.InvariantCulture)
            : PatternAlertText.Price(value);

    /// <summary>"crosses above", "goes below", "touches": the rule in words, present tense.</summary>
    public static string Verb(string kind, string condition, bool past = false) => (kind, condition) switch
    {
        (LevelAlertKinds.Pcr, LevelAlertConditions.Above) => past ? "went above" : "goes above",
        (LevelAlertKinds.Pcr, LevelAlertConditions.Below) => past ? "went below" : "goes below",
        (_, LevelAlertConditions.Above) => past ? "crossed above" : "crosses above",
        (_, LevelAlertConditions.Below) => past ? "crossed below" : "crosses below",
        _ => past ? "touched" : "touches",
    };

    /// <summary>"NIFTY crosses above 24,800": what the rule watches for.</summary>
    public static string Describe(string kind, string target, string condition, decimal level) =>
        $"{Subject(kind, target)} {Verb(kind, condition)} {Value(kind, level)}";

    /// <summary>"NIFTY crossed above 24,800".</summary>
    public static string Title(string kind, string target, string condition, decimal level) =>
        $"{Subject(kind, target)} {Verb(kind, condition, past: true)} {Value(kind, level)}";

    /// <summary>
    /// "NIFTY crossed above 24,800: 24,812.35 at 10:41:10 IST, from 24,796.10 at 10:41:05. Note: breakout watch".
    /// A PCR names its capture and expiry: "NIFTY PCR went above 1.30: 1.32 in the 10:41 capture (04 Nov expiry), from 1.29 at 10:40."
    /// </summary>
    public static string Message(
        string kind, string target, string condition, decimal level,
        LevelSample previous, LevelSample current, string? context, string? note)
    {
        var title = Title(kind, target, condition, level);
        var text = kind == LevelAlertKinds.Pcr
            ? $"{title}: {Value(kind, current.Value)} in the {Clock(current.AsOfUtc, seconds: false)} capture" +
              (context is null ? string.Empty : $" ({context})") +
              $", from {Value(kind, previous.Value)} at {Clock(previous.AsOfUtc, seconds: false)}."
            : $"{title}: {Value(kind, current.Value)} at {Clock(current.AsOfUtc, seconds: true)} IST, " +
              $"from {Value(kind, previous.Value)} at {Clock(previous.AsOfUtc, seconds: true)}.";

        return string.IsNullOrWhiteSpace(note) ? text : $"{text} Note: {note.Trim()}";
    }

    /// <summary>
    /// One Telegram message (parse_mode HTML) for the alerts of one scan. The
    /// owner's note is their own text: every line is escaped, never trusted as markup.
    /// </summary>
    public static string TelegramMessage(IReadOnlyList<LevelAlertFire> fires)
    {
        var lines = new List<string> { fires.Count == 1 ? "<b>Level alert</b>" : $"<b>Level alerts · {fires.Count}</b>" };
        lines.AddRange(fires.Select(f => WebUtility.HtmlEncode(f.Message)));
        return string.Join('\n', lines);
    }

    private static string Clock(DateTime utc, bool seconds) =>
        IstTime.ToIst(utc).ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);
}
