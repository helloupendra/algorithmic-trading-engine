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
/// nothing; a leaked token costs a rotation. A mask that garbles the desk's own
/// prose ("FYERS token expired at 08:45", "SSH password guessing from …") is a
/// failure too, so a key counts only when <c>:</c> or <c>=</c> follows it on
/// the same line.
/// </para>
/// <para>
/// One spec, identical in every layer that sends or shows incident text:
/// Sentinel's <c>notify.py</c> (Telegram), this class (the API),
/// <c>maskSecrets</c> in <c>web/src/lib/incidents.ts</c> (the console), and the
/// logs agent's filter for lines that may hold a secret. Change one, change
/// all; each has a table-driven test with the same cases.
/// </para>
/// <list type="number">
/// <item><c>Authorization: Bearer|Basic &lt;value&gt;</c>: the value.</item>
/// <item><c>Bearer</c> and 12 or more token characters anywhere.</item>
/// <item><c>scheme://user:password@</c> and <c>scheme://:password@</c>: the password.</item>
/// <item>
/// <c>key=value</c>, <c>key: value</c>, <c>"key": "value"</c>, where the key is
/// an identifier with a whole part (underscore separated, or the end of a
/// camelCase key) that is secret, password, passwd, pwd, token, api_key,
/// private_key, totp or pin: <c>DHAN_PIN</c>, <c>JWT_SECRET_KEY</c>,
/// <c>access_token</c>, <c>accessToken</c>, <c>X-Api-Key</c>; not "tokens" or
/// "Skipping". The separator is an optional quote, spaces or tabs (never a
/// newline), then <c>:</c> or <c>=</c> but not <c>==</c>. The value runs to
/// whitespace, a quote, <c>&amp;</c>, <c>,</c> or <c>;</c> — or, quoted, to its
/// closing quote.
/// </item>
/// <item>Telegram bot tokens anywhere, <c>/bot&lt;token&gt;/</c> in a URL included.</item>
/// <item>JSON Web Tokens: <c>eyJ….eyJ….&lt;signature&gt;</c>.</item>
/// </list>
/// </remarks>
public static partial class IncidentRedaction
{
    private const string Hidden = "…";

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>The text with every secret-shaped part replaced by "…"; null becomes empty.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        try
        {
            text = AuthorizationHeader().Replace(text, m => m.Groups[1].Value + Hidden);
            text = Bearer().Replace(text, m => m.Groups[1].Value + Hidden);
            text = UrlPassword().Replace(text, m => m.Groups[1].Value + Hidden);
            text = KeyValue().Replace(text, MaskValue);
            text = Jwt().Replace(text, Hidden);
            text = TelegramBotToken().Replace(text, Hidden);
            return text;
        }
        catch (RegexMatchTimeoutException)
        {
            // Text that cannot be checked in time is not shown.
            return "[hidden: this text could not be checked for secrets]";
        }
    }

    /// <summary>The key and separator kept, the value hidden; a quoted value keeps its quotes.</summary>
    private static string MaskValue(Match m)
    {
        string value = m.Groups[2].Value;
        string lead = value[0] is '"' or '\'' ? value[..1] : string.Empty;
        string tail = lead.Length > 0 && value.Length > 1 && value[^1] == lead[0] ? lead : string.Empty;
        return m.Groups[1].Value + lead + Hidden + tail;
    }

    /// <summary><c>Authorization: Bearer abc…</c>, <c>{'Authorization': 'Basic abc…'}</c>.</summary>
    [GeneratedRegex(@"(authorization[""']?[ \t]*[:=][ \t]*[""']?(?:bearer|basic)[ \t]+)[^\s""'&,;]+", Options, matchTimeoutMilliseconds: 250)]
    private static partial Regex AuthorizationHeader();

    /// <summary>A bearer token quoted without its header name.</summary>
    [GeneratedRegex(@"(\bbearer[ \t]+)[A-Za-z0-9._~+/=-]{12,}", Options, matchTimeoutMilliseconds: 250)]
    private static partial Regex Bearer();

    /// <summary>The password in <c>scheme://user:password@host</c> or <c>scheme://:password@host</c>.</summary>
    [GeneratedRegex(@"\b([a-z][a-z0-9+.-]*://[^\s:/@]*:)[^\s@/]+(?=@)", Options, matchTimeoutMilliseconds: 250)]
    private static partial Regex UrlPassword();

    /// <summary>
    /// A secret-named key, then <c>:</c> or <c>=</c> on the same line, then a
    /// value. Lengths are bounded so a long identifier cannot make it backtrack.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_])([A-Za-z0-9_]{0,64}?(?:secret|password|passwd|pwd|token|api[_-]?key|private[_-]?key|totp|pin)" +
        @"(?:_[A-Za-z0-9]{1,32}){0,8}[""']?[ \t]*[:=](?!=)[ \t]*)" +
        @"(""[^""\r\n]+""|'[^'\r\n]+'|[""']?[^\s""'&,;]+)",
        Options, matchTimeoutMilliseconds: 250)]
    private static partial Regex KeyValue();

    /// <summary>A JSON Web Token on its own: header.payload.signature.</summary>
    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Jwt();

    /// <summary>A Telegram bot token: the bot id, a colon, the secret — inside <c>/bot…/</c> too.</summary>
    [GeneratedRegex(@"(?<![0-9])[0-9]{8,10}:[A-Za-z0-9_-]{30,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex TelegramBotToken();
}
