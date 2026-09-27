using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// What Sentinel, the desk's watchman, found wrong: the list, a count for the
/// console's header, the history of every problem, and the things a person
/// does with an incident — acknowledge it, resolve it, and write down what
/// caused it and what was done.
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
/// Every incident is one episode of a problem, and the problem is its
/// fingerprint. So every incident the API returns says whether that problem
/// has happened before (<see cref="IncidentView.PreviousEpisodes"/>) and what
/// was done the last time (<see cref="IncidentView.LastResolution"/>), and
/// <see cref="History"/> puts all the episodes of each problem side by side:
/// the question "has this happened before, and what fixed it?" is answered
/// from the rows, not from someone's memory.
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

    /// <summary>The longest root cause or resolution stored, after trimming and masking.</summary>
    public const int MaxNoteChars = 2000;

    /// <summary>The longest fix reference stored: a sha, a pull request or a link. The column's length.</summary>
    public const int MaxFixRefChars = 300;

    private const int DefaultHistoryDays = 90;
    private const int MaxHistoryDays = 3650;

    /// <summary>
    /// At most this many episodes of one problem are listed in the history,
    /// newest first; the count covers all of them. A problem that flapped five
    /// hundred times is one fact, not five hundred rows to scroll past.
    /// </summary>
    public const int MaxEpisodesListed = 50;

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

        return Ok(await ViewsAsync(rows, cancellationToken));
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
        return incident is null
            ? NotFound(new { message = $"Incident {id} not found." })
            : Ok((await ViewsAsync([incident], cancellationToken))[0]);
    }

    /// <summary>
    /// Every problem Sentinel has recorded in the last <paramref name="days"/>
    /// days, one row per fingerprint with all its episodes: how often it came
    /// back, how long it took to clear, and what was learnt about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An episode counts when it was seen at any time in the window (its last
    /// sighting is inside it), or is live now whatever its age: a problem that
    /// started before the window and is still going is part of the window.
    /// Everything on a row — episodes, occurrences, first and last seen, the
    /// time to resolve — is over those episodes only, so the numbers on a row
    /// and the episodes listed under it always agree.
    /// </para>
    /// <para>
    /// The time to resolve is from an episode's first sighting to its
    /// resolve, whoever resolved it. When Sentinel resolved it that includes
    /// the clean checks it waits for (a few minutes at most), so it reads as
    /// "how long until it was over", not "how long until the first clean check".
    /// <c>ResolvedEpisodes</c> is how many episodes the mean is over: a mean of
    /// one is an anecdote, and the row says so.
    /// </para>
    /// <para>
    /// Sorted by episodes, then by the latest sighting: what keeps coming back
    /// first, because that is what a fix is worth most on. The rows are read
    /// without their summary and evidence and grouped here rather than in SQL:
    /// the table holds a few dozen episodes a day, and the per-problem "latest
    /// title" and "latest notes" are simpler and exact in C#.
    /// </para>
    /// </remarks>
    /// <param name="days">How far back, 1 to 3650 (clamped); 90 by default.</param>
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] int days = DefaultHistoryDays, CancellationToken cancellationToken = default)
    {
        days = Math.Clamp(days, 1, MaxHistoryDays);
        DateTime since = DateTime.UtcNow.AddDays(-days);
        string[] liveStatuses = IncidentStatus.Live.ToArray();

        var episodes = await _dbContext.Incidents.AsNoTracking()
            .Where(x => x.LastSeenUtc >= since || liveStatuses.Contains(x.Status))
            .Select(x => new IncidentEpisodeRow(
                x.Id, x.Fingerprint, x.Agent, x.Rule, x.Severity, x.Status, x.Title, x.Occurrences,
                x.FirstSeenUtc, x.LastSeenUtc, x.ResolvedUtc, x.ResolvedBy, x.RootCause, x.Resolution, x.FixRef))
            .ToListAsync(cancellationToken);

        var items = episodes
            .GroupBy(e => e.Fingerprint)
            .Select(g => HistoryRow(g.Key, g.ToList()))
            .OrderByDescending(r => r.Episodes)
            .ThenByDescending(r => r.LastSeenUtc)
            .ThenBy(r => r.Fingerprint, StringComparer.Ordinal)
            .ToList();

        return Ok(new IncidentHistory(days, since, items));
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
    /// <para>
    /// The body is optional: what caused it, what was done, and where the fix
    /// is, written while it is fresh — the moment of resolving is when a person
    /// knows most. The notes land in the same update as the status, so a
    /// resolve that 409s stores no notes either. See <see cref="Notes"/> for
    /// how each field is read.
    /// </para>
    /// </remarks>
    [HttpPost("{id:long}/resolve")]
    public async Task<IActionResult> Resolve(
        long id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] IncidentNotesRequest? notes,
        CancellationToken cancellationToken)
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
        ApplyNotes(incident, notes);

        return await SaveOrConflictAsync(incident, "Resolved", cancellationToken);
    }

    /// <summary>
    /// Write or correct what was learnt from an incident — its root cause, what
    /// was done, and where the fix is — open, acknowledged or resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any status, because the cause is often understood only later: the
    /// morning's incident closes itself at 09:20 and the commit that fixes it
    /// lands at 16:00. Writing notes changes nothing else, the status least of
    /// all.
    /// </para>
    /// <para>
    /// Each field: absent or null leaves it as it is; an empty string clears
    /// it; anything else is trimmed, masked like the rest of an incident's text
    /// (a pasted log line can carry a token), and cut to its limit
    /// (<see cref="MaxNoteChars"/>, <see cref="MaxFixRefChars"/>). Masked
    /// before it is stored, not only when it is shown: these are the only
    /// columns people write, and what is stored is what the history keeps.
    /// </para>
    /// <para>
    /// The same concurrency rule as acknowledge and resolve: the update carries
    /// the status this request read, so if Sentinel changed it in between the
    /// answer is 409 and nothing is stored, rather than a write that quietly
    /// raced a status change.
    /// </para>
    /// </remarks>
    [HttpPost("{id:long}/notes")]
    public async Task<IActionResult> Notes(long id, [FromBody] IncidentNotesRequest? notes, CancellationToken cancellationToken)
    {
        if (notes is null || (notes.RootCause is null && notes.Resolution is null && notes.FixRef is null))
        {
            return BadRequest(new { message = "Send rootCause, resolution or fixRef; an empty string clears one." });
        }

        var incident = await _dbContext.Incidents.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (incident is null)
        {
            return NotFound(new { message = $"Incident {id} not found." });
        }

        ApplyNotes(incident, notes);

        return await SaveOrConflictAsync(incident, "Updated the notes of", cancellationToken);
    }

    /// <summary>The notes a request sent, onto the tracked row; a field left out is left alone.</summary>
    private static void ApplyNotes(Incident incident, IncidentNotesRequest? notes)
    {
        if (notes is null)
        {
            return;
        }

        if (notes.RootCause is not null)
        {
            incident.RootCause = CleanNote(notes.RootCause, MaxNoteChars);
        }

        if (notes.Resolution is not null)
        {
            incident.Resolution = CleanNote(notes.Resolution, MaxNoteChars);
        }

        if (notes.FixRef is not null)
        {
            incident.FixRef = CleanNote(notes.FixRef, MaxFixRefChars);
        }
    }

    /// <summary>
    /// A note as it is stored: trimmed, masked, then cut to <paramref name="limit"/>;
    /// null when nothing is left.
    /// </summary>
    /// <remarks>
    /// Masked before it is cut, so a secret straddling the limit is masked
    /// whole rather than cut to a stub the patterns no longer recognise (a
    /// mask never makes text longer, so the cut still fits the column). A NUL
    /// becomes U+FFFD, since Postgres text cannot hold one, and the cut never
    /// splits a surrogate pair, which the database's UTF-8 would refuse.
    /// </remarks>
    public static string? CleanNote(string text, int limit)
    {
        string masked = IncidentRedaction.Mask(text.Replace('\0', '�').Trim());
        if (masked.Length > limit)
        {
            int cut = char.IsHighSurrogate(masked[limit - 1]) ? limit - 1 : limit;
            masked = masked[..cut].TrimEnd();
        }

        return masked.Length == 0 ? null : masked;
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
    /// <param name="verb">For the activity log: "Acknowledged", "Resolved" or "Updated the notes of".</param>
    /// <param name="cancellationToken">The request's.</param>
    private async Task<IActionResult> SaveOrConflictAsync(Incident incident, string verb, CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new { message = $"Incident {incident.Id} changed while this request was reading it. Reload and try again." });
        }

        // Only once it has landed: a 409 row saying "Resolved" would be a lie.
        var view = (await ViewsAsync([incident], cancellationToken))[0];
        HttpContext.Describe($"{verb} incident #{incident.Id}: {view.Title}", "incident", incident.Id.ToString());
        return Ok(view);
    }

    /// <summary>
    /// The rows as the console reads them, each with whether its problem has
    /// happened before — from one query for the whole page, not one per row.
    /// </summary>
    /// <remarks>
    /// One read of every episode of the page's fingerprints (a handful of
    /// columns, no summary or evidence), grouped here: "before this one"
    /// depends on each row's own start, so a SQL GROUP BY alone cannot answer
    /// it for a page that holds several episodes of one problem.
    /// </remarks>
    private async Task<List<IncidentView>> ViewsAsync(IReadOnlyList<Incident> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        string[] fingerprints = rows.Select(r => r.Fingerprint).Distinct().ToArray();
        var episodes = await _dbContext.Incidents.AsNoTracking()
            .Where(x => fingerprints.Contains(x.Fingerprint))
            .Select(x => new IncidentEpisodeRow(
                x.Id, x.Fingerprint, x.Agent, x.Rule, x.Severity, x.Status, x.Title, x.Occurrences,
                x.FirstSeenUtc, x.LastSeenUtc, x.ResolvedUtc, x.ResolvedBy, x.RootCause, x.Resolution, x.FixRef))
            .ToListAsync(cancellationToken);

        var byFingerprint = episodes
            .GroupBy(e => e.Fingerprint)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<IncidentEpisodeRow>)g.OrderBy(e => e.FirstSeenUtc).ThenBy(e => e.Id).ToList());

        return rows.Select(r => ToView(r, SeenBefore(r, byFingerprint.GetValueOrDefault(r.Fingerprint) ?? []))).ToList();
    }

    /// <summary>
    /// How often this row's problem happened before it, since when, and how
    /// the episode just before it ended.
    /// </summary>
    /// <remarks>
    /// "Before" is by when an episode was first seen (by id when two started
    /// at the same instant), never by id alone: ids say which row was inserted
    /// first, which is the same thing today but not a promise. The previous
    /// episode is the latest of those, notes or none: "what was done last
    /// time" is about last time, not the last time someone wrote something.
    /// </remarks>
    /// <param name="row">The incident being shown.</param>
    /// <param name="episodes">Every episode of its fingerprint, oldest first; may be missing the row itself.</param>
    internal static SeenBeforeFacts SeenBefore(Incident row, IReadOnlyList<IncidentEpisodeRow> episodes)
    {
        var earlier = episodes
            .Where(e => e.Id != row.Id
                && (e.FirstSeenUtc < row.FirstSeenUtc || (e.FirstSeenUtc == row.FirstSeenUtc && e.Id < row.Id)))
            .ToList();

        DateTime firstEver = earlier.Count > 0 && earlier[0].FirstSeenUtc < row.FirstSeenUtc
            ? earlier[0].FirstSeenUtc
            : row.FirstSeenUtc;

        return new SeenBeforeFacts(earlier.Count, firstEver, earlier.Count == 0 ? null : ResolutionOf(earlier[^1]));
    }

    /// <summary>One problem's row in the history, from its episodes in the window.</summary>
    internal static IncidentHistoryRow HistoryRow(string fingerprint, IReadOnlyList<IncidentEpisodeRow> episodes)
    {
        var newestFirst = episodes.OrderByDescending(e => e.FirstSeenUtc).ThenByDescending(e => e.Id).ToList();
        var latest = newestFirst[0];

        var resolved = episodes
            .Where(e => e.Status == IncidentStatus.Resolved && e.ResolvedUtc is not null)
            .ToList();
        double? meanSeconds = resolved.Count == 0
            ? null
            : resolved.Average(e => (e.ResolvedUtc!.Value - e.FirstSeenUtc).TotalSeconds);

        // The loudest the problem has been. A spelling the table would refuse
        // anyway ranks below low, so it can never pass for the worst.
        string severity = episodes
            .OrderByDescending(e => SeverityRank(e.Severity))
            .ThenByDescending(e => e.FirstSeenUtc)
            .First().Severity;

        var withNotes = newestFirst.FirstOrDefault(e =>
            !string.IsNullOrEmpty(e.RootCause) || !string.IsNullOrEmpty(e.Resolution) || !string.IsNullOrEmpty(e.FixRef));

        return new IncidentHistoryRow(
            fingerprint,
            latest.Agent,
            latest.Rule,
            IncidentRedaction.Mask(latest.Title),
            severity,
            episodes.Count,
            resolved.Count,
            episodes.Sum(e => e.Occurrences),
            episodes.Min(e => e.FirstSeenUtc),
            episodes.Max(e => e.LastSeenUtc),
            meanSeconds,
            episodes.Any(e => IncidentStatus.Live.Contains(e.Status)),
            withNotes is null ? null : ResolutionOf(withNotes),
            newestFirst.Take(MaxEpisodesListed).Select(e => new IncidentEpisodeView(
                e.Id,
                e.Status,
                e.Severity,
                IncidentRedaction.Mask(e.Title),
                e.Occurrences,
                e.FirstSeenUtc,
                e.LastSeenUtc,
                e.ResolvedUtc,
                e.ResolvedBy,
                MaskOrNull(e.RootCause),
                MaskOrNull(e.Resolution),
                MaskOrNull(e.FixRef))).ToList());
    }

    private static int SeverityRank(string severity)
    {
        for (int i = 0; i < IncidentSeverity.All.Count; i++)
        {
            if (IncidentSeverity.All[i] == severity)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>How an episode ended and what was written about it.</summary>
    private static IncidentResolutionView ResolutionOf(IncidentEpisodeRow e) => new(
        e.Id,
        e.Status,
        e.ResolvedUtc,
        e.ResolvedBy,
        MaskOrNull(e.RootCause),
        MaskOrNull(e.Resolution),
        MaskOrNull(e.FixRef));

    /// <summary>
    /// Masked like all incident text, but absent stays absent: an empty string
    /// would make "nobody wrote a root cause" look like "someone wrote nothing".
    /// </summary>
    private static string? MaskOrNull(string? text) => string.IsNullOrEmpty(text) ? null : IncidentRedaction.Mask(text);

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
    /// that says which problem this is. The notes were masked when they were
    /// written and are masked again here, in case a row was edited by hand.
    /// </remarks>
    internal static IncidentView ToView(Incident x, SeenBeforeFacts seen) => new(
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
        x.NotifiedUtc,
        MaskOrNull(x.RootCause),
        MaskOrNull(x.Resolution),
        MaskOrNull(x.FixRef),
        seen.PreviousEpisodes,
        seen.FirstEverUtc,
        seen.LastResolution);

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
/// <param name="RootCause">Why it happened, as a person wrote it; null until someone does.</param>
/// <param name="Resolution">What was done about it; null until someone writes it.</param>
/// <param name="FixRef">Where the fix lives: a commit sha, a pull request or a link.</param>
/// <param name="PreviousEpisodes">How many other episodes of the same fingerprint started before this one; 0 the first time.</param>
/// <param name="FirstEverUtc">When this problem was first seen at all: the earliest episode's first sighting, this one's own when it is the first.</param>
/// <param name="LastResolution">How the episode just before this one ended and what was written about it; null the first time.</param>
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
    DateTime? NotifiedUtc,
    string? RootCause,
    string? Resolution,
    string? FixRef,
    int PreviousEpisodes,
    DateTime FirstEverUtc,
    IncidentResolutionView? LastResolution);

/// <summary>
/// What a person writes about an incident, on resolve or from "Edit notes".
/// Each field: null or absent leaves it as it is, an empty string clears it.
/// </summary>
public sealed record IncidentNotesRequest(string? RootCause, string? Resolution, string? FixRef);

/// <summary>How one episode ended, and what was learnt from it.</summary>
/// <param name="Id">The episode's incident number.</param>
/// <param name="Status">Normally resolved; an earlier episode still live would be a problem of its own, and shows as such.</param>
/// <param name="ResolvedUtc">When it ended; null if it has not.</param>
/// <param name="ResolvedBy">Who resolved it from the console; null when Sentinel's checks came back clean.</param>
public sealed record IncidentResolutionView(
    long Id,
    string Status,
    DateTime? ResolvedUtc,
    string? ResolvedBy,
    string? RootCause,
    string? Resolution,
    string? FixRef);

/// <summary>Whether an incident's problem happened before it: the part of <see cref="IncidentView"/> computed across episodes.</summary>
public sealed record SeenBeforeFacts(int PreviousEpisodes, DateTime FirstEverUtc, IncidentResolutionView? LastResolution);

/// <summary>
/// The columns of one incident the history and "seen before" need: no summary,
/// evidence or suggestion, which are the bulk of a row and say nothing about
/// how often a problem comes back.
/// </summary>
public sealed record IncidentEpisodeRow(
    long Id,
    string Fingerprint,
    string Agent,
    string Rule,
    string Severity,
    string Status,
    string Title,
    int Occurrences,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    DateTime? ResolvedUtc,
    string? ResolvedBy,
    string? RootCause,
    string? Resolution,
    string? FixRef);

/// <summary>GET /api/Incidents/history: every problem seen in the window, most recurrent first.</summary>
/// <param name="Days">The window actually used, after clamping.</param>
/// <param name="SinceUtc">Its start: episodes seen since then, and every live one.</param>
public sealed record IncidentHistory(int Days, DateTime SinceUtc, IReadOnlyList<IncidentHistoryRow> Items);

/// <summary>One problem (one fingerprint) over the window.</summary>
/// <param name="Title">The latest episode's title.</param>
/// <param name="Severity">The loudest any episode reached.</param>
/// <param name="Episodes">How many times it happened: rows, not sightings.</param>
/// <param name="ResolvedEpisodes">How many of those ended — what the mean time to resolve is over.</param>
/// <param name="Occurrences">Sightings across every episode.</param>
/// <param name="FirstSeenUtc">The first sighting in the window.</param>
/// <param name="LastSeenUtc">The latest sighting.</param>
/// <param name="MeanTimeToResolveSeconds">From first sighting to resolve, averaged over the resolved episodes; null when none has ended.</param>
/// <param name="OpenNow">An episode is open or acknowledged now.</param>
/// <param name="LatestResolution">The newest episode anyone wrote notes on, with them; null when nobody has.</param>
/// <param name="EpisodeList">The episodes, newest first, at most <see cref="IncidentsController.MaxEpisodesListed"/>.</param>
public sealed record IncidentHistoryRow(
    string Fingerprint,
    string Agent,
    string Rule,
    string Title,
    string Severity,
    int Episodes,
    int ResolvedEpisodes,
    int Occurrences,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    double? MeanTimeToResolveSeconds,
    bool OpenNow,
    IncidentResolutionView? LatestResolution,
    IReadOnlyList<IncidentEpisodeView> EpisodeList);

/// <summary>One episode in the history: its dates, how it ended, and its notes.</summary>
/// <param name="ResolvedBy">Who resolved it from the console; null when Sentinel did, or it is live.</param>
public sealed record IncidentEpisodeView(
    long Id,
    string Status,
    string Severity,
    string Title,
    int Occurrences,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    DateTime? ResolvedUtc,
    string? ResolvedBy,
    string? RootCause,
    string? Resolution,
    string? FixRef);

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
