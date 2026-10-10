using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

// The Phase 4 agents' own work, read when the owner talks with them: the Data Curator's checks, the circulars with the
// Compliance Triage's verdicts, the Strategy Researcher's weekly notes (ai_reports, and regulatory_circulars). A
// report's text was written by a model from desk data; it is masked like any free text before it leaves again.

/// <summary><c>get_data_checks</c>: the Data Curator's passes over the recorded data, or one in full.</summary>
public sealed class DataChecksTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int MaxTake = 30;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.DataChecks;

    public string Description =>
        "Your data checks, newest first: the night pass after each trading day's archive and the 08:00 pass before the " +
        "open, over the last days (days, default 7) or one checked day (date). Each: the pass, the day checked, the " +
        "status (ok; invalid: the triage failed its check; failed: not run or no answer), the count of high, medium and " +
        "low findings and the high ones. With reportId: one pass in full, every finding with the code's facts, what reads " +
        "that data, your severity, impact and action, and the list of what was checked.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("date", AiToolSchema.Text("The trading day checked, yyyy-MM-dd, or 'today' / 'yesterday'."), false),
        ("days", AiToolSchema.Integer("Without a date: the last days, 1 to 30. Default 7."), false),
        ("reportId", AiToolSchema.Integer("One pass in full, by its report id from the list."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var today = IstTime.DateOf(_time.GetUtcNow().UtcDateTime);
        var query = db.AiReports.AsNoTracking().Where(r => r.AgentKey == AiCatalog.DataCurator && r.SubjectType == AiReportSubject.Check);
        if (args.Long("reportId", 1) is long id)
        {
            var report = await query.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new AiToolArgumentException($"No data check {id}; call get_data_checks without reportId for the list.");
            var data = ReportData.Of(report.DataJson);
            var full = new
            {
                reportId = report.Id,
                pass = ReportData.Str(data, "pass") ?? report.SubjectId.Split(':')[0],
                checkedDay = ReportData.Str(data, "checkedDay"),
                status = report.Status,
                title = Text(report.Title, 300),
                summary = Text(ReportData.Str(data, "summary"), 600),
                findings = (data["findings"] as JsonArray ?? []).OfType<JsonObject>().Select(f => new
                {
                    id = ReportData.Str(f, "id"),
                    subject = ReportData.Str(f, "subject"),
                    facts = Text(ReportData.Str(f, "facts"), 600),
                    readBy = ReportData.Strings(f, "readBy"),
                    severity = ReportData.Str(f, "severity"),
                    impact = Text(ReportData.Str(f, "impact"), 400),
                    action = ReportData.Str(f, "action"),
                    actionMeans = ReportData.Str(f, "actionMeans"),
                }).ToList(),
                @checked = ReportData.Strings(data, "checked").Select(c => Text(c, 200)).ToList(),
                error = Text(report.Error, 400),
                written = Ist(report.UpdatedUtc),
                callId = report.CallId,
            };
            return new AiToolOutput(full, report.UpdatedUtc, 1, $"Data check {report.SubjectId}: {report.Status}");
        }

        var date = args.Date("date", today);
        int days = args.Int("days", 1, 30) ?? 7;
        var since = today.AddDays(-days);
        query = date is DateOnly on ? query.Where(r => r.SessionDate == on) : query.Where(r => r.SessionDate >= since);
        var rows = await query.OrderByDescending(r => r.Id).Take(MaxTake).ToListAsync(cancellationToken);
        var list = rows.Select(r =>
        {
            var data = ReportData.Of(r.DataJson);
            var high = (data["findings"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(f => ReportData.Str(f, "severity") == "high")
                .Select(f => Text($"{ReportData.Str(f, "subject")}: {ReportData.Str(f, "facts")}", 300))
                .ToList();
            return new
            {
                reportId = r.Id,
                subject = r.SubjectId,
                checkedDay = r.SessionDate?.ToString("yyyy-MM-dd"),
                status = r.Status,
                title = Text(r.Title, 200),
                high = ReportData.Num(data, "high"),
                medium = ReportData.Num(data, "medium"),
                low = ReportData.Num(data, "low"),
                highFindings = high,
                error = Text(r.Error, 200),
                written = Ist(r.UpdatedUtc),
            };
        }).ToList();
        return new AiToolOutput(new { since = date is null ? since.ToString("yyyy-MM-dd") : null, date = date?.ToString("yyyy-MM-dd"), checks = list },
            rows.Count == 0 ? null : rows.Max(r => r.UpdatedUtc), list.Count, $"{list.Count} data checks");
    }
}

/// <summary><c>get_circulars</c>: the recorded SEBI, NSE and BSE circulars with the Compliance Triage's verdict on each, or one in full.</summary>
public sealed class CircularsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int MaxTake = 60;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.Circulars;

    public string Description =>
        "The circulars the desk recorded (SEBI's circulars; NSE's F&O, inspection and algo ones and others about " +
        "derivatives, risk or margins; BSE's notices on the same), newest first, recorded in the last days (days, default " +
        "14), by source and by your verdict (applies: yes, no, unclear, or untriaged). Each: source, number, date, title, " +
        "why it was recorded, and your verdict, area and reason when triaged. With id: one circular in full, with the text " +
        "read from its document and your quote and what would have to change.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("days", AiToolSchema.Integer("Recorded in the last days, 1 to 90. Default 14."), false),
        ("source", AiToolSchema.OneOf("Which source. Default any.", "any", "sebi", "nse", "bse"), false),
        ("applies", AiToolSchema.OneOf("Your verdict. Default any.", "any", "yes", "no", "unclear", "untriaged"), false),
        ("id", AiToolSchema.Integer("One circular in full, by its id from the list."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        if (args.Long("id", 1) is long id)
        {
            var c = await db.RegulatoryCirculars.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw new AiToolArgumentException($"No circular {id}; call get_circulars without id for the list.");
            var report = await db.AiReports.AsNoTracking()
                .FirstOrDefaultAsync(r => r.AgentKey == AiCatalog.ComplianceTriage && r.SubjectType == AiReportSubject.Check
                                          && r.SubjectId == ComplianceTriageAgent.Subject(c.Id), cancellationToken);
            var data = report is null ? null : ReportData.Of(report.DataJson);
            var full = new
            {
                id = c.Id,
                source = c.Source,
                number = c.Number,
                date = c.PublishedOn?.ToString("yyyy-MM-dd"),
                department = c.Department,
                title = Text(c.Title, 600),
                recordedBecause = c.Relevance,
                url = c.Url,
                text = Text(c.Excerpt, 3000),
                recorded = Ist(c.FirstSeenUtc),
                triage = report is null
                    ? null
                    : new
                    {
                        reportId = report.Id,
                        status = report.Status,
                        applies = ReportData.Str(data!, "applies"),
                        area = ReportData.Str(data!, "area"),
                        why = Text(ReportData.Str(data!, "why"), 600),
                        change = Text(ReportData.Str(data!, "change"), 600),
                        quote = Text(ReportData.Str(data!, "quote"), 600),
                        error = Text(report.Error, 300),
                    },
            };
            return new AiToolOutput(full, c.FirstSeenUtc, 1, $"Circular {c.Key}");
        }

        int days = args.Int("days", 1, 90) ?? 14;
        string source = args.String("source", 10)?.ToLowerInvariant() ?? "any";
        string applies = args.String("applies", 12)?.ToLowerInvariant() ?? "any";
        if (source is not ("any" or "sebi" or "nse" or "bse")) throw new AiToolArgumentException("source is any, sebi, nse or bse.");
        if (applies is not ("any" or "yes" or "no" or "unclear" or "untriaged")) throw new AiToolArgumentException("applies is any, yes, no, unclear or untriaged.");

        var since = _time.GetUtcNow().UtcDateTime.AddDays(-days);
        var query = db.RegulatoryCirculars.AsNoTracking().Where(c => c.FirstSeenUtc >= since);
        if (source != "any") query = query.Where(c => c.Source == source);
        var circulars = await query.OrderByDescending(c => c.FirstSeenUtc).ThenByDescending(c => c.Id).Take(400)
            .Select(c => new { c.Id, c.Source, c.Number, c.Title, c.PublishedOn, c.Relevance, c.FirstSeenUtc })
            .ToListAsync(cancellationToken);
        var subjects = circulars.Select(c => ComplianceTriageAgent.Subject(c.Id)).ToList();
        var verdicts = (await db.AiReports.AsNoTracking()
                .Where(r => r.AgentKey == AiCatalog.ComplianceTriage && r.SubjectType == AiReportSubject.Check && subjects.Contains(r.SubjectId))
                .Select(r => new { r.SubjectId, r.Status, r.DataJson })
                .ToListAsync(cancellationToken))
            .ToDictionary(r => r.SubjectId, r => (r.Status, Data: ReportData.Of(r.DataJson)));

        var rows = circulars.Select(c =>
            {
                var v = verdicts.TryGetValue(ComplianceTriageAgent.Subject(c.Id), out var found) ? found : ((string Status, JsonObject Data)?)null;
                string verdict = v is { Status: AiReportStatus.Ok } ok ? ReportData.Str(ok.Data, "applies") ?? "untriaged" : "untriaged";
                return new
                {
                    id = c.Id,
                    source = c.Source,
                    number = c.Number,
                    date = c.PublishedOn?.ToString("yyyy-MM-dd"),
                    title = Text(c.Title, 300),
                    recordedBecause = c.Relevance,
                    applies = verdict,
                    area = v is { } x ? ReportData.Str(x.Data, "area") : null,
                    why = v is { } y ? Text(ReportData.Str(y.Data, "why"), 300) : null,
                    triageStatus = v?.Status,
                    recorded = Ist(c.FirstSeenUtc),
                };
            })
            .Where(r => applies == "any" || r.applies == applies)
            .Take(MaxTake)
            .ToList();
        return new AiToolOutput(new { days, circulars = rows, recorded = circulars.Count }, circulars.Count == 0 ? null : circulars.Max(c => c.FirstSeenUtc),
            rows.Count, $"{rows.Count} circulars");
    }
}

/// <summary><c>get_research_notes</c>: the Strategy Researcher's weekly notes, or one with the tables and checks it read.</summary>
public sealed class ResearchNotesTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.ResearchNotes;

    public string Description =>
        "Your weekly research notes, newest first (weeks, default 4): the week, the status, the title, the summary and " +
        "the ideas to test. With reportId or week (the note's Sunday, yyyy-MM-dd): one note in full, with the backtests, " +
        "the paper results after charges and the overfitting checks the code ran, as you read them, and what you wrote " +
        "about each strategy.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("weeks", AiToolSchema.Integer("How many weeks back, 1 to 26. Default 4."), false),
        ("week", AiToolSchema.Text("One week's note, by its Sunday as yyyy-MM-dd."), false),
        ("reportId", AiToolSchema.Integer("One note, by its report id."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var today = IstTime.DateOf(_time.GetUtcNow().UtcDateTime);
        var query = db.AiReports.AsNoTracking().Where(r => r.AgentKey == AiCatalog.StrategyResearcher && r.SubjectType == AiReportSubject.Check);
        AiReport? one = null;
        if (args.Long("reportId", 1) is long id)
        {
            one = await query.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                  ?? throw new AiToolArgumentException($"No research note {id}; call get_research_notes for the list.");
        }
        else if (args.Date("week", today) is DateOnly week)
        {
            string subject = StrategyResearcherAgent.Subject(week);
            one = await query.FirstOrDefaultAsync(r => r.SubjectId == subject, cancellationToken)
                  ?? throw new AiToolArgumentException($"No note for the week of {week:yyyy-MM-dd}: notes are written on Sundays from 11:00 IST.");
        }

        if (one is not null)
        {
            var data = ReportData.Of(one.DataJson);
            var full = new
            {
                reportId = one.Id,
                week = ReportData.Str(data, "week"),
                status = one.Status,
                title = Text(one.Title, 300),
                summary = Text(ReportData.Str(data, "summary"), 800),
                strategies = Cut(data["strategyNotes"], 6000),
                ideas = Cut(data["ideas"], 3000),
                checks = Cut(data["checks"], 6000),
                backtests = Cut(data["backtests"], 8000),
                paper = Cut(data["paper"], 4000),
                error = Text(one.Error, 400),
                written = Ist(one.UpdatedUtc),
                callId = one.CallId,
            };
            return new AiToolOutput(full, one.UpdatedUtc, 1, $"Research note {one.SubjectId}: {one.Status}");
        }

        int weeks = args.Int("weeks", 1, 26) ?? 4;
        var since = today.AddDays(-7 * weeks);
        var rows = await query.Where(r => r.SessionDate >= since).OrderByDescending(r => r.Id).Take(weeks + 2).ToListAsync(cancellationToken);
        var list = rows.Select(r =>
        {
            var data = ReportData.Of(r.DataJson);
            return new
            {
                reportId = r.Id,
                week = r.SessionDate?.ToString("yyyy-MM-dd"),
                status = r.Status,
                title = Text(r.Title, 300),
                summary = Text(ReportData.Str(data, "summary"), 600),
                ideas = (data["ideas"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(i => Text($"{ReportData.Str(i, "strategy")}: {ReportData.Str(i, "idea")}", 300)).ToList(),
                error = Text(r.Error, 200),
            };
        }).ToList();
        return new AiToolOutput(new { since = since.ToString("yyyy-MM-dd"), notes = list }, rows.Count == 0 ? null : rows.Max(r => r.UpdatedUtc), list.Count,
            $"{list.Count} research notes");
    }

    /// <summary>A part of the note's data as JSON text, masked and cut so a tool answer stays inside its limit.</summary>
    private static string? Cut(JsonNode? node, int max) => node is null ? null : Text(node.ToJsonString(), max);
}
