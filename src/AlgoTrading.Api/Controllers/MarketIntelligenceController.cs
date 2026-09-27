using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketIntelligence;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// What the market-intelligence recorders have stored: headlines, NSE filings
/// and board meetings, overseas daily bars and morning snapshots, and NSE
/// breadth; plus how each recorder and backfill stands.
/// </summary>
/// <remarks>
/// <para>
/// Read-only, for the dashboard being designed and for the desk checkup,
/// which reads <c>status</c>. The one write is asking a history backfill to
/// run now, which still waits for the market to close.
/// </para>
/// <para>
/// The reads are for anyone holding the market-data grant, opened on 28 Sep
/// so the Desk can show traders the news, filings, overseas markets and
/// breadth; admins hold every grant. <c>status</c> and the backfill trigger stay with admins:
/// one describes the recorders' health, the other spends the host's time and
/// the sources' patience.
/// </para>
/// <para>
/// <c>from</c> and <c>to</c> on news and announcements are instants: an ISO
/// 8601 time with its offset ("2026-09-28T08:50:00+05:30"), or a date
/// ("2026-09-28"), which means that IST day from its first to its last
/// second. Everywhere else they are IST dates.
/// </para>
/// </remarks>
[Authorize]
[RequireModule(PlatformModules.MarketData)]
[ApiController]
[Route("api/MarketIntelligence")]
public class MarketIntelligenceController : ControllerBase
{
    private const int MaxDays = 4000;
    private const int MaxDaysAllSymbols = 400;

    private readonly MarketIntelligenceQueries _queries;
    private readonly MarketIntelligenceStatus _status;
    private readonly IMarketSessionService _sessions;
    private readonly TimeProvider _time;

    public MarketIntelligenceController(MarketIntelligenceQueries queries, MarketIntelligenceStatus status, IMarketSessionService sessions, TimeProvider? time = null)
    {
        _queries = queries;
        _status = status;
        _sessions = sessions;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Recorded headlines, newest first seen first. <c>q</c> matches title or summary, any case.</summary>
    [HttpGet("news")]
    public async Task<ActionResult<PagedResponse<NewsHeadlineDto>>> News(
        [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? category, [FromQuery] string? q,
        [FromQuery] int skip = 0, [FromQuery] int take = MarketIntelligenceQueries.DefaultTake, CancellationToken cancellationToken = default)
    {
        if (!TryInstant(from, endOfDay: false, out var fromUtc)) return BadRequest(new { message = "from must be an ISO 8601 time with an offset, or a yyyy-MM-dd IST date." });
        if (!TryInstant(to, endOfDay: true, out var toUtc)) return BadRequest(new { message = "to must be an ISO 8601 time with an offset, or a yyyy-MM-dd IST date." });
        if (Paging(skip, take) is { } bad) return bad;
        return Ok(await _queries.NewsAsync(fromUtc, toUtc, category, q, skip, take, cancellationToken));
    }

    /// <summary>NSE filings, newest broadcast first; <c>symbol</c> is the NSE symbol ("RELIANCE").</summary>
    [HttpGet("announcements")]
    public async Task<ActionResult<PagedResponse<AnnouncementDto>>> Announcements(
        [FromQuery] string? symbol, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] int skip = 0, [FromQuery] int take = MarketIntelligenceQueries.DefaultTake, CancellationToken cancellationToken = default)
    {
        if (!TryInstant(from, endOfDay: false, out var fromUtc)) return BadRequest(new { message = "from must be an ISO 8601 time with an offset, or a yyyy-MM-dd IST date." });
        if (!TryInstant(to, endOfDay: true, out var toUtc)) return BadRequest(new { message = "to must be an ISO 8601 time with an offset, or a yyyy-MM-dd IST date." });
        if (Paging(skip, take) is { } bad) return bad;
        return Ok(await _queries.AnnouncementsAsync(symbol, fromUtc, toUtc, skip, take, cancellationToken));
    }

    /// <summary>Board meetings between two IST dates (default: today to 30 days ahead).</summary>
    [HttpGet("calendar")]
    public async Task<ActionResult<List<CalendarEventDto>>> Calendar([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var today = Today();
        var (start, end) = (from ?? today, to ?? today.AddDays(30));
        if (Range(start, end, MaxDaysAllSymbols) is { } bad) return bad;
        return Ok(await _queries.CalendarAsync(start, end, cancellationToken));
    }

    /// <summary>Overseas daily bars (default: the last 30 days). Without <c>symbol</c>, every market, at most 400 days.</summary>
    [HttpGet("global/daily")]
    public async Task<ActionResult<List<GlobalDailyBarDto>>> GlobalDaily([FromQuery] string? symbol, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var today = Today();
        var (start, end) = (from ?? today.AddDays(-30), to ?? today);
        if (Range(start, end, string.IsNullOrWhiteSpace(symbol) ? MaxDaysAllSymbols : MaxDays) is { } bad) return bad;
        return Ok(await _queries.GlobalDailyAsync(symbol, start, end, cancellationToken));
    }

    /// <summary>Every snapshot taken on one IST date (default today), optionally of one key ("GIFTNIFTY", "SPX"...).</summary>
    [HttpGet("global/snapshots")]
    public async Task<ActionResult<List<QuoteSnapshotDto>>> Snapshots([FromQuery] string? key, [FromQuery] DateOnly? date, CancellationToken cancellationToken)
        => Ok(await _queries.SnapshotsAsync(key, date ?? Today(), cancellationToken));

    /// <summary>NSE breadth per session (default: the last 60 days).</summary>
    [HttpGet("breadth")]
    public async Task<ActionResult<List<BreadthDayDto>>> Breadth([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken cancellationToken)
    {
        var today = Today();
        var (start, end) = (from ?? today.AddDays(-60), to ?? today);
        if (Range(start, end, MaxDays) is { } bad) return bad;
        return Ok(await _queries.BreadthAsync(start, end, cancellationToken));
    }

    /// <summary>
    /// Every recorder's last success, rows today, last error and whether it is
    /// overdue; every backfill's reach (first and last date, sessions still
    /// missing). The desk checkup reads this. Admin-only.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpGet("status")]
    public async Task<ActionResult<MarketIntelligenceStatusResponse>> Status(CancellationToken cancellationToken)
        => Ok(await _queries.StatusAsync(cancellationToken));

    /// <summary>
    /// Asks a history backfill to run or resume now: <c>breadth</c>,
    /// <c>participant-oi</c>, <c>global-daily</c> or <c>all</c>. It skips what
    /// is stored, and inside 09:00–15:40 IST on a trading day it waits for
    /// 15:40 (the answer says so). Admin-only.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("backfill/{dataset}")]
    public ActionResult<BackfillRequestResponse> Backfill(string dataset)
    {
        var datasets = string.Equals(dataset, "all", StringComparison.OrdinalIgnoreCase)
            ? MarketIntelligenceNames.Backfills.ToList()
            : MarketIntelligenceNames.Backfills.Where(d => string.Equals(d, dataset, StringComparison.OrdinalIgnoreCase)).ToList();
        if (datasets.Count == 0)
            return BadRequest(new { message = $"Unknown dataset '{dataset}'. Valid: {string.Join(", ", MarketIntelligenceNames.Backfills)}, all." });

        _status.RequestBackfill(datasets);
        bool quiet = MarketIntelligenceSchedule.InQuietWindow(_time.GetUtcNow().UtcDateTime,
            d => _sessions.GetSessionInfo(IstTime.MiddayUtc(d), "NSE", "CM").IsTradingDay);

        return Accepted(new BackfillRequestResponse
        {
            Datasets = datasets,
            StartsNow = !quiet,
            Message = quiet
                ? "Queued: backfills do not run 09:00-15:40 IST on trading days. It starts after 15:40 IST."
                : "Started: it skips what is stored. Follow it on GET api/MarketIntelligence/status.",
        });
    }

    private DateOnly Today() => IstTime.DateOf(_time.GetUtcNow().UtcDateTime);

    private BadRequestObjectResult? Paging(int skip, int take)
    {
        if (skip < 0) return BadRequest(new { message = "skip must be zero or positive." });
        if (take < 1 || take > MarketIntelligenceQueries.MaxTake) return BadRequest(new { message = $"take must be between 1 and {MarketIntelligenceQueries.MaxTake}." });
        return null;
    }

    private BadRequestObjectResult? Range(DateOnly from, DateOnly to, int maxDays)
    {
        if (to < from) return BadRequest(new { message = "'to' is before 'from'." });
        if (to.DayNumber - from.DayNumber > maxDays) return BadRequest(new { message = $"Ask for at most {maxDays} days at a time." });
        return null;
    }

    /// <summary>
    /// An ISO 8601 instant, or a yyyy-MM-dd IST date read as its first
    /// (<paramref name="endOfDay"/> false) or last second. A time without an
    /// offset is refused rather than guessed at: IST and UTC are 5.5 hours
    /// apart, which is the whole morning a forecast is judged on.
    /// </summary>
    public static bool TryInstant(string? text, bool endOfDay, out DateTime? utc)
    {
        utc = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        string value = text.Trim();

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            utc = endOfDay ? IstTime.EndOfDayUtc(day) : IstTime.StartOfDayUtc(day);
            return true;
        }

        bool hasOffset = value.EndsWith('Z') || value.EndsWith('z')
                         || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d{2}:?\d{2}$");
        if (hasOffset && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
        {
            utc = instant.UtcDateTime;
            return true;
        }

        return false;
    }
}
