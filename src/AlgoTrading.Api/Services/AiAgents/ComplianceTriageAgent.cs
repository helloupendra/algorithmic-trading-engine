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
/// The Compliance Triage: on Saturdays, the week's SEBI, NSE and BSE circulars (recorded once a day by
/// <c>RegulatoryCircularsRecorder</c>), each judged against what this desk is: whether it applies, what would have to
/// change, and the exact words it rests on.
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
/// named when it applies, and the quote found in the circular's own text as it was handed over (its title, number
/// and the first pages read from its document). A quote the model made up cannot pass. One report per circular, so the
/// share of good records is measured as the News Analyst's is.
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

    /// <summary>The characters of a circular's text handed to the model, and so the most a quote can be checked against.</summary>
    public const int TextChars = 2500;

    public static readonly IReadOnlySet<string> Verdicts = new HashSet<string>(StringComparer.Ordinal) { "yes", "no", "unclear" };

    public static readonly IReadOnlySet<string> Areas = new HashSet<string>(StringComparer.Ordinal)
    {
        "retail algo", "f&o contracts", "risk and margins", "market hours", "data and apis", "other",
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

    /// <summary>The next circulars to read: recorded before the week's cutoff and within the lookback, not yet settled, oldest first.</summary>
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
        if (candidates.Count == 0) return [];
        var due = await reports.DueAsync(AgentKey, AiReportSubject.Check, candidates.Select(c => Subject(c.Id)).ToList(), cancellationToken);
        return await ItemsAsync(candidates.Where(c => due.Contains(Subject(c.Id))).Take(size).ToList());
    }

    private static Task<List<Item>> ItemsAsync(List<RegulatoryCircular> circulars) => Task.FromResult(circulars.Select(ItemOf).ToList());

    /// <summary>A circular as the model is handed it, and the text its quote is checked against.</summary>
    public static Item ItemOf(RegulatoryCircular c)
    {
        string text = Clip(c.Excerpt, TextChars);
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

    private async Task<List<AiReport>> TriageAsync(List<Item> batch, CancellationToken cancellationToken)
    {
        string question = "Triage these circulars for the desk:\n" + JsonSerializer.Serialize(batch.Select(i => i.Payload), Json);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 6000, 0,
            $"circulars-{batch[0].Id}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

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
        foreach (var item in batch)
        {
            var (status, title, body, data, error) = byId is not null && byId.TryGetValue(item.Id, out var record)
                ? Check(record, item)
                : Missing(item, byId, result.Text, cutOff);
            written.Add(await reports.SaveAsync(AgentKey, AiReportSubject.Check, Subject(item.CircularId), item.Day, status, result,
                title, body, data, error, cancellationToken));
        }

        return written;
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

    private static (string Status, string Title, string Body, string Data, string Error) Missing(Item item, Dictionary<string, JsonObject>? byId, string answer, bool cutOff)
    {
        string title = Clip(item.Title, 200);
        if (cutOff) return (AiReportStatus.Failed, title, string.Empty, "{}", "The answer was cut off at the token limit before this circular.");
        if (byId is null) return (AiReportStatus.Failed, title, string.Empty, "{}", "The answer was not the JSON object asked for.");
        return Regex.IsMatch(answer, $"\"id\"\\s*:\\s*\"{Regex.Escape(item.Id)}\"", RegexOptions.IgnoreCase)
            ? (AiReportStatus.Invalid, title, string.Empty, "{}", "Its record in the answer was not valid JSON.")
            : (AiReportStatus.Invalid, title, string.Empty, "{}", "The answer left this circular out.");
    }

    /// <summary>
    /// One record checked against its circular: a verdict and an area from the lists, a change named when it applies,
    /// and the quote found in the circular's text as handed over. Answers the report's status, title, body, data and
    /// the reasons it is invalid.
    /// </summary>
    public static (string Status, string Title, string Body, string Data, string Error) Check(JsonObject record, Item item)
    {
        var problems = new List<string>();
        string applies = AiJson.Str(record, "applies")?.Trim().ToLowerInvariant() ?? string.Empty;
        string area = AiJson.Str(record, "area")?.Trim().ToLowerInvariant() ?? string.Empty;
        string why = AiJson.Str(record, "why")?.Trim() ?? string.Empty;
        string change = AiJson.Str(record, "change")?.Trim() ?? string.Empty;
        string quote = AiJson.Str(record, "quote")?.Trim() ?? string.Empty;

        if (!Verdicts.Contains(applies)) problems.Add($"applies \"{Clip(applies, 20)}\" is not yes, no or unclear");
        if (!Areas.Contains(area)) problems.Add($"area \"{Clip(area, 30)}\" is not one of the listed areas");
        if (why.Length == 0) problems.Add("no reason given");
        if (applies == "yes" && change.Length == 0) problems.Add("it applies, but nothing says what would have to change");
        // A circular read from its title alone has a short text: a quote of most of its title is as good as it gets.
        int minQuote = Math.Min(12, AiFactCheck.Normalise(item.Title).Length);
        if (!AiFactCheck.QuoteIn(quote, item.Text, minQuote)) problems.Add($"the quote \"{Clip(quote, 80)}\" is not in the circular's text");

        string verdict = applies switch { "yes" => "Applies", "no" => "Does not apply", _ => "Unclear" };
        var data = new JsonObject
        {
            ["circularId"] = item.CircularId,
            ["applies"] = applies,
            ["area"] = area,
            ["why"] = why,
            ["change"] = change.Length > 0 ? change : null,
            ["quote"] = quote,
            ["title"] = item.Title,
        };

        var body = new StringBuilder();
        body.Append($"**{verdict} to the desk**{(area.Length > 0 ? $" ({area})" : string.Empty)}\n\n{why}\n");
        if (change.Length > 0) body.Append($"\n**What would have to change:** {change}\n");
        if (quote.Length > 0) body.Append($"\n> {quote}\n");
        string title = $"{verdict}: {item.Title}";
        return problems.Count == 0
            ? (AiReportStatus.Ok, Clip(title, 300), body.ToString().TrimEnd(), data.ToJsonString(Json), string.Empty)
            : (AiReportStatus.Invalid, Clip(title, 300), body.ToString().TrimEnd(), data.ToJsonString(Json), string.Join("; ", problems));
    }

    /// <summary>The report subject of a circular.</summary>
    public static string Subject(long circularId) => SubjectPrefix + circularId.ToString(CultureInfo.InvariantCulture);

    private static string Clip(string? text, int max)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
