using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiAgents;

// The desk analysts of Phase 4 (owner, 10 Oct 2026): the Technical, Options/OI, Macro and Fundamental Analysts. Each
// reads facts the code computed from the desk's own records, answers one JSON object, and code checks every level and
// number it states against the facts it was handed. What they share is here: the times they write at, the numbers
// check, the run of one report, and how their reports reach the AI Trader's brief and the daily digest.

/// <summary>One fixed time an analyst writes at, and how long after it a missed one is still worth writing.</summary>
public sealed record AnalystSlot(TimeOnly At, TimeSpan Window)
{
    public TimeOnly Until => At.Add(Window);
}

/// <summary>When the slot analysts write, and how their reports are named: one report per IST day and time.</summary>
public static class AnalystSchedule
{
    /// <summary>A slot's report subject (subject type <c>check</c>): "2026-10-12 08:20".</summary>
    public static string SubjectId(DateOnly day, TimeOnly at) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + at.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The day and time a slot subject names; null for anything else.</summary>
    public static (DateOnly Day, TimeOnly At)? Parse(string? subjectId) =>
        subjectId is { Length: 16 }
        && DateOnly.TryParseExact(subjectId[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
        && TimeOnly.TryParseExact(subjectId[11..], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? (day, at)
            : null;

    /// <summary>
    /// The slot whose window holds <paramref name="nowUtc"/> on an NSE trading day (the exchange calendar, not the
    /// weekday), or null: on a holiday, a weekend, or between the windows.
    /// </summary>
    public static (DateOnly Day, TimeOnly At)? Current(DateTime nowUtc, IReadOnlyList<AnalystSlot> slots, IMarketSessionService sessions)
    {
        if (!sessions.GetSessionInfo(nowUtc, "NSE", "FO").IsTradingDay) return null;
        var ist = IstTime.ToIst(nowUtc);
        var time = TimeOnly.FromDateTime(ist);
        foreach (var slot in slots)
        {
            if (time >= slot.At && time < slot.Until) return (DateOnly.FromDateTime(ist), slot.At);
        }

        return null;
    }

    /// <summary>The minute a moment falls in, as an IST time: "Run now" writes a report for that minute.</summary>
    public static (DateOnly Day, TimeOnly At) MinuteOf(DateTime utc)
    {
        var ist = IstTime.ToIst(utc);
        return (DateOnly.FromDateTime(ist), new TimeOnly(ist.Hour, ist.Minute));
    }
}

/// <summary>
/// The numbers an analyst was handed, as they were written to it. Every number its answer states must be one of them:
/// a level, a PCR, a percentage the model made up cannot pass, which is the News Analyst's quote check for numbers the
/// code computed rather than read.
/// </summary>
/// <remarks>
/// <para>
/// A number passes when it is a number handed over, or that number rounded to one place fewer; a price of 100 or more
/// may be rounded or cut to any fewer places, down to a whole number ("25,310" or "25,311" for 25,310.5, never
/// "25,312"). More places than were given is a number made up. Its sign is not read: "fell 0.13%" states −0.13 in
/// words.
/// </para>
/// <para>
/// What is not a stated number is taken out of a text first: times (09:30), dates (12 Oct, 2026-10-12), indicator
/// periods (EMA 20, ATR(14), 5-minute), index names (NIFTY 50, S&amp;P 500), "ATM+1", ordinals. A number written
/// straight after letters (EMA20, Q2, FY27, m3) is a name, not a number.
/// </para>
/// </remarks>
public sealed partial class FactNumbers
{
    private readonly List<(decimal Value, int Decimals)> _values = [];

    public int Count => _values.Count;

    /// <summary>Adds a number as it is written, rounded to <paramref name="decimals"/> places. Answers the written value.</summary>
    public decimal Add(decimal value, int decimals)
    {
        decimal written = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        _values.Add((written, decimals));
        return written;
    }

    /// <summary><see cref="Add(decimal, int)"/> for a number that may be missing.</summary>
    public decimal? Add(decimal? value, int decimals) => value is decimal v ? Add(v, decimals) : null;

    /// <summary><see cref="Add(decimal, int)"/>, answering these numbers: <c>new FactNumbers().Also(level, 2)</c>.</summary>
    public FactNumbers Also(decimal value, int decimals)
    {
        Add(value, decimals);
        return this;
    }

    /// <summary>Adds every number written in <paramref name="text"/> as it is written there (a filing's text).</summary>
    public FactNumbers AddFrom(string? text)
    {
        foreach (var (value, decimals, _) in Read(text)) _values.Add((value, decimals));
        return this;
    }

    /// <summary>These numbers and <paramref name="other"/>'s.</summary>
    public FactNumbers With(FactNumbers other)
    {
        var all = new FactNumbers();
        all._values.AddRange(_values);
        all._values.AddRange(other._values);
        return all;
    }

    /// <summary>Whether <paramref name="number"/>, as the model wrote it, is a number handed over.</summary>
    public bool Allows(decimal number)
    {
        decimal n = Math.Abs(Plain(number));
        int decimals = Places(n);
        foreach (var (value, given) in _values)
        {
            decimal v = Math.Abs(value);
            if (decimals > given) continue;
            if (v >= 100)
            {
                // A price shortened to fewer places, rounded or cut: 25,310.5 may be written 25,311 or 25,310.
                if (Math.Round(v, decimals, MidpointRounding.AwayFromZero) == n || Math.Round(v, decimals, MidpointRounding.ToZero) == n) return true;
                continue;
            }

            if (decimals >= given - 1 && Math.Round(v, decimals, MidpointRounding.AwayFromZero) == n) return true;
        }

        return false;
    }

    /// <summary>The numbers <paramref name="text"/> states that were not handed over, as written; empty when every one was.</summary>
    public List<string> Unsupported(string? text) =>
        Read(text).Where(x => !Allows(x.Value)).Select(x => x.Raw).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The numbers a text states, as written, after what is not a stated number has been taken out.</summary>
    public static List<(decimal Value, int Decimals, string Raw)> Read(string? text)
    {
        var found = new List<(decimal, int, string)>();
        if (string.IsNullOrWhiteSpace(text)) return found;
        string plain = text.Replace('−', '-').Replace(' ', ' ');
        foreach (var pattern in NotNumbers) plain = pattern.Replace(plain, " ");
        foreach (Match m in Number().Matches(plain))
        {
            if (decimal.TryParse(m.Value.Replace(",", string.Empty), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value))
            {
                value = Plain(value);
                found.Add((value, Places(value), m.Value));
            }
        }

        return found;
    }

    /// <summary>A model's number in a JSON answer (a key level, a metric), as a decimal; null when it is not one.</summary>
    public static decimal? Of(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out decimal d)) return Plain(d);
        if (v.TryGetValue(out double x) && double.IsFinite(x)) return Plain((decimal)x);
        if (v.TryGetValue(out string? s)
            && decimal.TryParse(s.Replace(",", string.Empty).Replace("₹", string.Empty).Replace("%", string.Empty).Trim(),
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out decimal p))
        {
            return Plain(p);
        }

        return null;
    }

    /// <summary>Lower case, one space between words, typographic quotes and dashes made plain: how a quote is matched.</summary>
    public static string Normalise(string text)
    {
        string plain = text.ToLowerInvariant()
            .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-').Replace('−', '-').Replace(' ', ' ');
        return Whitespace().Replace(plain, " ").Trim();
    }

    /// <summary>The value without trailing zeros: 25100.00 is 25100.</summary>
    private static decimal Plain(decimal value) => value / 1.000000000000000000000000000000000m;

    private static int Places(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    /// <summary>A month's name or its three letters, as a whole word: "decline" is not December.</summary>
    private const string Month =
        "(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)\\b\\.?";

    /// <summary>What a text says with digits that is not a stated number. A day is one or two digits, so "25000 may" stays a number.</summary>
    private static readonly Regex[] NotNumbers =
    [
        new(@"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.CultureInvariant),
        new(@"\b\d{1,2}:\d{2}(?::\d{2})?\b", RegexOptions.CultureInvariant),
        new($@"\b\d{{1,2}}(?:st|nd|rd|th)?\s+{Month}(?:,?\s+\d{{4}}\b)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new($@"\b{Month}\s+\d{{1,2}}(?:st|nd|rd|th)?\b(?:,?\s+\d{{4}}\b)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new($@"\b{Month}\s+\d{{4}}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(?:ema|sma|atr|rsi)\s*\(?\s*\d+\s*\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b\d+\s*-?\s*(?:minutes?|mins?|hours?|hrs?)\b|\b\d+[mh]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b\d+\s*-\s*year\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b(?:nifty\s*(?:next\s*)?50|s&p\s*500|nasdaq\s*100|ftse\s*100|nikkei\s*225|(?:euro\s*)?stoxx\s*50|sensex\s*30)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\batm\s*[+-]\s*\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\b\d+(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    /// <summary>A number as written: Indian or Western grouping, a fraction; not one written straight after letters (EMA20, Q2).</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(?:\d{1,3}(?:,\d{2,3})+|\d+)(?:\.\d+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>What code made of an analyst's answer: the report's status, title, body and data, and why it failed its check.</summary>
public sealed record AnalystVerdict(string Status, string Title, string Body, JsonObject Data, string Error)
{
    /// <summary>An answer that is not the JSON object asked for: kept as invalid with its text as the body.</summary>
    public static AnalystVerdict NotJson(string title, string answer) =>
        new(AiReportStatus.Invalid, title, answer, new JsonObject(), "The answer was not the JSON object asked for; its text is kept as the body.");
}

/// <summary>The facts one analyst run hands the model, built by code.</summary>
public interface IAnalystFacts
{
    /// <summary>What the model reads.</summary>
    string Text { get; }

    /// <summary>The facts as stored with the report, so the report shows what its numbers were checked against.</summary>
    JsonNode Data { get; }

    /// <summary>Why there is nothing to read (no bars, no chain, no factor recorded); null when there is.</summary>
    string? Nothing { get; }
}

/// <summary>
/// An analyst that writes at fixed times of a trading day: one report per slot (subject type <c>check</c>, subject
/// "yyyy-MM-dd HH:mm"), written once and never again, a failed one tried again after
/// <see cref="AiReportWriter.RetryAfter"/> while its window lasts and up to <see cref="AiSettings.MaxReportAttempts"/>
/// tries. A slot missed (the API down through its window) is not written late: the facts would be of another moment.
/// </summary>
public abstract class SlotAnalystAgent<TFacts>(
    AiGateway gateway,
    AiReportWriter reports,
    IMarketSessionService sessions,
    ILogger logger,
    TimeProvider? time) : IAiScheduledAgent
    where TFacts : IAnalystFacts
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public abstract string AgentKey { get; }

    /// <summary>Its fixed times on a trading day.</summary>
    public abstract IReadOnlyList<AnalystSlot> Slots { get; }

    /// <summary>"Technical", "Options", "Macro": the start of each report's title.</summary>
    protected abstract string Label { get; }

    protected IMarketSessionService Sessions => sessions;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (await DueAsync(nowUtc, cancellationToken) is not (DateOnly day, TimeOnly at)) return false;
        await WriteAsync(day, at, nowUtc, cancellationToken);
        return true;
    }

    /// <summary>"Run now": a report for the current minute, whatever the schedule says.</summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var (day, at) = AnalystSchedule.MinuteOf(now);
        return await WriteAsync(day, at, now, cancellationToken);
    }

    /// <summary>The slot to write now (its window holds now, and it is not written or is due another try), or null.</summary>
    public async Task<(DateOnly Day, TimeOnly At)?> DueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (await SlotNowAsync(nowUtc, cancellationToken) is not (DateOnly day, TimeOnly at)) return null;
        var due = await reports.DueAsync(AgentKey, AiReportSubject.Check, [AnalystSchedule.SubjectId(day, at)], cancellationToken);
        return due.Count > 0 ? (day, at) : null;
    }

    /// <summary>The slot whose window holds now; an agent with conditional slots adds its own.</summary>
    protected virtual Task<(DateOnly Day, TimeOnly At)?> SlotNowAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
        Task.FromResult(AnalystSchedule.Current(nowUtc, Slots, sessions));

    /// <summary>The facts as of <paramref name="asOfUtc"/>, built by code.</summary>
    protected abstract Task<TFacts> FactsAsync(DateTime asOfUtc, CancellationToken cancellationToken);

    /// <summary>The answer checked against the facts.</summary>
    protected abstract AnalystVerdict Check(string answer, TFacts facts);

    /// <summary>The question: the facts, introduced.</summary>
    protected virtual string Question(TFacts facts, DateOnly day, TimeOnly at) => facts.Text;

    private async Task<AiReport> WriteAsync(DateOnly day, TimeOnly at, DateTime nowUtc, CancellationToken cancellationToken)
    {
        string subject = AnalystSchedule.SubjectId(day, at);
        string head = $"{Label} {at.ToString("HH:mm", CultureInfo.InvariantCulture)}";
        TFacts facts;
        try
        {
            facts = await FactsAsync(nowUtc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The desk's side, not the model's: kept as a failed try, tried again in the slot's window.
            logger.LogWarning(ex, "{Agent}: the facts for {Subject} could not be built", AgentKey, subject);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, day, AiReportStatus.Failed, null,
                $"{head}: no report", string.Empty, "{}", $"The facts could not be built: {ex.GetType().Name}: {IncidentRedaction.Mask(ex.Message)}",
                cancellationToken);
        }

        if (facts.Nothing is string nothing)
        {
            // No model is asked about nothing: a failed try, so a recorder that comes back within the window still counts.
            logger.LogInformation("{Agent}: nothing to read for {Subject}: {Why}", AgentKey, subject, nothing);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, day, AiReportStatus.Failed, null,
                $"{head}: nothing to read", string.Empty, Wrap(new JsonObject(), facts, nowUtc, at), nothing, cancellationToken);
        }

        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", Question(facts, day, at))], null, 4000, 0.2,
            $"{AgentKey}-{subject}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            if (AiGateway.IsCapacityRefusal(result))
            {
                logger.LogInformation("{Agent}: {Subject} was turned away for capacity; tried again in the next window: {Why}", AgentKey, subject, why);
                return await reports.SaveTurnedAwayAsync(AgentKey, AiReportSubject.Check, subject, day, result, $"{head}: no report",
                    $"Turned away for capacity, not counted as a try: {why}", cancellationToken);
            }

            // The provider, not the desk: recorded as a failed report and retried.
            logger.LogInformation("{Agent}: {Subject} got no answer: {Why}", AgentKey, subject, why);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, day, AiReportStatus.Failed, result,
                $"{head}: no report", string.Empty, "{}", why, cancellationToken);
        }

        var verdict = Check(result.Text, facts);
        return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, day, verdict.Status, result,
            $"{head}: {verdict.Title}", verdict.Body, Wrap(verdict.Data, facts, nowUtc, at), verdict.Error, cancellationToken);
    }

    /// <summary>The report's data: what the model answered, as checked, beside the facts it was handed.</summary>
    private static string Wrap(JsonObject data, TFacts facts, DateTime nowUtc, TimeOnly at)
    {
        data["slot"] = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        data["asOf"] = IstTime.ToIst(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        data["facts"] = facts.Data.DeepClone();
        return data.ToJsonString(Json);
    }
}

/// <summary>The desk analysts by key, and what the rest of the desk reads of their reports.</summary>
public static class DeskAnalysts
{
    /// <summary>The four, in the catalog's order.</summary>
    public static readonly IReadOnlyList<string> Keys =
        [AiCatalog.TechnicalAnalyst, AiCatalog.OptionsAnalyst, AiCatalog.MacroAnalyst, AiCatalog.FundamentalAnalyst];

    /// <summary>The three whose reports are about a moment of the day (the brief reads these).</summary>
    public static readonly IReadOnlyList<string> SlotKeys = [AiCatalog.MacroAnalyst, AiCatalog.TechnicalAnalyst, AiCatalog.OptionsAnalyst];

    /// <summary>The short name a chat tool takes ("technical") for an analyst's key; null for anything else.</summary>
    public static string? KeyOf(string? shortName) => shortName?.Trim().ToLowerInvariant() switch
    {
        "technical" or AiCatalog.TechnicalAnalyst => AiCatalog.TechnicalAnalyst,
        "options" or "option" or "oi" or AiCatalog.OptionsAnalyst => AiCatalog.OptionsAnalyst,
        "macro" or AiCatalog.MacroAnalyst => AiCatalog.MacroAnalyst,
        "fundamental" or "fundamentals" or AiCatalog.FundamentalAnalyst => AiCatalog.FundamentalAnalyst,
        _ => null,
    };

    /// <summary>The NSE indices the slot analysts read, with their spot symbols.</summary>
    public static readonly IReadOnlyList<string> Indices = ["NIFTY", "BANKNIFTY", "SENSEX"];

    /// <summary>
    /// The brief's DESK ANALYSTS section: the latest checked report of the day of the Macro, Technical and Options
    /// Analysts, written at or before <paramref name="asOfUtc"/>. In a replay the clock is the replay's, so a report
    /// written after the replayed moment never reaches it; a day with none says so.
    /// </summary>
    public static async Task<string> BriefSectionAsync(TradingDbContext db, DateTime asOfUtc, bool replay, CancellationToken cancellationToken)
    {
        var asOf = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc);
        var day = IstTime.DateOf(asOf);
        var latest = await LatestOkAsync(db, asOf, cancellationToken);
        if (latest.Count == 0)
        {
            return replay
                ? $"No analyst report was written for {day.ToString("d MMM", CultureInfo.InvariantCulture)} by {IstTime.ToIst(asOf):HH:mm} IST, the replay's clock."
                : "None written yet today.";
        }

        var lines = new List<string>();
        foreach (var key in SlotKeys)
        {
            if (!latest.TryGetValue(key, out var r) || AiJson.Object(r.DataJson) is not { } data) continue;
            string at = $"written {IstTime.ToIst(r.UpdatedUtc):HH:mm}";
            if (key == AiCatalog.MacroAnalyst)
            {
                lines.Add($"- Macro ({at}): {AiJson.Str(data, "bias") ?? "?"}, size ×{Size(data)}. {Cut(AiJson.Str(data, "reason"), 240)}".TrimEnd());
                continue;
            }

            foreach (var index in (data["indices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                string name = AiJson.Str(index, "underlying") ?? "?";
                if (key == AiCatalog.TechnicalAnalyst)
                {
                    var levels = (index["keyLevels"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                        .Select(l => FactNumbers.Of(l["price"])?.ToString(CultureInfo.InvariantCulture)).Where(p => p is not null).ToList();
                    lines.Add($"- Technical ({at}) {name}: {AiJson.Str(index, "bias") ?? "?"}, grade {AiJson.Str(index, "grade") ?? "?"}" +
                              (levels.Count > 0 ? $", key levels {string.Join(" / ", levels)}" : string.Empty) + $". {Cut(AiJson.Str(index, "reason"), 160)}".TrimEnd());
                }
                else
                {
                    var said = AiJson.Strings(index, "lines");
                    lines.Add($"- Options ({at}) {name}: {AiJson.Str(index, "stance") ?? "?"}. {Cut(string.Join(" ", said.Take(2)), 220)}".TrimEnd());
                }
            }
        }

        return lines.Count == 0 ? "None written yet today." : string.Join('\n', lines);
    }

    /// <summary>One slot analyst's latest checked report of a day: when it was written and what it said.</summary>
    public sealed record LatestReport(long Id, DateTime UpdatedUtc, string DataJson);

    /// <summary>
    /// The latest <c>ok</c> report of the IST day of <paramref name="asOfUtc"/> of each slot analyst (Macro, Technical,
    /// Options/OI), written at or before that moment; an analyst with none that day is not in it. The brief's section
    /// and Ira's morning briefing read the same reports through it.
    /// </summary>
    public static async Task<Dictionary<string, LatestReport>> LatestOkAsync(TradingDbContext db, DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var asOf = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc);
        var day = IstTime.DateOf(asOf);
        var keys = SlotKeys.ToList();
        var rows = await db.AiReports.AsNoTracking()
            .Where(r => keys.Contains(r.AgentKey) && r.SubjectType == AiReportSubject.Check && r.SessionDate == day
                        && r.Status == AiReportStatus.Ok && r.UpdatedUtc <= asOf)
            .Select(r => new { r.Id, r.AgentKey, r.UpdatedUtc, r.DataJson })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.AgentKey).ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(r => r.UpdatedUtc).ThenByDescending(r => r.Id).Select(r => new LatestReport(r.Id, r.UpdatedUtc, r.DataJson)).First());
    }

    /// <summary>
    /// The daily digest's analyst sections: one short section for each slot analyst that wrote anything about
    /// <paramref name="day"/>, and for the Fundamental Analyst the results filings it read since
    /// <paramref name="filingsSinceUtc"/> (the last digest): results come out after the close, so a day's own digest
    /// has gone before most of them are read. None for an analyst that did not run.
    /// </summary>
    public static async Task<List<string>> DigestSectionsAsync(TradingDbContext db, DateOnly day, DateTime filingsSinceUtc, CancellationToken cancellationToken)
    {
        var slotKeys = SlotKeys.ToList();
        var rows = await db.AiReports.AsNoTracking()
            .Where(r => (slotKeys.Contains(r.AgentKey) && r.SessionDate == day)
                        || (r.AgentKey == AiCatalog.FundamentalAnalyst && r.UpdatedUtc > filingsSinceUtc && r.Status != AiReportStatus.Failed))
            .OrderBy(r => r.Id)
            .Select(r => new { r.AgentKey, r.SubjectId, r.Status, r.Title, r.UpdatedUtc, r.DataJson })
            .ToListAsync(cancellationToken);

        var sections = new List<string>();
        foreach (var key in Keys)
        {
            var mine = rows.Where(r => r.AgentKey == key).ToList();
            if (mine.Count == 0) continue;
            int ok = mine.Count(r => r.Status == AiReportStatus.Ok), invalid = mine.Count(r => r.Status == AiReportStatus.Invalid);
            int failed = mine.Count - ok - invalid;
            string counts = $"{ok} ok" + (invalid > 0 ? $", {invalid} invalid" : string.Empty) + (failed > 0 ? $", {failed} failed" : string.Empty);
            var last = mine.Where(r => r.Status == AiReportStatus.Ok).OrderBy(r => r.UpdatedUtc).LastOrDefault();
            var text = new StringBuilder();
            switch (key)
            {
                case AiCatalog.MacroAnalyst:
                    text.Append($"Macro Analyst ({counts})");
                    if (last is not null && AiJson.Object(last.DataJson) is { } macro)
                    {
                        text.Append($": {AiJson.Str(macro, "bias") ?? "?"}, size ×{Size(macro)}.");
                    }

                    break;
                case AiCatalog.FundamentalAnalyst:
                    var read = mine.Where(r => r.Status == AiReportStatus.Ok)
                        .Select(r => AiJson.Object(r.DataJson)).Where(d => d is not null && d["results"] is JsonValue v && v.TryGetValue(out bool b) && b)
                        .Select(d => d!).ToList();
                    int positive = read.Count(d => AiJson.Str(d, "surprise") == "positive"), negative = read.Count(d => AiJson.Str(d, "surprise") == "negative");
                    text.Append($"Fundamental Analyst: {mine.Count} results filing{(mine.Count == 1 ? "" : "s")} read since the last digest ({counts}): ")
                        .Append($"{positive} positive, {negative} negative, {read.Count - positive - negative} inline or unclear.");
                    foreach (var d in read.Where(d => AiJson.Str(d, "surprise") is "positive" or "negative").Take(3))
                    {
                        text.Append($"\n• {Escape(AiJson.Str(d, "symbol") ?? "?")}: {AiJson.Str(d, "surprise")}. {Escape(Cut(AiJson.Str(d, "summary"), 140))}".TrimEnd());
                    }

                    break;
                default:
                    string name = key == AiCatalog.TechnicalAnalyst ? "Technical Analyst" : "Options/OI Analyst";
                    text.Append($"{name}: {mine.Count} report{(mine.Count == 1 ? "" : "s")} ({counts})");
                    if (last is not null && AiJson.Object(last.DataJson) is { } slot)
                    {
                        var parts = (slot["indices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(i => key == AiCatalog.TechnicalAnalyst
                            ? $"{AiJson.Str(i, "underlying")} {AiJson.Str(i, "bias")} {AiJson.Str(i, "grade")}"
                            : $"{AiJson.Str(i, "underlying")} {AiJson.Str(i, "stance")}");
                        text.Append($". Latest, {AiJson.Str(slot, "slot") ?? "?"}: {Escape(string.Join(" · ", parts))}.");
                    }
                    else
                    {
                        text.Append('.');
                    }

                    break;
            }

            sections.Add(text.ToString());
        }

        return sections;
    }

    /// <summary>The checked size modifier, two places: "0.80".</summary>
    public static string Size(JsonObject macro) =>
        FactNumbers.Of(macro["sizeModifier"]) is decimal s ? s.ToString("0.00", CultureInfo.InvariantCulture) : "?";

    private static string Cut(string? text, int max)
    {
        var flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    /// <summary>A model's words in a message Telegram reads as HTML (see <see cref="AiDailyDigest"/>).</summary>
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
