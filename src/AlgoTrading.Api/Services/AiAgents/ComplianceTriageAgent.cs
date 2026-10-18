using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The Compliance Triage: on Saturdays, the week's SEBI, NSE and BSE circulars and the brokers' notices (recorded once
/// a day by <c>RegulatoryCircularsRecorder</c>), each judged against what this desk is: whether it applies, what would
/// have to change, from when, and the exact words it rests on.
/// </summary>
/// <remarks>
/// <para>
/// Due from <see cref="AiSettings.ComplianceDay"/> (Saturday) at <see cref="AiSettings.ComplianceAfterIst"/> (10:00 IST)
/// for every circular recorded before then and within <see cref="AiSettings.ComplianceLookbackDays"/>, a batch of
/// <see cref="AiSettings.ComplianceBatchSize"/> a scheduler minute; a circular recorded during the week waits for the
/// next Saturday. A Saturday the API missed is caught up at the next chance, never inside a session.
/// </para>
/// <para>
/// Each record is checked before it is stored as good: the verdict and the area from the lists asked for, a change
/// named when it applies, the quote found in the circular's own text as it was handed over (its title, number and the
/// pages read from its document), and every date and time in its effective dates and desk changes written in that
/// text too (<see cref="CircularFacts"/>). A quote, a date or a time the model made up cannot pass. A record that fails
/// is sent back once with what failed, and only an answer that passes is kept. One report per circular, so the share
/// of good records is measured as the News Analyst's is.
/// </para>
/// <para>
/// Until 18 Oct 2026 the model was handed the first 2,500 characters of each circular. SEBI's of 15 Oct 2026 on the
/// closing auction session and market timings says "3:45 p.m." at character 3,301 and "December 07, 2026" at 9,549:
/// its triage said it applied and missed both. It is handed <see cref="TextChars"/> now, the start and the end of a
/// longer one.
/// </para>
/// </remarks>
public sealed class ComplianceTriageAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    IMarketSessionService sessions,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<ComplianceTriageAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    public const string SubjectPrefix = "circular:";

    /// <summary>
    /// The characters of a circular's text handed to the model, and so the most a quote can be checked against: a
    /// SEBI circular of seven pages is about 11,000. A longer text is handed as its start and its last
    /// <see cref="TailChars"/>, where the implementation dates usually are.
    /// </summary>
    public const int TextChars = 12_000;

    /// <summary>Of <see cref="TextChars"/>, how much comes from the end of a text too long to hand over whole.</summary>
    public const int TailChars = 3_000;

    /// <summary>The longest answer one call may write: a batch of circulars with their dates and desk changes.</summary>
    public const int MaxTokens = 8000;

    /// <summary>Effective dates and desk changes kept of one record at most.</summary>
    public const int MaxListed = 8;

    /// <summary>
    /// The shape of a report's data: 2 from 18 Oct 2026, with <c>effectiveDates</c> and <c>deskChanges</c>. A report
    /// written before it has neither.
    /// </summary>
    public const int Format = 2;

    public static readonly IReadOnlySet<string> Verdicts = new HashSet<string>(StringComparer.Ordinal) { "yes", "no", "unclear" };

    public static readonly IReadOnlySet<string> Areas = new HashSet<string>(StringComparer.Ordinal)
    {
        "retail algo", "f&o contracts", "risk and margins", "market hours", "settlement", "data and apis", "other",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.ComplianceTriage;

    /// <summary>One circular handed to the model: its id ("c12"), its report's subject and day, the text a quote is checked against.</summary>
    public sealed record Item(string Id, long CircularId, DateOnly Day, string Title, string Text, object Payload);

    /// <summary>What one record came to: the report's status, title, body, data and the reasons it is invalid.</summary>
    public sealed record Verdict(string Status, string Title, string Body, string Data, string Error);

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (AiAgentHours.InSession(nowUtc, sessions)) return false;
        var batch = await NextBatchAsync(nowUtc, cancellationToken);
        if (batch.Count == 0) return false;
        await TriageAsync(batch, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        return true;
    }

    /// <summary>"Run now": one circular by its id, or the next batch recorded in the lookback, whatever the day.</summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        List<Item> batch;
        if (long.TryParse(subjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
        {
            batch = await ItemsAsync(await db.RegulatoryCirculars.AsNoTracking().Where(c => c.Id == id).ToListAsync(cancellationToken));
        }
        else
        {
            batch = await NextBatchAsync(now, cancellationToken, cutoffUtc: now);
        }

        if (batch.Count == 0) return null;
        return (await TriageAsync(batch, cancellationToken)).FirstOrDefault();
    }

    /// <summary>
    /// When the week's triage opens: the latest <see cref="AiSettings.ComplianceDay"/> at
    /// <see cref="AiSettings.ComplianceAfterIst"/> that has passed, in UTC. Circulars recorded before it are due.
    /// </summary>
    public DateTime WeekCutoffUtc(DateTime nowUtc)
    {
        var s = settings.CurrentValue;
        var at = AiAgentHours.Clock(s.ComplianceAfterIst, new TimeOnly(10, 0));
        var day = AiAgentHours.LatestWeekly(nowUtc, s.ComplianceDay, at);
        return IstTime.FromIst(day.ToDateTime(at));
    }

    /// <summary>The scheduler's key for the last time circulars read again were triaged again (<see cref="NextBatchAsync"/>).</summary>
    public const string RereadKey = AiCatalog.ComplianceTriage + ":reread";

    /// <summary>
    /// The next circulars to read: recorded before the week's cutoff and within the lookback, not yet settled, oldest
    /// first; then, whatever the day, those whose text was read again whole after their report was written
    /// (<see cref="RegulatoryCircular.TextRereadUtc"/>), at most once every <see cref="AiReportWriter.RetryAfter"/>: a
    /// try no model answers leaves a good report as it was, and must not be made again every minute.
    /// </summary>
    public async Task<List<Item>> NextBatchAsync(DateTime nowUtc, CancellationToken cancellationToken, DateTime? cutoffUtc = null)
    {
        var s = settings.CurrentValue;
        var cutoff = cutoffUtc ?? WeekCutoffUtc(nowUtc);
        var since = nowUtc.AddDays(-Math.Max(1, s.ComplianceLookbackDays));
        int size = Math.Clamp(s.ComplianceBatchSize, 1, 10);

        var candidates = await db.RegulatoryCirculars.AsNoTracking()
            .Where(c => c.FirstSeenUtc >= since && c.FirstSeenUtc < cutoff)
            .OrderBy(c => c.FirstSeenUtc).ThenBy(c => c.Id)
            .Take(400)
            .ToListAsync(cancellationToken);
        var batch = new List<RegulatoryCircular>();
        if (candidates.Count > 0)
        {
            var due = await reports.DueAsync(AgentKey, AiReportSubject.Check, candidates.Select(c => Subject(c.Id)).ToList(), cancellationToken);
            batch.AddRange(candidates.Where(c => due.Contains(Subject(c.Id))).Take(size));
        }

        if (batch.Count < size && (schedule.LastWork(RereadKey) is not DateTime last || nowUtc - last >= AiReportWriter.RetryAfter))
        {
            var again = await RereadSinceReportAsync(since, cancellationToken);
            var more = again.Where(c => batch.All(b => b.Id != c.Id)).Take(size - batch.Count).ToList();
            if (more.Count > 0)
            {
                schedule.Worked(RereadKey, nowUtc);
                batch.AddRange(more);
            }
        }

        return await ItemsAsync(batch);
    }

    /// <summary>The circulars of the lookback whose text was read again after their report was last written, oldest first.</summary>
    private async Task<List<RegulatoryCircular>> RereadSinceReportAsync(DateTime since, CancellationToken cancellationToken)
    {
        var reread = await db.RegulatoryCirculars.AsNoTracking()
            .Where(c => c.TextRereadUtc != null && c.FirstSeenUtc >= since)
            .OrderBy(c => c.FirstSeenUtc).ThenBy(c => c.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        if (reread.Count == 0) return [];
        var subjects = reread.Select(c => Subject(c.Id)).ToList();
        var written = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AgentKey && r.SubjectType == AiReportSubject.Check && subjects.Contains(r.SubjectId))
            .Select(r => new { r.SubjectId, r.UpdatedUtc })
            .ToDictionaryAsync(r => r.SubjectId, r => r.UpdatedUtc, cancellationToken);
        return reread.Where(c => written.TryGetValue(Subject(c.Id), out var at) && at < c.TextRereadUtc).ToList();
    }

    private static Task<List<Item>> ItemsAsync(List<RegulatoryCircular> circulars) => Task.FromResult(circulars.Select(ItemOf).ToList());

    /// <summary>A circular as the model is handed it, and the text its quote, dates and times are checked against.</summary>
    public static Item ItemOf(RegulatoryCircular c)
    {
        string text = Handed(c.Excerpt);
        string id = $"c{c.Id.ToString(CultureInfo.InvariantCulture)}";
        string date = c.PublishedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? IstTime.DateOf(c.FirstSeenUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var payload = new
        {
            id,
            source = c.Source.ToUpperInvariant(),
            number = c.Number.Length > 0 ? c.Number : null,
            date,
            department = c.Department.Length > 0 ? c.Department : null,
            title = c.Title,
            text = text.Length > 0 ? text : null,
            note = text.Length == 0 ? "No text could be read from its document: judge on the title alone, and quote the title." : null,
        };
        return new Item(id, c.Id, c.PublishedOn ?? IstTime.DateOf(c.FirstSeenUtc), c.Title, $"{c.Title}\n{c.Number}\n{text}", payload);
    }

    /// <summary>
    /// The text of a circular as the model is handed it: whole up to <see cref="TextChars"/>, else its start and its
    /// last <see cref="TailChars"/> joined by " … ".
    /// </summary>
    public static string Handed(string? excerpt)
    {
        string t = (excerpt ?? string.Empty).Trim();
        if (t.Length <= TextChars) return t;
        return t[..(TextChars - TailChars)].TrimEnd() + " … " + t[^TailChars..].TrimStart();
    }

    private async Task<List<AiReport>> TriageAsync(List<Item> batch, CancellationToken cancellationToken)
    {
        string question = "Triage these circulars for the desk:\n" + JsonSerializer.Serialize(batch.Select(i => i.Payload), Json);
        string conversation = $"circulars-{batch[0].Id}";
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, MaxTokens, 0,
            conversation, "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        var written = new List<AiReport>();
        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            bool capacity = AiGateway.IsCapacityRefusal(result);
            logger.LogInformation("Compliance triage of {Count} circulars got no answer: {Error}", batch.Count, why);
            foreach (var item in batch)
            {
                written.Add(capacity
                    ? await reports.SaveTurnedAwayAsync(AgentKey, AiReportSubject.Check, Subject(item.CircularId), item.Day, result,
                        Clip(item.Title, 200), $"Turned away for capacity, not counted as a try: {why}", cancellationToken)
                    : await reports.SaveAsync(AgentKey, AiReportSubject.Check, Subject(item.CircularId), item.Day, AiReportStatus.Failed, result,
                        Clip(item.Title, 200), string.Empty, "{}", why, cancellationToken));
            }

            return written;
        }

        var byId = ReadItems(result.Text);
        bool cutOff = result.FinishReason == "length";
        var verdicts = new Dictionary<string, (Verdict Verdict, AiAskResult Call)>(StringComparer.Ordinal);
        foreach (var item in batch)
        {
            var verdict = byId is not null && byId.TryGetValue(item.Id, out var record) ? Check(record, item) : Missing(item, byId, result.Text, cutOff);
            verdicts[item.Id] = (verdict, result);
        }

        // One more try for the records that failed their check, told exactly what failed (17 Oct 2026: two write-ups of
        // the same circular were kept as invalid). An answer cut off, or not JSON at all, is a failure to retry later.
        var redo = batch.Where(i => verdicts[i.Id].Verdict.Status == AiReportStatus.Invalid).ToList();
        if (redo.Count > 0 && byId is not null && !cutOff)
        {
            var again = await gateway.AskAsync(new AiAskInput(
                AgentKey, null,
                [new AiMessage("user", question), new AiMessage("assistant", result.Text), new AiMessage("user", Repair(redo.Select(i => (i.Id, verdicts[i.Id].Verdict.Error))))],
                null, MaxTokens, 0, conversation, "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);
            var fixedById = again.Outcome == AiCallOutcome.Ok ? ReadItems(again.Text) : null;
            foreach (var item in redo)
            {
                if (fixedById is null || !fixedById.TryGetValue(item.Id, out var record)) continue;
                var second = Check(record, item);
                if (second.Status != AiReportStatus.Ok) continue;
                var data = JsonNode.Parse(second.Data)!.AsObject();
                data["repairedFrom"] = verdicts[item.Id].Verdict.Error;
                verdicts[item.Id] = (second with { Data = data.ToJsonString(Json) }, again);
            }
        }

        foreach (var item in batch)
        {
            var (v, call) = verdicts[item.Id];
            written.Add(await reports.SaveAsync(AgentKey, AiReportSubject.Check, Subject(item.CircularId), item.Day, v.Status, call,
                v.Title, v.Body, v.Data, v.Error, cancellationToken));
        }

        return written;
    }

    /// <summary>What the second try is told: each record that failed and why, and how to write it so it passes.</summary>
    public static string Repair(IEnumerable<(string Id, string Error)> failed)
    {
        var list = failed.ToList();
        return "These records failed the desk's check:\n" +
               string.Join("\n", list.Select(f => $"- {f.Id}: {f.Error}")) + "\n" +
               $"Write the JSON object again with only these circulars ({string.Join(", ", list.Select(f => f.Id))}). Copy the quote " +
               "word for word from the circular's text, as one passage. Write every date and time in effectiveDates and " +
               "deskChanges exactly as the circular's text writes it, and leave out any you cannot copy. Pick the area " +
               "from the list.";
    }

    /// <summary>The answer's records by id; null when none can be read. A record whole on its own counts when the answer is not.</summary>
    public static Dictionary<string, JsonObject>? ReadItems(string answer)
    {
        var whole = AiJson.Object(answer)?["circulars"] as JsonArray;
        var records = whole is not null ? whole.OfType<JsonObject>() : AiJson.ObjectsUnder(answer, "circulars");
        var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in records)
        {
            if (AiJson.Str(obj, "id") is string id) byId.TryAdd(id, obj);
        }

        return whole is null && byId.Count == 0 ? null : byId;
    }

    private static Verdict Missing(Item item, Dictionary<string, JsonObject>? byId, string answer, bool cutOff)
    {
        string title = Clip(item.Title, 200);
        if (cutOff) return new Verdict(AiReportStatus.Failed, title, string.Empty, "{}", "The answer was cut off at the token limit before this circular.");
        if (byId is null) return new Verdict(AiReportStatus.Failed, title, string.Empty, "{}", "The answer was not the JSON object asked for.");
        return Regex.IsMatch(answer, $"\"id\"\\s*:\\s*\"{Regex.Escape(item.Id)}\"", RegexOptions.IgnoreCase)
            ? new Verdict(AiReportStatus.Invalid, title, string.Empty, "{}", "Its record in the answer was not valid JSON.")
            : new Verdict(AiReportStatus.Invalid, title, string.Empty, "{}", "The answer left this circular out.");
    }

    /// <summary>
    /// One record checked against its circular: a verdict and an area from the lists, a change named when it applies,
    /// the quote found in the circular's text as handed over, and each effective date and desk change with every date
    /// and time in it written in that text. Answers the report's status, title, body, data and the reasons it is invalid.
    /// </summary>
    public static Verdict Check(JsonObject record, Item item)
    {
        var problems = new List<string>();
        string applies = AiJson.Str(record, "applies")?.Trim().ToLowerInvariant() ?? string.Empty;
        string area = AreaOf(AiJson.Str(record, "area"));
        string why = AiJson.Str(record, "why")?.Trim() ?? string.Empty;
        string change = AiJson.Str(record, "change")?.Trim() ?? string.Empty;
        string quote = AiJson.Str(record, "quote")?.Trim() ?? string.Empty;

        if (!Verdicts.Contains(applies)) problems.Add($"applies \"{Clip(applies, 20)}\" is not yes, no or unclear");
        if (!Areas.Contains(area)) problems.Add($"area \"{Clip(area, 30)}\" is not one of the listed areas");
        if (why.Length == 0) problems.Add("no reason given");
        if (applies == "yes" && change.Length == 0) problems.Add("it applies, but nothing says what would have to change");
        // A circular read from its title alone has a short text: a quote of most of its title is as good as it gets.
        int minQuote = Math.Min(12, AiFactCheck.Normalise(item.Title).Length);
        if (!AiFactCheck.QuotePassagesIn(quote, item.Text, minQuote)) problems.Add($"the quote \"{Clip(quote, 80)}\" is not in the circular's text");
        var dates = EffectiveDates(record, item, problems);
        var deskChanges = DeskChanges(record, item, problems);
        foreach (string missing in CircularFacts.Unsupported(change, item.Text))
        {
            problems.Add($"the change names {missing}, which the circular's text does not");
        }

        string verdict = applies switch { "yes" => "Applies", "no" => "Does not apply", _ => "Unclear" };
        var data = new JsonObject
        {
            ["format"] = Format,
            ["circularId"] = item.CircularId,
            ["applies"] = applies,
            ["area"] = area,
            ["why"] = why,
            ["change"] = change.Length > 0 ? change : null,
            ["effectiveDates"] = new JsonArray(dates.Select(d => (JsonNode)new JsonObject
            {
                ["date"] = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["what"] = d.What,
            }).ToArray()),
            ["deskChanges"] = new JsonArray(deskChanges.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
            ["quote"] = quote,
            ["title"] = item.Title,
        };

        var body = new StringBuilder();
        body.Append($"**{verdict} to the desk**{(area.Length > 0 ? $" ({area})" : string.Empty)}\n\n{why}\n");
        if (change.Length > 0) body.Append($"\n**What would have to change:** {change}\n");
        if (dates.Count > 0)
        {
            body.Append("\n**Takes effect:**\n");
            foreach (var d in dates) body.Append($"- {d.Date:yyyy-MM-dd}: {d.What}\n");
        }

        if (deskChanges.Count > 0)
        {
            body.Append("\n**On the desk:**\n");
            foreach (string c in deskChanges) body.Append($"- {c}\n");
        }

        if (quote.Length > 0) body.Append($"\n> {quote}\n");
        string title = $"{verdict}: {item.Title}";
        return problems.Count == 0
            ? new Verdict(AiReportStatus.Ok, Clip(title, 300), body.ToString().TrimEnd(), data.ToJsonString(Json), string.Empty)
            : new Verdict(AiReportStatus.Invalid, Clip(title, 300), body.ToString().TrimEnd(), data.ToJsonString(Json), string.Join("; ", problems));
    }

    /// <summary>One date something takes effect from, and what.</summary>
    public sealed record EffectiveDate(DateOnly Date, string What);

    /// <summary>
    /// The record's effective dates, earliest first: each a yyyy-MM-dd day the circular's text writes (in any of its
    /// forms), with what takes effect then, every date and time in that written in the text too.
    /// </summary>
    private static List<EffectiveDate> EffectiveDates(JsonObject record, Item item, List<string> problems)
    {
        var list = new List<EffectiveDate>();
        if (record["effectiveDates"] is null) return list;
        if (record["effectiveDates"] is not JsonArray array)
        {
            problems.Add("effectiveDates is not a list");
            return list;
        }

        var written = CircularFacts.Dates(item.Text);
        foreach (var node in array.Take(MaxListed))
        {
            if (node is not JsonObject e)
            {
                problems.Add("an effective date is not a {date, what} record");
                continue;
            }

            string raw = AiJson.Str(e, "date") ?? string.Empty;
            string what = AiJson.Str(e, "what") ?? string.Empty;
            if (CircularFacts.IsoDate(raw) is not DateOnly date)
            {
                problems.Add($"the effective date \"{Clip(raw, 20)}\" is not a yyyy-MM-dd date");
                continue;
            }

            if (!written.Contains(date)) problems.Add($"the effective date {raw} is not written in the circular's text");
            if (what.Length == 0) problems.Add($"the effective date {raw} does not say what takes effect");
            foreach (string missing in CircularFacts.Unsupported(what, item.Text))
            {
                problems.Add($"what takes effect on {raw} names {missing}, which the circular's text does not");
            }

            list.Add(new EffectiveDate(date, what));
        }

        return list.OrderBy(d => d.Date).ToList();
    }

    /// <summary>The record's desk changes, as lines of text, every date and time in each written in the circular's text.</summary>
    private static List<string> DeskChanges(JsonObject record, Item item, List<string> problems)
    {
        var list = new List<string>();
        if (record["deskChanges"] is null) return list;
        if (record["deskChanges"] is not JsonArray array)
        {
            problems.Add("deskChanges is not a list");
            return list;
        }

        foreach (var node in array.Take(MaxListed))
        {
            string? line = node is JsonValue v && v.TryGetValue(out string? s) ? s?.Trim() : null;
            if (line is null)
            {
                problems.Add("a desk change is not a line of text");
                continue;
            }

            if (line.Length == 0) continue;
            foreach (string missing in CircularFacts.Unsupported(line, item.Text))
            {
                problems.Add($"the desk change \"{Clip(line, 60)}\" names {missing}, which the circular's text does not");
            }

            list.Add(line);
        }

        return list;
    }

    /// <summary>The area as listed; of several given ("market hours, settlement"), the first that is listed.</summary>
    public static string AreaOf(string? raw)
    {
        string area = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (Areas.Contains(area)) return area;
        return area.Split([',', '/', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(Areas.Contains) ?? area;
    }

    /// <summary>The report subject of a circular.</summary>
    public static string Subject(long circularId) => SubjectPrefix + circularId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The circular a report's subject names, or null.</summary>
    public static long? CircularIdOf(string? subjectId) =>
        subjectId is not null && subjectId.StartsWith(SubjectPrefix, StringComparison.Ordinal)
        && long.TryParse(subjectId[SubjectPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            ? id
            : null;

    private static string Clip(string? text, int max)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
