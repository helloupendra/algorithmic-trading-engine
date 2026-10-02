using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The News Analyst: every few minutes, a batch of recent headlines and
/// filings it has not read, turned into one structured record each.
/// </summary>
/// <remarks>
/// <para>
/// Only items from the last <see cref="AiSettings.NewsLookbackHours"/> by
/// their own publication time: the recorders' 2020 backfills are history for
/// research, not work for a model on a free tier.
/// </para>
/// <para>
/// Each record is checked before it is stored as good. The event and
/// direction must be from the lists asked for, the confidence between 0 and
/// 1. Every number must carry a quote found in the item's own text, so a
/// number the model made up cannot pass. A record that fails is kept as
/// <see cref="AiReportStatus.Invalid"/> with the reason, which is what the
/// roadmap's "JSON validity above 98%" is measured on.
/// </para>
/// </remarks>
public sealed class NewsAnalystAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<NewsAnalystAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    public static readonly IReadOnlySet<string> Events = new HashSet<string>(StringComparer.Ordinal)
    {
        "results", "guidance", "order win", "rating change", "policy", "macro data", "corporate action", "management", "legal", "other", "none",
    };

    public static readonly IReadOnlySet<string> Directions = new HashSet<string>(StringComparer.Ordinal) { "positive", "negative", "neutral", "unclear" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.NewsAnalyst;

    /// <summary>One item handed to the model: its id ("n12" a headline, "f7" a filing), its text, and where its report goes.</summary>
    public sealed record Item(string Id, string SubjectType, string SubjectId, DateOnly Day, string Text, object Payload);

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var every = TimeSpan.FromMinutes(Math.Max(1, settings.CurrentValue.NewsEveryMinutes));
        if (schedule.LastWork(AgentKey) is DateTime last && nowUtc - last < every) return false;

        var batch = await NextBatchAsync(nowUtc, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        if (batch.Count == 0) return false;

        await AnalyseAsync(batch, cancellationToken);
        return true;
    }

    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var batch = await NextBatchAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
        if (batch.Count == 0) return null;
        var written = await AnalyseAsync(batch, cancellationToken);
        return written.FirstOrDefault();
    }

    /// <summary>The next items to read: filings first (fewer, and more often material), then headlines, newest first.</summary>
    public async Task<List<Item>> NextBatchAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var since = nowUtc.AddHours(-Math.Max(1, s.NewsLookbackHours));
        int size = Math.Clamp(s.NewsBatchSize, 1, 25);

        var filings = await db.CorporateAnnouncements.AsNoTracking()
            .Where(a => (a.AnnouncedUtc ?? a.FirstSeenUtc) >= since && a.FirstSeenUtc >= since)
            .OrderByDescending(a => a.AnnouncedUtc ?? a.FirstSeenUtc)
            .Take(size * 4)
            .Select(a => new { a.Id, a.Symbol, a.Company, a.Subject, a.Details, a.AnnouncedUtc, a.FirstSeenUtc })
            .ToListAsync(cancellationToken);
        var news = await db.NewsItems.AsNoTracking()
            .Where(n => (n.PublishedUtc ?? n.FirstSeenUtc) >= since && n.FirstSeenUtc >= since)
            .OrderByDescending(n => n.PublishedUtc ?? n.FirstSeenUtc)
            .Take(size * 4)
            .Select(n => new { n.Id, n.Source, n.Title, n.Summary, n.PublishedUtc, n.FirstSeenUtc })
            .ToListAsync(cancellationToken);

        var dueFilings = await reports.DueAsync(AgentKey, AiReportSubject.Filing, filings.Select(f => Id(f.Id)).ToList(), cancellationToken);
        var dueNews = await reports.DueAsync(AgentKey, AiReportSubject.News, news.Select(n => Id(n.Id)).ToList(), cancellationToken);

        var items = new List<Item>();
        foreach (var f in filings.Where(f => dueFilings.Contains(Id(f.Id))))
        {
            string details = FilingDetails(f.Details);
            items.Add(new Item($"f{f.Id}", AiReportSubject.Filing, Id(f.Id), IstTime.DateOf(f.AnnouncedUtc ?? f.FirstSeenUtc),
                FilingText(f.Subject, f.Details),
                new { id = $"f{f.Id}", kind = "exchange filing", symbol = f.Symbol, company = f.Company, subject = f.Subject, details }));
            if (items.Count >= size) return items;
        }

        foreach (var n in news.Where(n => dueNews.Contains(Id(n.Id))))
        {
            string summary = HeadlineSummary(n.Summary);
            items.Add(new Item($"n{n.Id}", AiReportSubject.News, Id(n.Id), IstTime.DateOf(n.PublishedUtc ?? n.FirstSeenUtc),
                HeadlineText(n.Title, n.Summary),
                new { id = $"n{n.Id}", kind = "headline", source = n.Source, title = n.Title, summary }));
            if (items.Count >= size) break;
        }

        return items;
    }

    /// <summary>A filing's details as the model is handed them: the first 800 characters.</summary>
    public static string FilingDetails(string? details) => Clip(details, 800);

    /// <summary>A headline's summary as the model is handed it: the first 500 characters.</summary>
    public static string HeadlineSummary(string? summary) => Clip(summary, 500);

    /// <summary>The text a filing's numbers are checked against: its subject and its details as handed over. The chat reads the same.</summary>
    public static string FilingText(string subject, string? details) => $"{subject}\n{FilingDetails(details)}";

    /// <summary>The text a headline's numbers are checked against: its title and its summary as handed over.</summary>
    public static string HeadlineText(string title, string? summary) => $"{title}\n{HeadlineSummary(summary)}";

    private async Task<List<AiReport>> AnalyseAsync(List<Item> batch, CancellationToken cancellationToken)
    {
        string question = "Extract the events from these items:\n" + JsonSerializer.Serialize(batch.Select(i => i.Payload), Json);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 6000, 0,
            $"news-{batch[0].Id}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        var written = new List<AiReport>();
        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            // The provider, not the desk: recorded as failed reports and retried.
            logger.LogInformation("News extraction of {Count} items failed: {Error}", batch.Count, why);
            foreach (var item in batch)
            {
                written.Add(await reports.SaveAsync(AgentKey, item.SubjectType, item.SubjectId, item.Day, AiReportStatus.Failed, result,
                    Clip(FirstLine(item.Text), 200), string.Empty, "{}", why, cancellationToken));
            }

            return written;
        }

        var byId = ReadItems(result.Text);
        bool cutOff = result.FinishReason == "length";
        foreach (var item in batch)
        {
            var (status, title, body, data, error) = byId is not null && byId.TryGetValue(item.Id, out var extracted)
                ? Check(extracted, item.Text)
                : Missing(item, byId, result.Text, cutOff);

            written.Add(await reports.SaveAsync(AgentKey, item.SubjectType, item.SubjectId, item.Day, status, result, title, body, data, error, cancellationToken));
        }

        return written;
    }

    /// <summary>
    /// An item with no record in the answer. Cut off at the token limit, it
    /// is failed and read again in a later batch: the batch was too long, not
    /// the model wrong. An answer with no JSON at all is failed too: the model
    /// did not answer the task (call 168 on 1 Oct returned its "thinking
    /// process" and stopped), so no item was judged and each is read again.
    /// Otherwise the model broke or left out this item's record.
    /// </summary>
    private static (string Status, string Title, string Body, string Data, string Error) Missing(
        Item item, Dictionary<string, JsonObject>? byId, string answer, bool cutOff)
    {
        string title = Clip(FirstLine(item.Text), 200);
        if (cutOff) return (AiReportStatus.Failed, title, string.Empty, "{}", "The answer was cut off at the token limit before this item.");
        if (byId is null) return (AiReportStatus.Failed, title, string.Empty, "{}", "The answer was not the JSON object asked for.");
        return Regex.IsMatch(answer, $"\"id\"\\s*:\\s*\"{Regex.Escape(item.Id)}\"", RegexOptions.IgnoreCase)
            ? (AiReportStatus.Invalid, title, string.Empty, "{}", "Its record in the answer was not valid JSON.")
            : (AiReportStatus.Invalid, title, string.Empty, "{}", "The answer left this item out.");
    }

    /// <summary>
    /// The answer's items by id; null when none can be read. When the answer
    /// is not one whole object, each record that is whole on its own still
    /// counts, so one broken record or a cut-off tail does not lose the batch.
    /// </summary>
    public static Dictionary<string, JsonObject>? ReadItems(string answer)
    {
        var whole = AiJson.Object(answer)?["items"] as JsonArray;
        var records = whole is not null ? whole.OfType<JsonObject>() : AiJson.ObjectsUnder(answer, "items");
        var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in records)
        {
            if (AiJson.Str(obj, "id") is string id) byId.TryAdd(id, obj);
        }

        return whole is null && byId.Count == 0 ? null : byId;
    }

    /// <summary>
    /// One extracted record, checked against its source text: the shape asked
    /// for, and every number's quote found in the text. Answers the report's
    /// status, title, body, data and the reason when it is invalid.
    /// </summary>
    public static (string Status, string Title, string Body, string Data, string Error) Check(JsonObject record, string sourceText)
    {
        var problems = new List<string>();
        string ev = AiJson.Str(record, "event")?.ToLowerInvariant() ?? string.Empty;
        string direction = AiJson.Str(record, "direction")?.ToLowerInvariant() ?? string.Empty;
        double? confidence = AiJson.Num(record, "confidence");
        string summary = AiJson.Str(record, "summary") ?? string.Empty;
        var symbols = AiJson.Strings(record, "symbols").Select(s => s.ToUpperInvariant()).Distinct().ToList();

        if (!Events.Contains(ev)) problems.Add($"event \"{ev}\" is not one of the listed events");
        if (!Directions.Contains(direction)) problems.Add($"direction \"{direction}\" is not positive, negative, neutral or unclear");
        if (confidence is null or < 0 or > 1) problems.Add("confidence is not a number from 0 to 1");

        var numbers = new JsonArray();
        string haystack = Normalise(sourceText);
        if (record["numbers"] is JsonArray given)
        {
            foreach (var n in given.OfType<JsonObject>())
            {
                string? quote = AiJson.Str(n, "quote");
                double? value = AiJson.Num(n, "value");
                if (value is null) problems.Add($"a number ({AiJson.Str(n, "what") ?? "?"}) has no numeric value");
                if (quote is null || !haystack.Contains(Normalise(quote), StringComparison.Ordinal))
                {
                    problems.Add($"the quote \"{Clip(quote ?? string.Empty, 60)}\" is not in the item's text");
                }

                numbers.Add(new JsonObject
                {
                    ["what"] = AiJson.Str(n, "what"),
                    ["value"] = value,
                    ["unit"] = AiJson.Str(n, "unit"),
                    ["quote"] = quote,
                });
            }
        }
        else if (record["numbers"] is not null)
        {
            problems.Add("numbers is not a list");
        }

        var data = new JsonObject
        {
            ["event"] = ev,
            ["direction"] = direction,
            ["symbols"] = new JsonArray(symbols.Select(s => (JsonNode)s).ToArray()),
            ["numbers"] = numbers,
            ["confidence"] = confidence,
            ["summary"] = summary,
        };

        string title = summary.Length > 0 ? summary : Clip(FirstLine(sourceText), 200);
        var body = new StringBuilder();
        body.Append($"**{(ev.Length > 0 ? ev : "?")}**, {(direction.Length > 0 ? direction : "?")}");
        if (symbols.Count > 0) body.Append(" · ").Append(string.Join(", ", symbols));
        if (confidence is double c) body.Append($" · confidence {c:0.00}");
        body.Append('\n');
        foreach (var n in numbers.OfType<JsonObject>())
        {
            body.Append($"\n- {AiJson.Str(n, "what")}: {AiJson.Num(n, "value")?.ToString(CultureInfo.InvariantCulture)} {AiJson.Str(n, "unit")} — \"{AiJson.Str(n, "quote")}\"");
        }

        return problems.Count == 0
            ? (AiReportStatus.Ok, Clip(title, 300), body.ToString().TrimEnd(), data.ToJsonString(Json), string.Empty)
            : (AiReportStatus.Invalid, Clip(title, 300), body.ToString().TrimEnd(), data.ToJsonString(Json), string.Join("; ", problems));
    }

    /// <summary>Lower case, one space between words, typographic quotes and dashes made plain: how a quote is matched.</summary>
    private static string Normalise(string text)
    {
        string plain = text.ToLowerInvariant()
            .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-').Replace(' ', ' ');
        return Regex.Replace(plain, @"\s+", " ").Trim();
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    private static string Clip(string? text, int max)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
