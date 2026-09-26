using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// What Sentinel, the desk's watchman, found wrong: the list, a count for the
/// console's header, and the two things a person does with an incident.
/// </summary>
/// <remarks>
/// <para>
/// This controller never creates an incident. Sentinel writes the rows straight
/// into the <c>incidents</c> table, because the moment it most needs to record
/// something is often the moment the API is not answering, and an HTTP call
/// would fail with it. The API reads what Sentinel wrote and moves rows through
/// acknowledged and resolved.
/// </para>
/// <para>
/// Admin-only: an incident names runs, accounts, feeds and log files across
/// every trader on the desk.
/// </para>
/// <para>
/// Every piece of text is passed through <see cref="IncidentRedaction"/> on the
/// way out. Sentinel should never store a secret, but its evidence is quoted
/// from logs and tracebacks, and the console is where Telegram sends people to
/// read the rest, so a token that slipped into a row is masked here as well.
/// </para>
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/Incidents")]
public class IncidentsController : ControllerBase
{
    private const int DefaultTake = 200;
    private const int MaxTake = 500;

    private readonly TradingDbContext _dbContext;

    public IncidentsController(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Incidents, most recently seen first.
    /// </summary>
    /// <param name="status">
    /// <c>live</c> (the default: open and acknowledged, what still needs a
    /// person), <c>open</c>, <c>acknowledged</c>, <c>resolved</c> or <c>any</c>.
    /// </param>
    /// <param name="severity"><c>low</c>, <c>medium</c>, <c>high</c> or <c>critical</c>; several may be given comma-separated.</param>
    /// <param name="agent">The Sentinel agent that raised it, for example <c>feeds</c>.</param>
    /// <param name="take">At most this many rows, up to 500.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? severity,
        [FromQuery] string? agent,
        [FromQuery] int take = DefaultTake,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseStatus(status, out var statuses))
        {
            return BadRequest(new { message = "status must be one of live, open, acknowledged, resolved or any." });
        }

        if (!TryParseSeverities(severity, out var severities))
        {
            return BadRequest(new { message = "severity must be low, medium, high or critical (comma-separated for several)." });
        }

        IQueryable<Incident> query = _dbContext.Incidents.AsNoTracking();

        if (statuses is not null)
        {
            query = query.Where(x => statuses.Contains(x.Status));
        }

        if (severities is not null)
        {
            query = query.Where(x => severities.Contains(x.Severity));
        }

        if (!string.IsNullOrWhiteSpace(agent))
        {
            string wanted = agent.Trim();
            query = query.Where(x => x.Agent == wanted);
        }

        var rows = await query
            .OrderByDescending(x => x.LastSeenUtc)
            .ThenByDescending(x => x.Id)
            .Take(Math.Clamp(take, 1, MaxTake))
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(ToView).ToList());
    }

    /// <summary>
    /// How many incidents are live, by severity, the newest one, and when
    /// Sentinel last finished a round of checks: what the console shows in its
    /// header without loading the list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Newest" is the live incident that appeared most recently, by when it
    /// was first seen. Not by last seen: Sentinel re-sees every live incident
    /// on every check, so that time is always about thirty seconds ago and
    /// says nothing about when the trouble started.
    /// </para>
    /// <para>
    /// The last check is what makes a zero mean something. Sentinel writes an
    /// incident only when it finds one, so without it a stopped Sentinel and a
    /// quiet desk look the same. It is null when Sentinel has never reported a
    /// round, and returned as stored however old it is: judging it stale is the
    /// console's job, and hiding an old value would hide exactly that.
    /// </para>
    /// </remarks>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        string[] liveStatuses = IncidentStatus.Live.ToArray();
        var live = _dbContext.Incidents.AsNoTracking().Where(x => liveStatuses.Contains(x.Status));

        var grouped = await live
            .GroupBy(x => x.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        // All four severities are always present, so the console renders a
        // real zero rather than a blank. A value Sentinel should never write
        // still shows up under its own name rather than vanishing.
        var counts = IncidentSeverity.All.ToDictionary(s => s, _ => 0);
        foreach (var g in grouped)
        {
            counts[g.Severity] = counts.GetValueOrDefault(g.Severity) + g.Count;
        }

        string? worst = IncidentSeverity.All.LastOrDefault(s => counts[s] > 0);

        var newest = await live
            .OrderByDescending(x => x.FirstSeenUtc)
            .ThenByDescending(x => x.Id)
            .Select(x => new { x.Id, x.Title, x.Severity, x.FirstSeenUtc })
            .FirstOrDefaultAsync(cancellationToken);

        DateTime? lastCheck = await _dbContext.SentinelHeartbeats.AsNoTracking()
            .Select(x => (DateTime?)x.LastCheckUtc)
            .MaxAsync(cancellationToken);

        return Ok(new IncidentSummary(
            grouped.Sum(g => g.Count),
            counts,
            worst,
            newest?.Id,
            newest is null ? null : IncidentRedaction.Mask(newest.Title),
            newest?.Severity,
            newest?.FirstSeenUtc,
            lastCheck is null ? null : DateTime.SpecifyKind(lastCheck.Value, DateTimeKind.Utc)));
    }

    /// <summary>One incident.</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var incident = await _dbContext.Incidents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return incident is null ? NotFound(new { message = $"Incident {id} not found." }) : Ok(ToView(incident));
    }

    /// <summary>
    /// "I have seen this and I am on it." Only an open incident can be
    /// acknowledged.
    /// </summary>
    /// <remarks>
    /// Acknowledging does not make the incident go away: while the problem
    /// lasts Sentinel keeps updating the same row, and resolves it itself once
    /// its checks come back clean.
    /// </remarks>
    [HttpPost("{id:long}/acknowledge")]
    public async Task<IActionResult> Acknowledge(long id, CancellationToken cancellationToken)
    {
        var incident = await _dbContext.Incidents.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (incident is null)
        {
            return NotFound(new { message = $"Incident {id} not found." });
        }

        if (incident.Status != IncidentStatus.Open)
        {
            return Conflict(new
            {
                message = $"Incident {id} is {incident.Status}; only an open incident can be acknowledged.",
                status = incident.Status,
            });
        }

        incident.Status = IncidentStatus.Acknowledged;
        incident.AcknowledgedBy = Actor();
        incident.AcknowledgedUtc = DateTime.UtcNow;

        return await SaveOrConflictAsync(incident, "Acknowledged", cancellationToken);
    }

    /// <summary>
    /// Mark an incident over, from open or acknowledged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This closes the record; it does not fix anything. If the condition is
    /// still true, Sentinel's next check finds it again and, since the old row
    /// is no longer live, opens a fresh incident for it. That is deliberate: a
    /// problem that is still happening should not be silenced by a click.
    /// </para>
    /// <para>
    /// The row records who closed it. Sentinel's own resolve leaves that empty,
    /// so the history can tell "it cleared" from "someone closed it".
    /// </para>
    /// </remarks>
    [HttpPost("{id:long}/resolve")]
    public async Task<IActionResult> Resolve(long id, CancellationToken cancellationToken)
    {
        var incident = await _dbContext.Incidents.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (incident is null)
        {
            return NotFound(new { message = $"Incident {id} not found." });
        }

        if (!IncidentStatus.Live.Contains(incident.Status))
        {
            return Conflict(new
            {
                message = $"Incident {id} is already {incident.Status}.",
                status = incident.Status,
            });
        }

        incident.Status = IncidentStatus.Resolved;
        incident.ResolvedUtc = DateTime.UtcNow;
        incident.ResolvedBy = Actor();

        return await SaveOrConflictAsync(incident, "Resolved", cancellationToken);
    }

    /// <summary>The signed-in user's name, cut to the column's 100 characters.</summary>
    private string Actor()
    {
        string by = User.GetUserName() ?? User.Identity?.Name ?? "admin";
        return by.Length > 100 ? by[..100] : by;
    }

    /// <summary>
    /// Saves a status change, or reports that Sentinel changed the row first.
    /// </summary>
    /// <remarks>
    /// Status is a concurrency token (see <c>IncidentConfiguration</c>): the
    /// update only lands if the row still has the status this request read.
    /// Otherwise Sentinel resolved it in between, and overwriting that would
    /// leave a finished incident live forever.
    /// </remarks>
    /// <param name="incident">The tracked row, already changed.</param>
    /// <param name="verb">For the activity log: "Acknowledged" or "Resolved".</param>
    /// <param name="cancellationToken">The request's.</param>
    private async Task<IActionResult> SaveOrConflictAsync(Incident incident, string verb, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Only once it has landed: a 409 row saying "Resolved" would be a lie.
            var view = ToView(incident);
            HttpContext.Describe($"{verb} incident #{incident.Id}: {view.Title}", "incident", incident.Id.ToString());
            return Ok(view);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = $"Incident {incident.Id} changed while this request was reading it. Reload and try again." });
        }
    }

    /// <summary>Null means no filter.</summary>
    private static bool TryParseStatus(string? raw, out string[]? statuses)
    {
        switch ((raw ?? "live").Trim().ToLowerInvariant())
        {
            case "":
            case "live":
                statuses = IncidentStatus.Live.ToArray();
                return true;
            case "any":
                statuses = null;
                return true;
            case IncidentStatus.Open:
                statuses = [IncidentStatus.Open];
                return true;
            case IncidentStatus.Acknowledged:
                statuses = [IncidentStatus.Acknowledged];
                return true;
            case IncidentStatus.Resolved:
                statuses = [IncidentStatus.Resolved];
                return true;
            default:
                statuses = null;
                return false;
        }
    }

    /// <summary>Null means no filter.</summary>
    private static bool TryParseSeverities(string? raw, out string[]? severities)
    {
        severities = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (parts.Length == 0 || parts.Any(p => !IncidentSeverity.All.Contains(p)))
        {
            return false;
        }

        severities = parts;
        return true;
    }

    /// <summary>The row as the console reads it, with anything that looks like a secret masked.</summary>
    /// <remarks>
    /// The free text is masked; the fingerprint, agent and rule are not. Those
    /// are names Sentinel's code builds from rules, runs and feeds, never text
    /// it quotes, and masking "broker-token:fyers" would break the one field
    /// that says which problem this is.
    /// </remarks>
    internal static IncidentView ToView(Incident x) => new(
        x.Id,
        x.Fingerprint,
        x.Agent,
        x.Rule,
        x.Severity,
        x.Status,
        IncidentRedaction.Mask(x.Title),
        IncidentRedaction.Mask(x.Summary),
        IncidentRedaction.Mask(x.Location),
        ParseEvidence(x.EvidenceJson).Select(IncidentRedaction.Mask).ToList(),
        IncidentRedaction.Mask(x.Suggestion),
        x.Occurrences,
        x.FirstSeenUtc,
        x.LastSeenUtc,
        x.ResolvedUtc,
        x.ResolvedBy,
        x.AcknowledgedBy,
        x.AcknowledgedUtc,
        x.NotifiedUtc);

    /// <summary>
    /// The evidence column as a list of strings.
    /// </summary>
    /// <remarks>
    /// Sentinel writes a JSON array of strings, but the writer is a script and
    /// the column is plain text. Whatever is there is shown, never dropped: a
    /// value that is not valid JSON comes back as one item, verbatim, because
    /// an incident whose evidence cannot be read is still an incident.
    /// </remarks>
    internal static IReadOnlyList<string> ParseEvidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Array => root.EnumerateArray()
                    .Where(e => e.ValueKind != JsonValueKind.Null)
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())
                    .ToList(),
                JsonValueKind.Null => [],
                JsonValueKind.String => [root.GetString()!],
                _ => [json],
            };
        }
        catch (JsonException)
        {
            return [json];
        }
    }
}

/// <summary>An incident as the console reads it; the evidence is already a list.</summary>
/// <param name="ResolvedBy">Who resolved it from the console; null when Sentinel resolved it, or it is still live.</param>
public sealed record IncidentView(
    long Id,
    string Fingerprint,
    string Agent,
    string Rule,
    string Severity,
    string Status,
    string Title,
    string Summary,
    string Location,
    IReadOnlyList<string> Evidence,
    string Suggestion,
    int Occurrences,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    DateTime? ResolvedUtc,
    string? ResolvedBy,
    string? AcknowledgedBy,
    DateTime? AcknowledgedUtc,
    DateTime? NotifiedUtc);

/// <summary>Live incidents counted by severity, and the one that appeared most recently.</summary>
/// <param name="Live">Open plus acknowledged.</param>
/// <param name="Counts">Always has low, medium, high and critical, zero when none.</param>
/// <param name="WorstSeverity">The highest severity with a live incident, or null when there are none.</param>
/// <param name="NewestId">The newest live incident, or null when nothing is live.</param>
/// <param name="NewestTitle">Its title.</param>
/// <param name="NewestSeverity">Its severity.</param>
/// <param name="NewestUtc">When it was first seen.</param>
/// <param name="LastCheckUtc">
/// When Sentinel last finished a round of checks, or null when it never has.
/// Without it an empty list cannot be told from a stopped watchman.
/// </param>
public sealed record IncidentSummary(
    int Live,
    IReadOnlyDictionary<string, int> Counts,
    string? WorstSeverity,
    long? NewestId,
    string? NewestTitle,
    string? NewestSeverity,
    DateTime? NewestUtc,
    DateTime? LastCheckUtc);

/// <summary>
/// Masks anything in an incident's text that looks like a credential.
/// </summary>
/// <remarks>
/// <para>
/// The second layer, not the first: Sentinel is meant to keep secrets out of
/// what it stores. But its evidence quotes log lines and the tail of a
/// traceback, and one slip would otherwise be served to every admin's browser,
/// screen recordings of the console included. A masked harmless string costs
/// nothing; a leaked token costs a rotation.
/// </para>
/// <para>
/// The same shapes Sentinel's <c>notify.py</c> masks for Telegram, with one
/// difference: a key counts only when <c>:</c> or <c>=</c> follows it. Prose
/// such as "FYERS token expired at 08:45" is exactly what this desk's incidents
/// say, and it must stay readable; <c>token=…</c>, <c>"password": "…"</c> and
/// <c>Password=…;</c> in a connection string are what leaks look like.
/// </para>
/// </remarks>
public static partial class IncidentRedaction
{
    private const string Hidden = "…";

    /// <summary>The text with every secret-shaped part replaced by "…"; null becomes empty.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            text = Bearer().Replace(text, m => m.Groups[1].Value + Hidden);
            text = KeyValue().Replace(text, m => m.Groups[1].Value + Hidden);
            text = Jwt().Replace(text, Hidden);
            text = TelegramBotToken().Replace(text, Hidden);
            text = UrlPassword().Replace(text, m => m.Groups[1].Value + Hidden);
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // Text that cannot be checked in time is not shown.
            return "[hidden: this text could not be checked for secrets]";
        }
    }

    /// <summary><c>Authorization: Bearer abc…</c>.</summary>
    [GeneratedRegex(@"(?i)(\bbearer\s+)[A-Za-z0-9._\-]{12,}", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex Bearer();

    /// <summary>
    /// <c>password=…</c>, <c>"access_token": "…"</c>, <c>client_secret=…</c>,
    /// <c>trading_pin: 1234</c>: a secret-named key, then <c>:</c> or <c>=</c>,
    /// then a value.
    /// </summary>
    [GeneratedRegex(
        @"(?i)((?:password|passwd|secret|token|api[_-]?key|access[_-]?key|app[_-]?secret|totp|(?<![a-z])pin)[""']?\s*[:=]\s*[""']?)[^\s""',}]{4,}",
        RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex KeyValue();

    /// <summary>A JSON Web Token on its own: header.payload.signature.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex Jwt();

    /// <summary>A Telegram bot token: the bot id, a colon, the secret.</summary>
    [GeneratedRegex(@"\b[0-9]{8,10}:[A-Za-z0-9_\-]{30,}\b", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex TelegramBotToken();

    /// <summary>The password in <c>scheme://user:password@host</c>.</summary>
    [GeneratedRegex(@"(?i)\b([a-z][a-z0-9+.\-]*://[^/\s:@]+:)[^@\s/]+(?=@)", RegexOptions.None, matchTimeoutMilliseconds: 250)]
    private static partial Regex UrlPassword();
}
