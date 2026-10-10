using System.Globalization;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

// The desk analysts' own work (ai_reports of the Technical, Options/OI, Macro and Fundamental Analysts), read when the
// owner talks with one of them. Each report's text was written by a model from desk data; it is masked like any free
// text before it leaves again.

/// <summary><c>get_analyst_reports</c>: one desk analyst's reports of a day or the last days, with what each concluded.</summary>
public sealed class AnalystReportsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int MaxRead = 300;
    private const int DefaultTake = 20;
    private const int MaxTake = 50;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.AnalystReports;

    public string Description =>
        "A desk analyst's reports, newest first: one IST day's (date, default today) or the last days' (days). analyst " +
        "is technical, options, macro or fundamental (default: the analyst asking). Each report: its slot or filing, " +
        "the status (ok; invalid: a number or level the code did not hand over, or not the asked JSON; failed: no answer, " +
        "tried again), the title and what it concluded (technical: each index's bias, grade and key levels; options: " +
        "each index's stance; macro: the bias band and size modifier; fundamental: the stock, the surprise and the " +
        "metrics). symbol narrows the fundamental reports to one stock. get_analyst_report reads one in full.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("analyst", AiToolSchema.OneOf("Whose reports. Default: the analyst asking.", "technical", "options", "macro", "fundamental"), false),
        ("date", AiToolSchema.Text("The IST day as yyyy-MM-dd, or 'today' / 'yesterday'. Default today."), false),
        ("days", AiToolSchema.Integer("Instead of a date: the last days, 1 to 30, today included."), false),
        ("symbol", AiToolSchema.Text("Fundamental only: one NSE symbol."), false),
        ("status", AiToolSchema.OneOf("Which reports. Default any.", "any", "ok", "invalid", "failed"), false),
        ("take", AiToolSchema.Integer($"How many to list, 1 to {MaxTake}. Default {DefaultTake}."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        string agent = DeskAnalysts.KeyOf(args.String("analyst", 30)) ?? DeskAnalysts.KeyOf(args.Caller?.AgentKey)
            ?? throw new AiToolArgumentException("analyst is technical, options, macro or fundamental.");
        int? days = args.Int("days", 1, 30);
        var date = days is null ? args.Date("date", today) ?? today : (DateOnly?)null;
        var since = today.AddDays(-((days ?? 1) - 1));
        string? symbol = args.String("symbol", 20)?.ToUpperInvariant();
        string status = args.String("status", 10)?.ToLowerInvariant() ?? "any";
        if (status is not ("any" or "ok" or "invalid" or "failed")) throw new AiToolArgumentException("status is any, ok, invalid or failed.");
        int take = args.Int("take", 1, MaxTake) ?? DefaultTake;

        var query = db.AiReports.AsNoTracking().Where(r => r.AgentKey == agent);
        query = date is DateOnly on ? query.Where(r => r.SessionDate == on) : query.Where(r => r.SessionDate >= since);
        if (status != "any") query = query.Where(r => r.Status == status);
        var reports = await query.OrderByDescending(r => r.Id).Take(MaxRead).ToListAsync(cancellationToken);

        var rows = reports.Select(r => (Report: r, Data: ReportData.Of(r.DataJson)))
            .Where(x => symbol is null || string.Equals(ReportData.Str(x.Data, "symbol"), symbol, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var listed = rows.Take(take).Select(x => new
        {
            reportId = x.Report.Id,
            about = x.Report.SubjectType == AiReportSubject.Filing ? $"filing {x.Report.SubjectId}" : $"slot {x.Report.SubjectId}",
            day = x.Report.SessionDate?.ToString("yyyy-MM-dd"),
            status = x.Report.Status,
            title = Text(x.Report.Title, 240),
            concluded = Concluded(agent, x.Data),
            error = Text(x.Report.Error, 240),
            written = Ist(x.Report.UpdatedUtc),
            tries = x.Report.Attempts,
        }).ToList();

        var data = new
        {
            analyst = AiCatalog.AgentName(agent),
            date = date?.ToString("yyyy-MM-dd"),
            since = date is null ? since.ToString("yyyy-MM-dd") : null,
            reports = listed,
            totals = new
            {
                reports = rows.Count,
                ok = rows.Count(x => x.Report.Status == AiReportStatus.Ok),
                invalid = rows.Count(x => x.Report.Status == AiReportStatus.Invalid),
                failed = rows.Count(x => x.Report.Status == AiReportStatus.Failed),
            },
        };

        return new AiToolOutput(data, reports.Count == 0 ? null : reports.Max(r => r.UpdatedUtc), listed.Count,
            $"{listed.Count} {AiCatalog.AgentName(agent)} reports");
    }

    /// <summary>What one report concluded, projected field by field; free text masked.</summary>
    internal static object? Concluded(string agent, JsonObject data) => agent switch
    {
        AiCatalog.TechnicalAnalyst => (data["indices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(i => new
        {
            underlying = ReportData.Str(i, "underlying"),
            bias = ReportData.Str(i, "bias"),
            grade = ReportData.Str(i, "grade"),
            keyLevels = (i["keyLevels"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Select(l => new { price = ReportData.Num(l, "price"), what = Text(ReportData.Str(l, "what"), 80) }).ToList(),
        }).ToList(),
        AiCatalog.OptionsAnalyst => (data["indices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(i => new
        {
            underlying = ReportData.Str(i, "underlying"),
            stance = ReportData.Str(i, "stance"),
            lines = ReportData.Strings(i, "lines").Count,
        }).ToList(),
        AiCatalog.MacroAnalyst => new
        {
            bias = ReportData.Str(data, "bias"),
            sizeModifier = ReportData.Num(data, "sizeModifier"),
            restsOn = (data["restsOn"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Select(f => ReportData.Str(f, "id")).ToList(),
        },
        AiCatalog.FundamentalAnalyst => new
        {
            symbol = ReportData.Str(data, "symbol"),
            results = data["results"] is JsonValue v && v.TryGetValue(out bool b) ? b : (bool?)null,
            surprise = ReportData.Str(data, "surprise"),
            metrics = ReportData.Count(data, "metrics"),
        },
        _ => null,
    };
}

/// <summary><c>get_analyst_report</c>: one desk analyst report in full, with the facts or the filing text it was checked against.</summary>
public sealed class AnalystReportTool(TradingDbContext db) : IAiTool
{
    public string Name => AiToolNames.AnalystReport;

    public string Description =>
        "One desk analyst report in full, by its id from get_analyst_reports: the report as written, what it concluded " +
        "(levels, lines, the bias and size, the metrics with their quotes), why it failed its check if it did, and what " +
        "it read: for the technical, options and macro analysts the facts the code handed over (every number it could " +
        "use); for the fundamental analyst the filing's text, exactly as handed over.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("reportId", AiToolSchema.Integer("The report's id, from get_analyst_reports."), true));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        long id = args.Long("reportId", 1) ?? throw new AiToolArgumentException("Give reportId; get_analyst_reports lists them.");
        var keys = DeskAnalysts.Keys.ToList();
        var report = await db.AiReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && keys.Contains(r.AgentKey), cancellationToken)
            ?? throw new AiToolArgumentException($"No analyst report {id}; get_analyst_reports lists them.");
        var data = ReportData.Of(report.DataJson);

        string? textRead = null;
        object? filing = null;
        if (report.SubjectType == AiReportSubject.Filing && long.TryParse(report.SubjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long filingId))
        {
            var a = await db.CorporateAnnouncements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == filingId, cancellationToken);
            if (a is not null)
            {
                textRead = FundamentalAnalystAgent.FilingText(a.Subject, a.Details);
                filing = new
                {
                    item = $"f{a.Id}", symbol = a.Symbol, company = a.Company, subject = Text(a.Subject, 200),
                    announced = Ist(a.AnnouncedUtc ?? a.FirstSeenUtc), link = a.AttachmentUrl.Length > 0 ? a.AttachmentUrl : null,
                };
            }
        }

        // The facts as handed over, free text masked: what every number was checked against.
        var facts = data["facts"]?.DeepClone();
        data.Remove("facts");
        var answer = new
        {
            reportId = report.Id,
            analyst = AiCatalog.AgentName(report.AgentKey),
            about = report.SubjectType == AiReportSubject.Filing ? $"filing {report.SubjectId}" : $"slot {report.SubjectId}",
            day = report.SessionDate?.ToString("yyyy-MM-dd"),
            status = report.Status,
            title = Text(report.Title, 300),
            report = Text(report.Body, 6000),
            concluded = Text(data.ToJsonString(), 6000),
            error = Text(report.Error, 600),
            factsHandedOver = facts is null ? null : Text(facts.ToJsonString(), 8000),
            filing,
            textRead = Text(textRead, 4000),
            tries = report.Attempts,
            written = Ist(report.UpdatedUtc),
            callId = report.CallId,
            model = report.Model.Length > 0 ? report.Model : null,
        };

        return new AiToolOutput(answer, report.UpdatedUtc, 1, $"{AiCatalog.AgentName(report.AgentKey)} report {report.Id}: {report.Status}");
    }
}
