using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The Data Curator: after each trading day's candle archive and India VIX check, and at 08:00 IST before the
/// open, code checks what the desk recorded (<see cref="DataCuratorChecks"/>) and the model triages each problem it
/// found: a severity, what breaks if it is ignored, and one fix from a fixed list.
/// </summary>
/// <remarks>
/// <para>
/// The findings are the code's. The model is handed them with their ids, facts, readers and allowed fixes, and its
/// answer is held to them: an id the checks did not record, a finding left out, a fix not on the finding's list, or a
/// number, time or date that is not in the findings makes the report invalid (<see cref="Triage"/>). A pass that
/// finds nothing asks no model and is written as it is.
/// </para>
/// <para>
/// When: the night pass for a trading day is due <see cref="NightSettle"/> after the day's archive is
/// (<see cref="ArchiveSchedule.DueAtIst"/>: 23:50 IST, or 20 minutes after MCX's close in the winter), since the VIX
/// check runs in the archive's tick, and once the archive's marker has reached the day; if it has not
/// <see cref="ArchiveWait"/> later, the pass runs anyway and the lag is a finding. The morning pass runs from
/// <see cref="AiSettings.DataCuratorMorningIst"/> (08:00) to 09:00 on NSE trading days, by the exchange calendar,
/// and checks the trading day before, with today's expiries. Nothing runs from 09:00 to 15:45 on a trading day
/// (<see cref="AiAgentHours.InSession"/>).
/// </para>
/// </remarks>
public sealed class DataCuratorAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    DataCuratorChecks checks,
    IMarketSessionService sessions,
    IConfiguration configuration,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<DataCuratorAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    /// <summary>After the archive is due: its tick also runs the India VIX check, which can take a few minutes.</summary>
    public static readonly TimeSpan NightSettle = TimeSpan.FromMinutes(20);

    /// <summary>How long the night pass waits for an archive that has not finished before it runs anyway.</summary>
    public static readonly TimeSpan ArchiveWait = TimeSpan.FromHours(2);

    public static readonly IReadOnlySet<string> Severities = new HashSet<string>(StringComparer.Ordinal) { "high", "medium", "low" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.DataCurator;

    /// <summary>A pass to make: which, for which day, and the report's subject.</summary>
    public sealed record Pass(string Kind, DateOnly CheckedDay, DateOnly Today)
    {
        public string SubjectId => $"{Kind}:{(Kind == DataCuratorChecks.MorningPass ? Today : CheckedDay):yyyy-MM-dd}";
    }

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var pass = await NextDueAsync(nowUtc, cancellationToken);
        if (pass is null) return false;
        await RunPassAsync(pass, nowUtc, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        return true;
    }

    /// <summary>
    /// "Run now": the pass named (<c>night:yyyy-MM-dd</c>, <c>morning:yyyy-MM-dd</c>, or a day for its night pass), or
    /// the night pass of the latest trading day whose archive is due. Refused, as null, inside the session.
    /// </summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (AiAgentHours.InSession(now, sessions)) return null;
        var today = IstTime.DateOf(now);
        Pass? pass = ParseSubject(subjectId, today, x => checks.PreviousTradingDay(x))
                     ?? (LatestNightDay(now) is DateOnly d ? new Pass(DataCuratorChecks.NightPass, d, today) : null);
        return pass is null ? null : await RunPassAsync(pass, now, cancellationToken);
    }

    /// <summary>
    /// "night:2026-10-09", "morning:2026-10-10" or "2026-10-09" (a night pass) as a pass; null otherwise. A morning
    /// pass checks the trading day before its own.
    /// </summary>
    public static Pass? ParseSubject(string? subjectId, DateOnly today, Func<DateOnly, DateOnly?> previousTradingDay)
    {
        if (string.IsNullOrWhiteSpace(subjectId)) return null;
        string[] parts = subjectId.Trim().Split(':', 2);
        string kind = parts.Length == 2 ? parts[0] : DataCuratorChecks.NightPass;
        if (!DateOnly.TryParseExact(parts[^1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;
        return kind switch
        {
            DataCuratorChecks.NightPass => new Pass(kind, day, today),
            DataCuratorChecks.MorningPass => previousTradingDay(day) is DateOnly before ? new Pass(kind, before, day) : null,
            _ => null,
        };
    }

    /// <summary>The pass that is due now, or null: the morning pass first in its hour, else the latest night pass owed.</summary>
    public async Task<Pass?> NextDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (AiAgentHours.InSession(nowUtc, sessions)) return null;
        var ist = IstTime.ToIst(nowUtc);
        var today = DateOnly.FromDateTime(ist);
        var clock = TimeOnly.FromDateTime(ist);

        var morningAt = AiAgentHours.Clock(settings.CurrentValue.DataCuratorMorningIst, new TimeOnly(8, 0));
        if (AiAgentHours.IsTradingDay(today, sessions) && clock >= morningAt && clock < AiAgentHours.QuietFrom
            && checks.PreviousTradingDay(today) is DateOnly before)
        {
            var morning = new Pass(DataCuratorChecks.MorningPass, before, today);
            if ((await reports.DueAsync(AgentKey, AiReportSubject.Check, [morning.SubjectId], cancellationToken)).Count > 0) return morning;
        }

        if (LatestNightDay(nowUtc) is not DateOnly day) return null;
        var night = new Pass(DataCuratorChecks.NightPass, day, today);
        if ((await reports.DueAsync(AgentKey, AiReportSubject.Check, [night.SubjectId], cancellationToken)).Count == 0) return null;

        // The VIX check runs in the archive's tick, after the day's marker is saved: wait for the marker, not forever.
        string? marker = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == NightlyArchiveService.LastArchivedDayKey).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        bool archived = DateOnly.TryParseExact(marker, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var last) && last >= day;
        return archived || ist >= NightDueIst(day) + ArchiveWait ? night : null;
    }

    /// <summary>The latest NSE trading day whose night pass time has come, at most six days back.</summary>
    private DateOnly? LatestNightDay(DateTime nowUtc)
    {
        var ist = IstTime.ToIst(nowUtc);
        for (var d = DateOnly.FromDateTime(ist); d >= DateOnly.FromDateTime(ist).AddDays(-6); d = d.AddDays(-1))
        {
            if (AiAgentHours.IsTradingDay(d, sessions) && ist >= NightDueIst(d)) return d;
        }

        return null;
    }

    /// <summary>When a trading day's night pass is due, in IST: <see cref="NightSettle"/> after its archive is.</summary>
    public DateTime NightDueIst(DateOnly day) =>
        ArchiveSchedule.DueAtIst(day, ArchiveSchedule.ParseRunAt(configuration["Archive:RunAtIst"]), sessions) + NightSettle;

    private async Task<AiReport> RunPassAsync(Pass pass, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var underlyings = settings.CurrentValue.DataCuratorChainUnderlyings
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.ToUpperInvariant())
            .Distinct()
            .ToList();
        DataCheckRun run;
        try
        {
            run = await checks.RunAsync(pass.Kind, pass.CheckedDay, pass.Today, nowUtc, underlyings, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The desk's side, not the model's: kept as a failed try and retried like one.
            logger.LogWarning(ex, "The data checks for {Subject} stopped on the desk's side; kept as a failed try", pass.SubjectId);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, pass.SubjectId, pass.CheckedDay, AiReportStatus.Failed, null,
                $"Data checks for {Day(pass.CheckedDay)}: not run", string.Empty, "{}",
                $"The checks stopped on the desk's side: {ex.GetType().Name}", cancellationToken);
        }

        string label = $"Data checks for {Day(pass.CheckedDay)} ({pass.Kind})";
        if (run.Findings.Count == 0)
        {
            var clean = DataOf(run, null, null);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, pass.SubjectId, pass.CheckedDay, AiReportStatus.Ok, null,
                $"{label}: nothing wrong", Body(run, null, null), clean.ToJsonString(Json), string.Empty, cancellationToken);
        }

        string question = Question(run);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 6000, 0.1,
            $"data-{pass.SubjectId}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            if (AiGateway.IsCapacityRefusal(result))
            {
                logger.LogInformation("Data checks {Subject} were turned away for capacity; tried again in the next window: {Why}", pass.SubjectId, why);
                return await reports.SaveTurnedAwayAsync(AgentKey, AiReportSubject.Check, pass.SubjectId, pass.CheckedDay, result,
                    $"{label}: not triaged", $"Turned away for capacity, not counted as a try: {why}", cancellationToken);
            }

            logger.LogInformation("Data checks {Subject}: no model answered: {Why}", pass.SubjectId, why);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, pass.SubjectId, pass.CheckedDay, AiReportStatus.Failed, result,
                $"{label}: not triaged", string.Empty, DataOf(run, null, null).ToJsonString(Json), why, cancellationToken);
        }

        var triage = Triage(result.Text, run, AiCatalog.Agent(AgentKey)!.SystemPrompt + "\n" + question);
        if (triage.Problems.Count > 0)
        {
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, pass.SubjectId, pass.CheckedDay, AiReportStatus.Invalid, result,
                $"{label}: {run.Findings.Count} finding{(run.Findings.Count == 1 ? "" : "s")}, triage failed its check", result.Text,
                DataOf(run, null, null).ToJsonString(Json), string.Join("; ", triage.Problems.Take(6)), cancellationToken);
        }

        int high = triage.ById.Values.Count(t => t.Severity == "high");
        int medium = triage.ById.Values.Count(t => t.Severity == "medium");
        int low = triage.ById.Values.Count(t => t.Severity == "low");
        string title = $"{label}: {high} high, {medium} medium, {low} low";
        return await reports.SaveAsync(AgentKey, AiReportSubject.Check, pass.SubjectId, pass.CheckedDay, AiReportStatus.Ok, result,
            title, Body(run, triage.ById, triage.Summary), DataOf(run, triage.ById, triage.Summary).ToJsonString(Json), string.Empty, cancellationToken);
    }

    /// <summary>What the model is handed: the pass, and each finding with its facts, readers and allowed fixes.</summary>
    public static string Question(DataCheckRun run)
    {
        var payload = new
        {
            pass = run.Pass,
            checkedDay = run.CheckedDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            today = run.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            findings = run.Findings.Select(f => new
            {
                id = f.Id,
                kind = f.Kind,
                subject = f.Subject,
                facts = f.Facts,
                readBy = f.ReadBy,
                actions = f.Actions.Select(a => new { action = a, means = DataCuratorChecks.Actions[a] }),
            }),
        };
        return "Triage these data findings. Use only their facts; pick each action from the finding's own actions.\n" +
               JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>The model's triage of one finding.</summary>
    public sealed record FindingTriage(string Severity, string Impact, string Action, string Why);

    /// <summary>The triage by finding id, the summary, and every reason it fails its check (none when it passes).</summary>
    public sealed record TriageResult(Dictionary<string, FindingTriage> ById, string? Summary, List<string> Problems);

    /// <summary>
    /// Reads the model's triage and holds it to the code's findings: every id one the checks recorded, every finding
    /// triaged once, a severity from the list, a fix from the finding's own list, and every number, time and date in
    /// its words one of <paramref name="given"/>'s.
    /// </summary>
    public static TriageResult Triage(string answer, DataCheckRun run, string given)
    {
        var problems = new List<string>();
        var byId = new Dictionary<string, FindingTriage>(StringComparer.Ordinal);
        var obj = AiJson.Object(answer);
        if (obj?["findings"] is not JsonArray items)
        {
            problems.Add("The answer was not the JSON object asked for.");
            return new TriageResult(byId, null, problems);
        }

        var known = run.Findings.ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (var item in items.OfType<JsonObject>())
        {
            string id = AiJson.Str(item, "id") ?? string.Empty;
            if (!known.TryGetValue(id, out var finding))
            {
                problems.Add($"it names a finding the checks did not record (\"{Clip(id, 60)}\")");
                continue;
            }

            if (byId.ContainsKey(id))
            {
                problems.Add($"it triages {id} twice");
                continue;
            }

            string severity = AiJson.Str(item, "severity")?.ToLowerInvariant() ?? string.Empty;
            string action = AiJson.Str(item, "action")?.Trim().ToLowerInvariant() ?? string.Empty;
            string impact = AiJson.Str(item, "impact")?.Trim() ?? string.Empty;
            string why = AiJson.Str(item, "why")?.Trim() ?? string.Empty;
            if (!Severities.Contains(severity)) problems.Add($"{id}: severity \"{Clip(severity, 20)}\" is not high, medium or low");
            if (!finding.Actions.Contains(action)) problems.Add($"{id}: action \"{Clip(action, 40)}\" is not one of {string.Join(", ", finding.Actions)}");
            if (impact.Length == 0) problems.Add($"{id}: no impact");
            foreach (var bad in AiFactCheck.Unsupported(impact + "\n" + why, given).Take(3))
            {
                problems.Add($"{id}: \"{bad}\" is not in the findings");
            }

            byId[id] = new FindingTriage(severity, impact, action, why);
        }

        foreach (var missing in run.Findings.Where(f => !byId.ContainsKey(f.Id)).Take(5))
        {
            problems.Add($"it left out {missing.Id}");
        }

        string? summary = AiJson.Str(obj, "summary");
        foreach (var bad in AiFactCheck.Unsupported(summary, given).Take(3)) problems.Add($"summary: \"{bad}\" is not in the findings");
        return new TriageResult(byId, summary, problems.Distinct().ToList());
    }

    /// <summary>The report's data: the pass, what was checked, and each finding with its triage when there is one.</summary>
    private static JsonObject DataOf(DataCheckRun run, Dictionary<string, FindingTriage>? triage, string? summary)
    {
        var findings = new JsonArray();
        foreach (var f in run.Findings)
        {
            var t = triage?.GetValueOrDefault(f.Id);
            findings.Add(new JsonObject
            {
                ["id"] = f.Id,
                ["kind"] = f.Kind,
                ["subject"] = f.Subject,
                ["facts"] = f.Facts,
                ["readBy"] = new JsonArray(f.ReadBy.Select(r => (JsonNode)r).ToArray()),
                ["actions"] = new JsonArray(f.Actions.Select(a => (JsonNode)a).ToArray()),
                ["severity"] = t?.Severity,
                ["impact"] = t?.Impact,
                ["action"] = t?.Action,
                ["actionMeans"] = t is null ? null : DataCuratorChecks.Actions.GetValueOrDefault(t.Action),
                ["why"] = t?.Why,
            });
        }

        return new JsonObject
        {
            ["pass"] = run.Pass,
            ["checkedDay"] = run.CheckedDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["today"] = run.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["summary"] = summary,
            ["checked"] = new JsonArray(run.Checked.Select(c => (JsonNode)c).ToArray()),
            ["findings"] = findings,
            ["high"] = triage?.Values.Count(t => t.Severity == "high") ?? 0,
            ["medium"] = triage?.Values.Count(t => t.Severity == "medium") ?? 0,
            ["low"] = triage?.Values.Count(t => t.Severity == "low") ?? 0,
        };
    }

    private static string Body(DataCheckRun run, Dictionary<string, FindingTriage>? triage, string? summary)
    {
        var body = new StringBuilder();
        if (run.Findings.Count == 0)
        {
            body.Append($"No problem found in the data recorded for {Day(run.CheckedDay)}.\n");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(summary)) body.Append(summary.Trim()).Append("\n\n");
            foreach (var severity in new[] { "high", "medium", "low" })
            {
                var these = run.Findings.Where(f => triage?.GetValueOrDefault(f.Id)?.Severity == severity).ToList();
                if (these.Count == 0) continue;
                body.Append($"**{char.ToUpperInvariant(severity[0])}{severity[1..]}**\n");
                foreach (var f in these)
                {
                    var t = triage![f.Id];
                    body.Append($"- **{f.Subject}.** {f.Facts}\n  Breaks: {t.Impact}\n  Do: {DataCuratorChecks.Actions[t.Action]}.\n");
                }

                body.Append('\n');
            }
        }

        body.Append("**Checked**\n");
        foreach (var c in run.Checked) body.Append("- ").Append(c).Append('\n');
        return body.ToString().TrimEnd();
    }

    private static string Day(DateOnly day) => day.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
