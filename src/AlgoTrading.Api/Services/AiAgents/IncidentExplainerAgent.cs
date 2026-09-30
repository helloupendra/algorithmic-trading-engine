using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The Incident Explainer: for each new medium, high or critical Sentinel
/// incident, what happened, the likely cause and what to do.
/// </summary>
/// <remarks>
/// It reads what Sentinel stored, text masked the way the desk tools mask it,
/// and may look around with its tools (other incidents, the checkup, runs,
/// positions, quotes). An incident is explained once, within a day of its
/// first sighting; a low one is not worth a model call.
/// </remarks>
public sealed class IncidentExplainerAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiSchedulerState schedule,
    ILogger<IncidentExplainerAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    private static readonly string[] Worth = [IncidentSeverity.Medium, IncidentSeverity.High, IncidentSeverity.Critical];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.IncidentExplainer;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        long? id = await NextDueAsync(nowUtc, cancellationToken);
        if (id is null) return false;
        await ExplainAsync(id.Value, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        return true;
    }

    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        long? id = long.TryParse(subjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long given)
            ? given
            : await NextDueAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
        return id is null ? null : await ExplainAsync(id.Value, cancellationToken);
    }

    /// <summary>The oldest live incident worth explaining that has no explanation yet, or null.</summary>
    public async Task<long?> NextDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var since = nowUtc - Window;
        var candidates = await db.Incidents.AsNoTracking()
            .Where(i => IncidentStatus.Live.Contains(i.Status) && Worth.Contains(i.Severity) && i.FirstSeenUtc >= since)
            .OrderBy(i => i.FirstSeenUtc)
            .Select(i => i.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return null;

        var due = await reports.DueAsync(AgentKey, AiReportSubject.Incident, candidates.Select(Id).ToList(), cancellationToken);
        return candidates.FirstOrDefault(c => due.Contains(Id(c))) is long next && next != 0 ? next : null;
    }

    private async Task<AiReport?> ExplainAsync(long incidentId, CancellationToken cancellationToken)
    {
        var incident = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == incidentId, cancellationToken);
        if (incident is null) return null;
        var day = IstTime.DateOf(incident.FirstSeenUtc);

        var facts = new
        {
            id = incident.Id,
            agent = incident.Agent,
            rule = incident.Rule,
            severity = incident.Severity,
            status = incident.Status,
            title = AiToolFormat.Text(incident.Title, 200),
            summary = AiToolFormat.Text(incident.Summary, 600),
            evidence = Evidence(incident.EvidenceJson),
            sentinelSuggestion = AiToolFormat.Text(incident.Suggestion, 400),
            occurrences = incident.Occurrences,
            firstSeen = AiToolFormat.Ist(incident.FirstSeenUtc),
            lastSeen = AiToolFormat.Ist(incident.LastSeenUtc),
        };

        string question = "Explain this Sentinel incident:\n" + JsonSerializer.Serialize(facts, Json);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 4000, 0.2,
            $"incident-{incidentId}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            logger.LogWarning("Explaining incident {IncidentId} failed: {Error}", incidentId, why);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Incident, Id(incidentId), day, AiReportStatus.Failed, result,
                $"Incident {incidentId}: no explanation", string.Empty, "{}", why, cancellationToken);
        }

        var parsed = ParseExplanation(result.Text);
        if (parsed is null)
        {
            return await reports.SaveAsync(AgentKey, AiReportSubject.Incident, Id(incidentId), day, AiReportStatus.Invalid, result,
                $"Incident {incidentId}: {facts.title}", result.Text, "{}",
                "The answer was not the JSON object asked for; its text is kept as the body.", cancellationToken);
        }

        return await reports.SaveAsync(AgentKey, AiReportSubject.Incident, Id(incidentId), day, AiReportStatus.Ok, result,
            parsed.Value.Title, parsed.Value.Body, parsed.Value.Data, string.Empty, cancellationToken);
    }

    /// <summary>The explanation as the report's title, body and data; null when it is not the asked shape.</summary>
    public static (string Title, string Body, string Data)? ParseExplanation(string answer)
    {
        var obj = AiJson.Object(answer);
        if (obj is null) return null;
        string? what = AiJson.Str(obj, "what");
        string? todo = AiJson.Str(obj, "do");
        if (what is null || todo is null) return null;

        string? why = AiJson.Str(obj, "why");
        string? said = AiJson.Str(obj, "urgency")?.ToLowerInvariant();
        string urgency = said is "now" or "today" or "later" ? said : "unclear";
        double? confidence = AiJson.Num(obj, "confidence");

        var body = new StringBuilder();
        body.Append("**What happened.** ").Append(what).Append("\n\n");
        body.Append("**Likely why.** ").Append(why ?? "The evidence does not say.").Append("\n\n");
        body.Append("**What to do** (").Append(urgency).Append("). ").Append(todo);
        if (confidence is double c) body.Append($"\n\n_Confidence {c:0.00}._");

        var data = new JsonObject { ["urgency"] = urgency, ["confidence"] = confidence };
        return (AiJson.Str(obj, "title") ?? what, body.ToString(), data.ToJsonString(Json));
    }

    private static List<string>? Evidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray items) return [AiToolFormat.Text(json, 400)!];
            return items.Take(6)
                .Select(e => e is JsonValue v && v.TryGetValue(out string? s) ? s : e?.ToJsonString())
                .Select(s => AiToolFormat.Text(s, 400))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();
        }
        catch (JsonException)
        {
            return [AiToolFormat.Text(json, 400)!];
        }
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
