using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

// The scheduled agents' own work, read when the owner talks with them: the Trade Reviewer's reviews, the News
// Analyst's records, the Incident Explainer's explanations (ai_reports), each beside the thing it was about. A
// report's text was written by a model from desk data; it is masked like any free text before it leaves again.

/// <summary>Reads a report's structured part.</summary>
internal static class ReportData
{
    public static JsonObject Of(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    public static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    public static double? Num(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;

    public static int Count(JsonObject o, string key) => o[key] is JsonArray a ? a.Count : 0;

    public static List<string> Strings(JsonObject o, string key) =>
        o[key] is JsonArray a ? a.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() : [];

    public static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}

/// <summary><c>get_trade_reviews</c>: the Trade Reviewer's reviews with their verdicts, by day, by run or over the last days.</summary>
public sealed class TradeReviewsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int MaxRead = 400;
    private const int DefaultTake = 30;
    private const int MaxTake = 60;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.TradeReviews;

    public string Description =>
        "Your run reviews, newest first: one IST day's (date), the last days' (days, default 7), or one run's " +
        "(runId). Each: the run (strategy, account, symbol), its day, your verdict (followed, deviated, unclear), the " +
        "title, how many deviations and stale fills you found, the status (ok; invalid: the answer was not in the asked " +
        "shape; failed: no answer, tried again later) and when it was written. Filter by verdict. Totals by verdict. " +
        "get_trade_review reads one in full.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("date", AiToolSchema.Text("The runs' IST day as yyyy-MM-dd, or 'today' / 'yesterday'."), false),
        ("days", AiToolSchema.Integer("Without a date: the last days, 1 to 30, today included. Default 7."), false),
        ("runId", AiToolSchema.Integer("One run's review."), false),
        ("verdict", AiToolSchema.OneOf("Which verdicts. Default any.", "any", "followed", "deviated", "unclear"), false),
        ("take", AiToolSchema.Integer($"How many to list, 1 to {MaxTake}. Default {DefaultTake}."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        var date = args.Date("date", today);
        int days = args.Int("days", 1, 30) ?? 7;
        long? runId = args.Long("runId", 1);
        string verdict = args.String("verdict", 10)?.ToLowerInvariant() ?? "any";
        if (verdict is not ("any" or "followed" or "deviated" or "unclear")) throw new AiToolArgumentException("verdict is any, followed, deviated or unclear.");
        int take = args.Int("take", 1, MaxTake) ?? DefaultTake;

        var query = db.AiReports.AsNoTracking().Where(r => r.AgentKey == AiCatalog.TradeReviewer && r.SubjectType == AiReportSubject.Run);
        var since = today.AddDays(-(days - 1));
        if (runId is long id)
        {
            string subject = ReportData.Id(id);
            query = query.Where(r => r.SubjectId == subject);
        }
        else if (date is DateOnly on)
        {
            query = query.Where(r => r.SessionDate == on);
        }
        else
        {
            query = query.Where(r => r.SessionDate >= since);
        }

        var reports = await query.OrderByDescending(r => r.Id).Take(MaxRead).ToListAsync(cancellationToken);
        var runs = await RunsAsync(db, reports.Select(r => r.SubjectId), cancellationToken);
        var rows = reports.Select(r => (Report: r, Data: ReportData.Of(r.DataJson))).ToList();
        var listed = rows
            .Where(x => verdict == "any" || ReportData.Str(x.Data, "verdict") == verdict)
            .Take(take)
            .Select(x =>
            {
                var run = runs.GetValueOrDefault(x.Report.SubjectId);
                return new
                {
                    reviewId = x.Report.Id,
                    runId = long.TryParse(x.Report.SubjectId, out long rid) ? rid : (long?)null,
                    strategy = run?.Strategy,
                    account = run?.Account,
                    symbol = run?.Symbol,
                    day = x.Report.SessionDate?.ToString("yyyy-MM-dd"),
                    status = x.Report.Status,
                    verdict = ReportData.Str(x.Data, "verdict"),
                    title = Text(x.Report.Title, 200),
                    deviations = ReportData.Count(x.Data, "deviations"),
                    staleFills = ReportData.Num(x.Data, "staleFills"),
                    written = Ist(x.Report.UpdatedUtc),
                    tries = x.Report.Attempts,
                    error = Text(x.Report.Error, 200),
                };
            })
            .ToList();

        var ok = rows.Where(x => x.Report.Status == AiReportStatus.Ok).ToList();
        var data = new
        {
            date = runId is null ? date?.ToString("yyyy-MM-dd") : null,
            since = runId is null && date is null ? since.ToString("yyyy-MM-dd") : null,
            runId,
            reviews = listed,
            totals = new
            {
                reviews = rows.Count,
                followed = ok.Count(x => ReportData.Str(x.Data, "verdict") == "followed"),
                deviated = ok.Count(x => ReportData.Str(x.Data, "verdict") == "deviated"),
                unclear = ok.Count(x => ReportData.Str(x.Data, "verdict") == "unclear"),
                invalid = rows.Count(x => x.Report.Status == AiReportStatus.Invalid),
                failed = rows.Count(x => x.Report.Status == AiReportStatus.Failed),
            },
        };

        return new AiToolOutput(data, reports.Count == 0 ? null : reports.Max(r => r.UpdatedUtc), listed.Count,
            $"{listed.Count} reviews" + (verdict == "any" ? string.Empty : $" ({verdict})"));
    }

    internal sealed record RunFacts(string Strategy, string? Account, string Symbol, string Status, DateTime? StartedUtc, DateTime? CompletedUtc);

    /// <summary>The runs reviews are about, by their id as text: strategy, account and symbol.</summary>
    internal static async Task<Dictionary<string, RunFacts>> RunsAsync(TradingDbContext db, IEnumerable<string> subjectIds, CancellationToken cancellationToken)
    {
        var ids = subjectIds.Select(s => long.TryParse(s, out long id) ? id : 0).Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return [];
        var runs = await db.SimulationRuns.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.StrategyName, r.Symbol, r.UserId, r.Status, r.StartedUtc, r.CompletedUtc })
            .ToListAsync(cancellationToken);
        var userIds = runs.Select(r => r.UserId).Distinct().ToList();
        var users = await db.AppUsers.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);
        return runs.ToDictionary(r => ReportData.Id(r.Id),
            r => new RunFacts(r.StrategyName, users.GetValueOrDefault(r.UserId), r.Symbol, r.Status, r.StartedUtc, r.CompletedUtc));
    }
}

/// <summary><c>get_trade_review</c>: one review in full, by its id or its run's.</summary>
public sealed class TradeReviewTool(TradingDbContext db) : IAiTool
{
    public string Name => AiToolNames.TradeReview;

    public string Description =>
        "One of your run reviews in full, by the review's id or the run's: the run, your verdict, the journal, the " +
        "rules it kept, the deviations with their times, stale fills, the market context and the one thing worth " +
        "testing; a second review's verdict when a deviation was asked again; the status and any error.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("reviewId", AiToolSchema.Integer("The review's id, from get_trade_reviews."), false),
        ("runId", AiToolSchema.Integer("Or the run's id."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        long? reviewId = args.Long("reviewId", 1);
        long? runId = args.Long("runId", 1);
        var query = db.AiReports.AsNoTracking().Where(r => r.AgentKey == AiCatalog.TradeReviewer && r.SubjectType == AiReportSubject.Run);
        AiReport? report;
        if (reviewId is long rid)
        {
            report = await query.FirstOrDefaultAsync(r => r.Id == rid, cancellationToken)
                ?? throw new AiToolArgumentException($"No review {rid}; get_trade_reviews lists them.");
        }
        else if (runId is long run)
        {
            string subject = ReportData.Id(run);
            report = await query.FirstOrDefaultAsync(r => r.SubjectId == subject, cancellationToken)
                ?? throw new AiToolArgumentException(
                    $"No review of run {run}: a run is reviewed 10 minutes after it stops, after 15:45 IST on its day (an MCX run after MCX " +
                    "closes), from the last two days only; manual books, alert runs and recaps are not reviewed. get_trade_reviews lists the reviews.");
        }
        else
        {
            throw new AiToolArgumentException("Give reviewId or runId.");
        }

        var facts = (await TradeReviewsTool.RunsAsync(db, [report.SubjectId], cancellationToken)).GetValueOrDefault(report.SubjectId);
        var data = ReportData.Of(report.DataJson);
        var answer = new
        {
            reviewId = report.Id,
            runId = long.TryParse(report.SubjectId, out long id) ? id : (long?)null,
            run = facts is null
                ? null
                : new { strategy = facts.Strategy, account = facts.Account, symbol = facts.Symbol, status = facts.Status, started = Ist(facts.StartedUtc), stopped = Ist(facts.CompletedUtc) },
            day = report.SessionDate?.ToString("yyyy-MM-dd"),
            status = report.Status,
            verdict = ReportData.Str(data, "verdict"),
            title = Text(report.Title, 300),
            journal = Text(report.Body, 6000),
            followed = ReportData.Strings(data, "followed").Select(s => Text(s, 300)).ToList(),
            deviations = ReportData.Strings(data, "deviations").Select(s => Text(s, 400)).ToList(),
            staleFills = ReportData.Num(data, "staleFills"),
            marketContext = Text(ReportData.Str(data, "marketContext"), 500),
            worthTesting = Text(ReportData.Str(data, "lesson"), 400),
            secondReview = data["secondReview"] is JsonObject second ? second.DeepClone() : null,
            error = Text(report.Error, 400),
            tries = report.Attempts,
            written = Ist(report.UpdatedUtc),
            callId = report.CallId,
            model = report.Model.Length > 0 ? report.Model : null,
        };

        return new AiToolOutput(answer, report.UpdatedUtc, 1, $"Review of run {report.SubjectId}: {ReportData.Str(data, "verdict") ?? report.Status}");
    }
}

/// <summary><c>get_news_events</c>: the News Analyst's records of the items published in the last hours.</summary>
public sealed class NewsEventsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int MaxItems = 400;
    private const int DefaultTake = 30;
    private const int MaxTake = 60;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.NewsEvents;

    public string Description =>
        "Your records of the news, newest first by the item's publication time, over the last hours (default 24): for " +
        "each headline or exchange filing you read, the event, its direction, the symbols, your confidence and one-line " +
        "summary, and how many numbers you took from it; a record whose check failed is marked invalid with why. Items " +
        "in the window you have not read yet are counted. Filter by direction, event or symbol. get_news_event reads " +
        "one with the text you read.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("hours", AiToolSchema.Integer("How far back by publication time, 1 to 168. Default 24."), false),
        ("direction", AiToolSchema.OneOf("Which direction. Default any.", "any", "positive", "negative", "neutral", "unclear"), false),
        ("event", AiToolSchema.Text("One event type: results, guidance, order win, rating change, policy, macro data, corporate action, management, legal, other, none."), false),
        ("symbol", AiToolSchema.Text("Only records naming this NSE symbol."), false),
        ("status", AiToolSchema.OneOf("Which records. Default ok and invalid.", "read", "ok", "invalid", "failed", "any"), false),
        ("take", AiToolSchema.Integer($"How many to list, 1 to {MaxTake}. Default {DefaultTake}."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        int hours = args.Int("hours", 1, 168) ?? 24;
        string direction = args.String("direction", 10)?.ToLowerInvariant() ?? "any";
        if (direction != "any" && !NewsAnalystAgent.Directions.Contains(direction)) throw new AiToolArgumentException("direction is any, positive, negative, neutral or unclear.");
        string? ev = args.String("event", 20)?.ToLowerInvariant();
        if (ev is not null && !NewsAnalystAgent.Events.Contains(ev)) throw new AiToolArgumentException($"event is one of: {string.Join(", ", NewsAnalystAgent.Events)}.");
        string? symbol = args.String("symbol", 20)?.ToUpperInvariant();
        string status = args.String("status", 10)?.ToLowerInvariant() ?? "read";
        if (status is not ("read" or "ok" or "invalid" or "failed" or "any")) throw new AiToolArgumentException("status is read, ok, invalid, failed or any.");
        int take = args.Int("take", 1, MaxTake) ?? DefaultTake;
        var from = now.AddHours(-hours);

        var headlines = await db.NewsItems.AsNoTracking()
            .Where(n => (n.PublishedUtc ?? n.FirstSeenUtc) >= from && (n.PublishedUtc ?? n.FirstSeenUtc) <= now)
            .OrderByDescending(n => n.PublishedUtc ?? n.FirstSeenUtc).Take(MaxItems)
            .Select(n => new { n.Id, n.Source, n.Title, At = n.PublishedUtc ?? n.FirstSeenUtc })
            .ToListAsync(cancellationToken);
        var filings = await db.CorporateAnnouncements.AsNoTracking()
            .Where(a => (a.AnnouncedUtc ?? a.FirstSeenUtc) >= from && (a.AnnouncedUtc ?? a.FirstSeenUtc) <= now)
            .OrderByDescending(a => a.AnnouncedUtc ?? a.FirstSeenUtc).Take(MaxItems)
            .Select(a => new { a.Id, a.Symbol, a.Subject, At = a.AnnouncedUtc ?? a.FirstSeenUtc })
            .ToListAsync(cancellationToken);

        var newsIds = headlines.Select(n => ReportData.Id(n.Id)).ToList();
        var filingIds = filings.Select(a => ReportData.Id(a.Id)).ToList();
        var reports = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.NewsAnalyst
                        && ((r.SubjectType == AiReportSubject.News && newsIds.Contains(r.SubjectId))
                            || (r.SubjectType == AiReportSubject.Filing && filingIds.Contains(r.SubjectId))))
            .ToListAsync(cancellationToken);
        var byItem = reports.GroupBy(r => (r.SubjectType, r.SubjectId)).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.Id).First());

        var items = headlines.Select(n => (Item: $"n{n.Id}", Kind: "headline", At: n.At, Source: n.Source, Symbol: (string?)null, Title: n.Title,
                Report: byItem.GetValueOrDefault((AiReportSubject.News, ReportData.Id(n.Id)))))
            .Concat(filings.Select(a => (Item: $"f{a.Id}", Kind: "filing", At: a.At, Source: "exchange", Symbol: (string?)a.Symbol, Title: a.Subject,
                Report: byItem.GetValueOrDefault((AiReportSubject.Filing, ReportData.Id(a.Id))))))
            .OrderByDescending(x => x.At)
            .ToList();

        var read = items.Where(x => x.Report is not null).Select(x => (x.Item, x.Kind, x.At, x.Source, x.Symbol, x.Title, Report: x.Report!, Data: ReportData.Of(x.Report!.DataJson))).ToList();
        var listed = read
            .Where(x => status switch
            {
                "read" => x.Report.Status != AiReportStatus.Failed,
                "any" => true,
                _ => x.Report.Status == status,
            })
            .Where(x => direction == "any" || ReportData.Str(x.Data, "direction") == direction)
            .Where(x => ev is null || ReportData.Str(x.Data, "event") == ev)
            .Where(x => symbol is null || string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                        || ReportData.Strings(x.Data, "symbols").Contains(symbol, StringComparer.OrdinalIgnoreCase))
            .Take(take)
            .Select(x => new
            {
                reportId = x.Report.Id,
                item = x.Item,
                kind = x.Kind,
                published = Ist(x.At),
                source = x.Source,
                title = Text(x.Title, 200),
                status = x.Report.Status,
                @event = ReportData.Str(x.Data, "event"),
                direction = ReportData.Str(x.Data, "direction"),
                symbols = ReportData.Strings(x.Data, "symbols"),
                confidence = ReportData.Num(x.Data, "confidence"),
                summary = Text(ReportData.Str(x.Data, "summary"), 200),
                numbers = ReportData.Count(x.Data, "numbers"),
                error = Text(x.Report.Error, 200),
            })
            .ToList();

        var ok = read.Where(x => x.Report.Status == AiReportStatus.Ok).ToList();
        var data = new
        {
            from = Ist(from),
            to = Ist(now),
            records = listed,
            totals = new
            {
                itemsInWindow = items.Count,
                read = read.Count,
                notReadYet = items.Count - read.Count,
                ok = ok.Count,
                invalid = read.Count(x => x.Report.Status == AiReportStatus.Invalid),
                failed = read.Count(x => x.Report.Status == AiReportStatus.Failed),
                positive = ok.Count(x => ReportData.Str(x.Data, "direction") == "positive"),
                negative = ok.Count(x => ReportData.Str(x.Data, "direction") == "negative"),
                noEvent = ok.Count(x => ReportData.Str(x.Data, "event") == "none"),
            },
            note = items.Count >= MaxItems ? $"Only the newest {MaxItems} headlines and filings of the window are read; ask for fewer hours." : null,
        };

        return new AiToolOutput(data, now, listed.Count, $"{listed.Count} records of {items.Count} items in {hours} h");
    }
}

/// <summary><c>get_news_event</c>: one News Analyst record with the text it read.</summary>
public sealed class NewsEventTool(TradingDbContext db) : IAiTool
{
    public string Name => AiToolNames.NewsEvent;

    public string Description =>
        "One of your news records with the item it was about: the event, direction, symbols, every number with the " +
        "exact words it came from, your confidence and summary, why it was invalid if it was; and the item itself " +
        "(headline or filing, source, time, link) with the text you read, exactly as you were given it.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("reportId", AiToolSchema.Integer("The record's id, from get_news_events."), false),
        ("item", AiToolSchema.Text("Or the item: n123 for a headline, f45 for an exchange filing."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        long? reportId = args.Long("reportId", 1);
        string? item = args.String("item", 24)?.ToLowerInvariant();
        var query = db.AiReports.AsNoTracking().Where(r => r.AgentKey == AiCatalog.NewsAnalyst);
        AiReport? report;
        string subjectType;
        string subjectId;
        if (reportId is long rid)
        {
            report = await query.FirstOrDefaultAsync(r => r.Id == rid, cancellationToken)
                ?? throw new AiToolArgumentException($"No news record {rid}; get_news_events lists them.");
            subjectType = report.SubjectType;
            subjectId = report.SubjectId;
        }
        else if (item is { Length: > 1 } && item[0] is 'n' or 'f' && long.TryParse(item.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long iid))
        {
            subjectType = item[0] == 'n' ? AiReportSubject.News : AiReportSubject.Filing;
            subjectId = ReportData.Id(iid);
            string type = subjectType;
            string sid = subjectId;
            report = await query.Where(r => r.SubjectType == type && r.SubjectId == sid).OrderByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            throw new AiToolArgumentException("Give reportId, or item as n123 (a headline) or f45 (a filing).");
        }

        long itemId = long.TryParse(subjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
        object? source = null;
        string? textRead = null;
        if (subjectType == AiReportSubject.News)
        {
            var n = await db.NewsItems.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
            if (n is not null)
            {
                textRead = NewsAnalystAgent.HeadlineText(n.Title, n.Summary);
                source = new
                {
                    item = $"n{n.Id}", kind = "headline", source = n.Source, category = n.Category, published = Ist(n.PublishedUtc ?? n.FirstSeenUtc),
                    link = n.Link.Length > 0 ? n.Link : null, finbertSentiment = n.Sentiment, symbols = Text(n.Symbols, 200),
                };
            }
        }
        else if (subjectType == AiReportSubject.Filing)
        {
            var a = await db.CorporateAnnouncements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
            if (a is not null)
            {
                textRead = NewsAnalystAgent.FilingText(a.Subject, a.Details);
                source = new
                {
                    item = $"f{a.Id}", kind = "exchange filing", exchange = a.Exchange, symbol = a.Symbol, company = a.Company,
                    announced = Ist(a.AnnouncedUtc ?? a.FirstSeenUtc), link = a.AttachmentUrl.Length > 0 ? a.AttachmentUrl : null, finbertSentiment = a.Sentiment,
                };
            }
        }

        if (report is null && source is null) throw new AiToolArgumentException($"No item {item}; get_news lists the recorded news.");

        var data = report is null ? new JsonObject() : ReportData.Of(report.DataJson);
        var answer = new
        {
            reportId = report?.Id,
            read = report is not null,
            note = report is null ? "You have not read this item yet: a batch reads the last day's unread items every 10 minutes, filings first." : null,
            status = report?.Status,
            @event = ReportData.Str(data, "event"),
            direction = ReportData.Str(data, "direction"),
            symbols = ReportData.Strings(data, "symbols"),
            numbers = data["numbers"] is JsonArray numbers ? numbers.DeepClone() : null,
            confidence = ReportData.Num(data, "confidence"),
            summary = Text(ReportData.Str(data, "summary"), 300),
            error = Text(report?.Error, 400),
            written = Ist(report?.UpdatedUtc),
            callId = report?.CallId,
            source,
            textRead = Text(textRead, 1600),
        };

        return new AiToolOutput(answer, report?.UpdatedUtc, 1,
            report is null ? $"Item {subjectType} {subjectId}: not read yet" : $"Record {report.Id}: {ReportData.Str(data, "event") ?? report.Status}");
    }
}

/// <summary><c>get_incident</c>: one Sentinel incident with all its evidence, masked, and the Incident Explainer's explanation of it.</summary>
public sealed class IncidentTool(TradingDbContext db) : IAiTool
{
    private const int MaxEvidence = 12;

    public string Name => AiToolNames.Incident;

    public string Description =>
        "One Sentinel incident in full, by its id: its agent and rule, severity, status, title, summary, every line of " +
        "evidence (masked), Sentinel's suggestion, how often it fired, when it was first and last seen, acknowledged and " +
        "resolved, the root cause and resolution once written; and your explanation of it (what happened, why, what to " +
        "do, urgency), if you wrote one.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("incidentId", AiToolSchema.Integer("The incident's id, from get_incidents."), true));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        long id = args.Long("incidentId", 1) ?? throw new AiToolArgumentException("incidentId is required; get_incidents lists them.");
        var i = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AiToolArgumentException($"No incident {id}; get_incidents lists them.");
        string subject = ReportData.Id(id);
        var report = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.IncidentExplainer && r.SubjectType == AiReportSubject.Incident && r.SubjectId == subject)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync(cancellationToken);
        var data = report is null ? new JsonObject() : ReportData.Of(report.DataJson);

        var answer = new
        {
            id = i.Id,
            agent = i.Agent,
            rule = i.Rule,
            severity = i.Severity,
            status = i.Status,
            title = Text(i.Title, 200),
            summary = Text(i.Summary, 600),
            location = Text(i.Location, 200),
            evidence = Evidence(i.EvidenceJson),
            suggestion = Text(i.Suggestion, 400),
            occurrences = i.Occurrences,
            firstSeen = Ist(i.FirstSeenUtc),
            lastSeen = Ist(i.LastSeenUtc),
            acknowledged = Ist(i.AcknowledgedUtc),
            resolved = Ist(i.ResolvedUtc),
            rootCause = Text(i.RootCause, 400),
            resolution = Text(i.Resolution, 400),
            explanation = report is null
                ? null
                : new
                {
                    reportId = report.Id,
                    status = report.Status,
                    title = Text(report.Title, 200),
                    body = Text(report.Body, 3000),
                    urgency = ReportData.Str(data, "urgency"),
                    confidence = ReportData.Num(data, "confidence"),
                    written = Ist(report.UpdatedUtc),
                    error = Text(report.Error, 300),
                    callId = report.CallId,
                },
            explanationNote = report is null
                ? "No explanation: only a live incident of medium severity or worse, first seen in the last day, is explained, once."
                : null,
        };

        return new AiToolOutput(answer, i.LastSeenUtc, 1, $"Incident {id}: {i.Severity}, {i.Status}" + (report is null ? ", not explained" : ", explained"));
    }

    /// <summary>The evidence lines, masked: they quote log lines, which can hold anything.</summary>
    internal static List<string>? Evidence(string? json, int max = MaxEvidence)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray items) return [Text(json, 400)!];
            return items.Take(max)
                .Select(e => e is JsonValue v && v.TryGetValue(out string? s) ? s : e?.ToJsonString())
                .Select(s => Text(s, 400))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();
        }
        catch (JsonException)
        {
            return [Text(json, 400)!];
        }
    }
}

/// <summary><c>get_incident_explanations</c>: the Incident Explainer's explanations over the last days, each beside its incident.</summary>
public sealed class IncidentExplanationsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int DefaultTake = 20;
    private const int MaxTake = 40;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.IncidentExplanations;

    public string Description =>
        "Your incident explanations, newest first, over the last days (default 7): each with its incident (severity, " +
        "status now, title, first seen), your title, urgency and confidence, the status (ok; invalid: not in the asked " +
        "shape, its text kept; failed: no answer) and when it was written. get_incident reads one in full.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("days", AiToolSchema.Integer("How many days back, 1 to 30. Default 7."), false),
        ("take", AiToolSchema.Integer($"How many to list, 1 to {MaxTake}. Default {DefaultTake}."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        int days = args.Int("days", 1, 30) ?? 7;
        int take = args.Int("take", 1, MaxTake) ?? DefaultTake;
        var since = now.AddDays(-days);

        var reports = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.IncidentExplainer && r.SubjectType == AiReportSubject.Incident && r.CreatedUtc >= since)
            .OrderByDescending(r => r.Id).Take(take)
            .ToListAsync(cancellationToken);
        var ids = reports.Select(r => long.TryParse(r.SubjectId, out long id) ? id : 0).Where(id => id > 0).ToList();
        var incidents = await db.Incidents.AsNoTracking().Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.Severity, i.Status, i.Title, i.FirstSeenUtc })
            .ToDictionaryAsync(i => ReportData.Id(i.Id), cancellationToken);

        var rows = reports.Select(r =>
        {
            var data = ReportData.Of(r.DataJson);
            var i = incidents.GetValueOrDefault(r.SubjectId);
            return new
            {
                reportId = r.Id,
                incidentId = long.TryParse(r.SubjectId, out long iid) ? iid : (long?)null,
                incident = i is null ? null : new { severity = i.Severity, statusNow = i.Status, title = Text(i.Title, 200), firstSeen = Ist(i.FirstSeenUtc) },
                status = r.Status,
                title = Text(r.Title, 200),
                urgency = ReportData.Str(data, "urgency"),
                confidence = ReportData.Num(data, "confidence"),
                written = Ist(r.UpdatedUtc),
                error = Text(r.Error, 200),
            };
        }).ToList();

        return new AiToolOutput(new { since = Ist(since), explanations = rows }, reports.Count == 0 ? null : reports.Max(r => r.UpdatedUtc), rows.Count,
            $"{rows.Count} explanations in {days} days");
    }
}
