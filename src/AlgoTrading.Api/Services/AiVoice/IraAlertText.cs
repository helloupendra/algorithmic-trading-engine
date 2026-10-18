using System.Globalization;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiVoice;

/// <summary>
/// The words Ira says first (<see cref="IraAlertService"/>): each alert and briefing as one short message, read in the
/// chat and spoken by Polly in the same words. Built by code from the desk's own records; no model writes any of it, so
/// every number is the desk's.
/// </summary>
/// <remarks>
/// Short, plain sentences that read well aloud in an Indian voice: rupees with ₹ and Indian grouping
/// (<see cref="PatternAlertText.Price"/>, by hand, so a host without ICU writes the same), times in IST, a strategy's
/// name split into its words ("GhostTangentCrossings" is "Ghost Tangent Crossings"), no Markdown, no symbols Polly would
/// read out. A message is kept under <see cref="MaxChars"/>, cut after its last whole sentence.
/// </remarks>
public static partial class IraAlertText
{
    /// <summary>The longest message, spoken or read: about 25 seconds of speech.</summary>
    public const int MaxChars = 350;

    /// <summary>The longest reason the AI Trader's own words are cut to.</summary>
    public const int MaxReason = 140;

    // ---------- the AI Trader acted live ----------

    /// <summary>"The AI Trader bought 2 lots of NIFTY 25100 CE at ₹120.50, with a stop at ₹95 and a target at ₹170. Its reason: …"</summary>
    public static string Bought(string contract, int lots, decimal price, decimal? stop, decimal? target, string? reason)
    {
        string levels = (stop, target) switch
        {
            (decimal s, decimal t) => $", with a stop at {Rupees(s)} and a target at {Rupees(t)}",
            (decimal s, null) => $", with a stop at {Rupees(s)} and no target",
            (null, decimal t) => $", with a target at {Rupees(t)} and no stop",
            _ => ", with no stop or target",
        };
        return Fit($"The AI Trader bought {Lots(lots)} of {contract} at {Rupees(price)}{levels}.{Because(reason)}");
    }

    /// <summary>"The AI Trader exited 2 lots of NIFTY 25100 CE at ₹88.50. Its reason: …"; the lots or the price left out when unknown.</summary>
    public static string Exited(string contract, int? lots, decimal? price, string? reason)
    {
        string what = lots is int l and > 0 ? $"{Lots(l)} of {contract}" : contract;
        string at = price is decimal p ? $" at {Rupees(p)}" : string.Empty;
        return Fit($"The AI Trader exited {what}{at}.{Because(reason)}");
    }

    /// <summary>"The AI Trader started Ghost on NIFTY with 1 lot, run 512. Its reason: …"</summary>
    public static string Started(string strategy, string underlying, int lots, long? runId, string? reason)
    {
        string run = runId is long id ? $", run {Id(id)}" : string.Empty;
        return Fit($"The AI Trader started {Name(strategy)} on {underlying} with {Lots(lots)}{run}.{Because(reason)}");
    }

    /// <summary>"The AI Trader stopped run 512, Ghost on NIFTY, and squared off 2 positions. Its reason: …"</summary>
    public static string StoppedRun(string? strategy, string? underlying, long? runId, int flattened, string? reason)
    {
        string run = runId is long id ? $"run {Id(id)}" : "its run";
        string which = string.IsNullOrWhiteSpace(strategy) ? string.Empty
            : string.IsNullOrWhiteSpace(underlying) ? $", {Name(strategy)}," : $", {Name(strategy)} on {underlying},";
        string squared = flattened switch
        {
            0 => which.Length > 0 ? " and had no open position" : ", which had no open position",
            1 => " and squared off 1 position",
            _ => $" and squared off {flattened} positions",
        };
        return Fit($"The AI Trader stopped {run}{which}{squared}.{Because(reason)}");
    }

    /// <summary>A placement read back from a result this text does not know: what it did, in the console's words.</summary>
    public static string Acted(string what, string? reason) => Fit($"The AI Trader acted: {Sentence(what)}{Because(reason)}");

    // ---------- a run's risk rules ----------

    /// <summary>
    /// "Risk rules stopped Ghost on NIFTY in admin's account: its overall stop loss was hit. The run ended ₹5,210 down
    /// after charges."
    /// </summary>
    public static string RunStopped(string strategy, string underlying, string account, string reason, decimal net) =>
        Fit($"Risk rules stopped {Name(strategy)} on {underlying} in {account}'s account: its overall {RuleOf(reason)} was hit. " +
            $"The run ended {Signed(net)} after charges.");

    /// <summary>
    /// "Risk rules closed group G1 of Fulcrum on BANKNIFTY in admin's account: its group target was hit. The run is
    /// ₹1,240 up after charges so far."
    /// </summary>
    public static string GroupClosed(string strategy, string underlying, string account, string? group, string reason, decimal net)
    {
        string which = string.IsNullOrWhiteSpace(group) ? "a group" : $"group {group.Trim()}";
        return Fit($"Risk rules closed {which} of {Name(strategy)} on {underlying} in {account}'s account: its group {RuleOf(reason)} was hit. " +
                   $"The run is {Signed(net)} after charges so far.");
    }

    /// <summary>
    /// The rule a risk guard's reason names: "Stop loss hit: …" and "Group stop-loss hit: …" are a stop loss,
    /// "Trailing stop hit" and "Group trailing stop hit" a trailing stop, "Target hit" and "Group target hit" a target.
    /// </summary>
    public static string RuleOf(string? reason)
    {
        string r = (reason ?? string.Empty).Trim().ToLowerInvariant();
        if (r.StartsWith("group ", StringComparison.Ordinal)) r = r[6..];
        return r.StartsWith("trailing", StringComparison.Ordinal) ? "trailing stop"
            : r.StartsWith("stop loss", StringComparison.Ordinal) || r.StartsWith("stop-loss", StringComparison.Ordinal) ? "stop loss"
            : r.StartsWith("target", StringComparison.Ordinal) ? "target"
            : "risk rule";
    }

    // ---------- Sentinel ----------

    /// <summary>"Sentinel opened a critical incident: Dhan feed silent. No tick for 5 minutes since 10:42 IST."</summary>
    public static string Incident(string severity, string title, string? summary)
    {
        string first = (summary ?? string.Empty).Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        string tail = first.Length == 0 ? string.Empty : " " + Sentence(Cut(first, 160));
        return Fit($"Sentinel opened a {severity.Trim().ToLowerInvariant()} incident: {Sentence(Cut(Flat(title), 160))}{tail}");
    }

    // ---------- the briefings ----------

    /// <summary>
    /// "Good morning. Macro: lean bullish, size 0.80. Technical: NIFTY bullish B, BANKNIFTY neutral C, SENSEX bullish B.
    /// Options: NIFTY range-bound, BANKNIFTY bearish, SENSEX neutral. Morning plan: 13 of 13 runs started for admin, 10
    /// of 10 for coderforchange." What is missing is said plainly.
    /// </summary>
    public static string Morning(IraMorning m)
    {
        var parts = new List<string> { "Good morning." };
        parts.Add(m.Macro is { } macro ? $"Macro: {macro.Band}, size {macro.Size}." : "No Macro report yet today.");
        parts.Add(Indices("Technical", m.Technical, "not graded"));
        parts.Add(Indices("Options", m.Options, "not read"));
        parts.Add(Plan(m.Plan));
        return Fit(string.Join(' ', parts));
    }

    /// <summary>
    /// "The market has closed. Day net after charges: admin ₹12,340 down, coderforchange ₹2,100 up. Best run: Ghost on
    /// NIFTY for admin, ₹5,000 up. Worst run: Fulcrum on BANKNIFTY for admin, ₹86,804 down. The AI Trader acted twice;
    /// its book is ₹1,240 up after charges."
    /// </summary>
    public static string Close(IraClose c)
    {
        var parts = new List<string> { "The market has closed." };
        parts.Add(c.Accounts.Count == 0
            ? "No live runs today."
            : "Day net after charges: " + string.Join(", ", c.Accounts.Select(a => $"{a.Account} {Signed(a.Net)}")) + ".");
        if (c.Runs == 1 && c.Best is { } only)
        {
            parts.Add($"The only run: {Run(only)}.");
        }
        else if (c.Runs > 1 && c.Best is { } best && c.Worst is { } worst)
        {
            parts.Add($"Best run: {Run(best)}. Worst run: {Run(worst)}.");
        }

        parts.Add(Trader(c.Trader));
        return Fit(string.Join(' ', parts));
    }

    /// <summary>"And 3 more alerts since 10:05 IST: see Today on the console."</summary>
    public static string Folded(int count, DateTime sinceUtc) =>
        $"And {count} more alert{(count == 1 ? "" : "s")} since {Clock(sinceUtc)} IST: see Today on the console.";

    /// <summary><paramref name="text"/> with the "and N more" line after it, the text cut to leave it room.</summary>
    public static string WithFolded(string text, int count, DateTime? sinceUtc)
    {
        if (count <= 0 || sinceUtc is not DateTime since) return text;
        string line = Folded(count, since);
        return Fit(text, MaxChars - line.Length - 1) + " " + line;
    }

    // ---------- /alerts ----------

    /// <summary>What /alerts says while they are on for the owner.</summary>
    public static string AlertsOn(AiVoiceAlertsSettings s) =>
        "Alerts are on. I speak first when the AI Trader acts live, when risk rules stop a run or close a group, when " +
        "Sentinel opens a high or critical incident, and when a new circular applies to the desk, at most " +
        $"{Math.Max(1, s.MaxPerHour)} an hour. On NSE trading days I also " +
        $"brief you at {s.MorningAt} and {s.CloseAt} IST. /alerts off stops them.";

    /// <summary>What /alerts says while they are off for the owner.</summary>
    public const string AlertsOff = "Alerts are off for you: no alerts and no briefings. /alerts on brings them back.";

    /// <summary>Added to /alerts while the desk-wide switch is off.</summary>
    public const string DeskOff =
        " They are switched off for the whole desk for now (Ai:Voice:Alerts:Enabled), so nothing is sent until that is on again.";

    /// <summary>/alerts with anything but on or off after it.</summary>
    public const string AlertsUsage = "Use /alerts on or /alerts off; /alerts alone says where they stand.";

    // ---------- pieces ----------

    /// <summary>A price: "₹120.50", "₹95", "₹1,03,383".</summary>
    public static string Rupees(decimal value) => "₹" + PatternAlertText.Price(Math.Abs(value));

    /// <summary>A P&amp;L in whole rupees, as said: "₹5,210 down", "₹1,240 up", "flat".</summary>
    public static string Signed(decimal net)
    {
        decimal whole = Math.Round(net, 0, MidpointRounding.AwayFromZero);
        return whole == 0 ? "flat" : $"{Rupees(whole)} {(whole > 0 ? "up" : "down")}";
    }

    /// <summary>A strategy's name in its words: "GhostTangentCrossings" is "Ghost Tangent Crossings", "Fulcrum2StraddleBuy20" "Fulcrum 2 Straddle Buy 20".</summary>
    public static string Name(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return "a strategy";
        return Spaces().Replace(Words().Replace(strategy.Trim(), " "), " ").Trim();
    }

    /// <summary>
    /// The AI Trader's own reason, cut to its first sentence and at most <see cref="MaxReason"/> characters, after
    /// "Its reason:". Empty when it gave none.
    /// </summary>
    public static string Because(string? reason)
    {
        string flat = Flat(reason);
        if (flat.Length == 0) return string.Empty;
        var first = FirstSentence().Match(flat);
        string sentence = first.Success ? first.Groups[1].Value : flat;
        return " Its reason: " + Sentence(Cut(sentence, MaxReason));
    }

    /// <summary><paramref name="text"/> cut after its last whole sentence within <paramref name="max"/>; a first sentence too long is cut at a word.</summary>
    public static string Fit(string text, int max = MaxChars)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        int end = -1;
        foreach (Match m in SentenceEnd().Matches(text))
        {
            if (m.Index + 1 > max) break;
            end = m.Index + 1;
        }

        return end > 0 ? text[..end] : Cut(text, max);
    }

    private static string Run(IraRunNet r) => $"{Name(r.Strategy)} on {r.Underlying} for {r.Account}, {Signed(r.Net)}";

    private static string Trader(IraTraderDay? t)
    {
        if (t is null) return "The AI Trader was off.";
        if (t.Mode == AiTraderModes.Shadow)
        {
            return t.Positions == 0
                ? "The AI Trader, in shadow mode, made no trade."
                : $"The AI Trader, in shadow mode, made {t.Positions} paper trade{(t.Positions == 1 ? "" : "s")}, {Signed(t.Net)} after charges.";
        }

        return t.Placed == 0 && t.Positions == 0
            ? "The AI Trader made no trade."
            : $"The AI Trader acted {Times(t.Placed)}; its book is {Signed(t.Net)} after charges.";
    }

    private static string Indices(string label, IReadOnlyList<IraIndexView>? views, string missingWord)
    {
        if (views is null) return $"No {label} report yet today.";
        var missing = DeskAnalysts.Indices.Where(i => !views.Any(v => string.Equals(v.Index, i, StringComparison.OrdinalIgnoreCase))).ToList();
        string known = string.Join(", ", views.Select(v => $"{v.Index} {v.View}".Trim()));
        string gone = missing.Count == 0 ? string.Empty : $"{string.Join(" and ", missing)} {missingWord}";
        return known.Length == 0 ? $"{label}: {gone}." : gone.Length == 0 ? $"{label}: {known}." : $"{label}: {known}; {gone}.";
    }

    private static string Plan(IReadOnlyList<IraPlanAccount>? plan)
    {
        if (plan is null) return "The morning plan could not be read.";
        var accounts = plan.Where(a => a.Planned > 0).ToList();
        if (accounts.Count == 0) return "The morning plan has no runs today.";
        var first = accounts[0];
        string rest = string.Concat(accounts.Skip(1).Select(a => $", {a.Started} of {a.Planned} for {a.Account}"));
        return $"Morning plan: {first.Started} of {first.Planned} run{(first.Planned == 1 ? "" : "s")} started for {first.Account}{rest}.";
    }

    private static string Lots(int lots) => $"{lots} lot{(lots == 1 ? "" : "s")}";

    private static string Times(int n) => n switch { 1 => "once", 2 => "twice", _ => $"{n} times" };

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static string Clock(DateTime utc) => IstTime.ToIst(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>One line, single spaces.</summary>
    private static string Flat(string? text) => Spaces().Replace((text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' '), " ").Trim();

    /// <summary>Ends with a full stop unless it already ends a sentence.</summary>
    private static string Sentence(string text)
    {
        string t = text.Trim();
        return t.Length == 0 || t[^1] is '.' or '!' or '?' or '…' ? t : t + ".";
    }

    /// <summary>Cut at a word to fit <paramref name="max"/>, with "…".</summary>
    private static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        int at = text.LastIndexOf(' ', Math.Max(0, max - 2));
        string head = at > 0 ? text[..at] : text[..(max - 1)];
        return head.TrimEnd(',', ';', ':', '-', ' ', '.') + "…";
    }

    [GeneratedRegex(@"^(.+?[.!?])(?=\s|$)")]
    private static partial Regex FirstSentence();

    [GeneratedRegex(@"[.!?](?=\s|$)")]
    private static partial Regex SentenceEnd();

    /// <summary>Where a name's words meet: a lower letter before a capital, a capital before a capital and a lower one, letters beside digits.</summary>
    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|(?<=[A-Za-z])(?=\d)|(?<=\d)(?=[A-Za-z])")]
    private static partial Regex Words();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
