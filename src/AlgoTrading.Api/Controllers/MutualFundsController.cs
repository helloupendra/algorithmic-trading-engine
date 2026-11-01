using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.MutualFunds;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MutualFunds;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using AlgoTrading.Infrastructure.Services.MutualFunds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Mutual funds: AMFI's scheme master as we cleaned it, every scheme's daily
/// NAV, and what is stored (coverage) before anything is picked (Phase 1);
/// the fund report, the comparison and the report's written summary
/// (Phase 2). See docs/modules/mutual_funds_module.md.
/// </summary>
/// <remarks>
/// The reads are for anyone holding the market-data grant, like the other
/// market data; admins hold every grant. Running a sync, the history backfill,
/// the report's jobs and the spot check against mfapi.in stay with admins:
/// they spend the host's time and the sources' patience. Facts only: nothing
/// here rates or recommends a fund; a category position is a rank by a stated
/// return.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Authorize]
[RequireModule(PlatformModules.MarketData)]
public class MutualFundsController : ControllerBase
{
    private readonly MutualFundQueries _queries;
    private readonly MutualFundNavSync _sync;
    private readonly MutualFundStatus _status;
    private readonly IProcessSettingsStore _settings;
    private readonly IMarketSessionService _sessions;
    private readonly MutualFundReports _reports;
    private readonly MutualFundBenchmarkSync _benchmarks;
    private readonly MutualFundSummaryWriter _summaries;
    private readonly TimeProvider _time;

    public MutualFundsController(MutualFundQueries queries, MutualFundNavSync sync, MutualFundStatus status, IProcessSettingsStore settings,
        IMarketSessionService sessions, MutualFundReports reports, MutualFundBenchmarkSync benchmarks, MutualFundSummaryWriter summaries,
        TimeProvider? time = null)
    {
        _queries = queries;
        _sync = sync;
        _status = status;
        _settings = settings;
        _sessions = sessions;
        _reports = reports;
        _benchmarks = benchmarks;
        _summaries = summaries;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// What is stored: schemes by plan, option, category and AMC (all and
    /// active), the NAV rows' date range and count, how far the newest full
    /// NAV day is behind the business day expected by now, the last sync and
    /// the history backfill. The desk checkup reads this.
    /// </summary>
    [HttpGet("coverage")]
    public async Task<ActionResult<MutualFundCoverageResponse>> Coverage(CancellationToken cancellationToken)
    {
        var coverage = await _queries.CoverageAsync(cancellationToken);
        coverage.Analytics = await _reports.CoverageAsync(_benchmarks, cancellationToken);
        return Ok(coverage);
    }

    /// <summary>
    /// Schemes whose name or AMC contains <paramref name="search"/> (any case),
    /// or whose code or ISIN it is; active first. <paramref name="plan"/>:
    /// direct, regular or unknown. <paramref name="option"/>: growth, idcw, other
    /// or unknown.
    /// </summary>
    [HttpGet("schemes")]
    public async Task<ActionResult<List<MutualFundSchemeDto>>> Schemes([FromQuery] string? search, [FromQuery] string? plan,
        [FromQuery] string? option, [FromQuery] bool? active, [FromQuery] int take = 50, CancellationToken cancellationToken = default)
    {
        if (take < 1 || take > MutualFundQueries.MaxTake)
            return BadRequest(new { message = $"take must be between 1 and {MutualFundQueries.MaxTake}." });
        return Ok(await _queries.SchemesAsync(search, plan, option, active, take, cancellationToken));
    }

    /// <summary>One scheme's NAVs, oldest first, between two IST dates (yyyy-MM-dd; either may be left out).</summary>
    [HttpGet("{code:int}/nav")]
    public async Task<ActionResult<MutualFundNavResponse>> Nav(int code, [FromQuery] string? from, [FromQuery] string? to,
        CancellationToken cancellationToken)
    {
        if (!TryDate(from, out var start) || !TryDate(to, out var end))
            return BadRequest(new { message = "from and to are dates, yyyy-MM-dd." });
        if (start is not null && end is not null && end < start)
            return BadRequest(new { message = "'to' is before 'from'." });

        var result = await _queries.NavAsync(code, start, end, cancellationToken);
        return result is null ? NotFound(new { message = $"No scheme {code} in the master." }) : Ok(result);
    }

    /// <summary>
    /// The fund report: returns over 1, 3, 5 and 10 years and by calendar year, rolling 3-year returns, a ₹10,000
    /// monthly SIP and a ₹2,00,000 lump sum, volatility and drawdowns, the benchmark and category comparisons, the
    /// TER and the direct-versus-regular gap, each with its dates and source, to <paramref name="asOf"/> (yyyy-MM-dd,
    /// the last NAV when left out). Facts only. See docs/modules/mutual_funds_module.md.
    /// </summary>
    [HttpGet("{code:int}/report")]
    public async Task<ActionResult<MutualFundReportResponse>> Report(int code, [FromQuery] string? asOf, CancellationToken cancellationToken)
    {
        if (!TryDate(asOf, out var day)) return BadRequest(new { message = "asOf is a date, yyyy-MM-dd." });
        var (report, notFound) = await _reports.ReportAsync(code, day, cancellationToken);
        return report is null ? NotFound(new { message = notFound }) : Ok(report);
    }

    /// <summary>
    /// The written summary of a fund report: one paragraph a model wrote and code checked (every number from the
    /// report, no advice or rating wording), or the reason there is none. Slow the first time (a model call); the
    /// report does not wait for it.
    /// </summary>
    [HttpGet("{code:int}/report/summary")]
    public async Task<ActionResult<MutualFundSummaryResponse>> Summary(int code, [FromQuery] string? asOf, CancellationToken cancellationToken)
    {
        if (!TryDate(asOf, out var day)) return BadRequest(new { message = "asOf is a date, yyyy-MM-dd." });
        var (report, notFound) = await _reports.ReportAsync(code, day, cancellationToken);
        if (report is null) return NotFound(new { message = notFound });
        return Ok(await _summaries.WriteAsync(User.GetRequiredUserId(), report, cancellationToken));
    }

    /// <summary>2 to 4 schemes' reports, all ending on the newest NAV day every one of them has. <paramref name="codes"/>: comma-separated scheme codes.</summary>
    [HttpGet("compare")]
    public async Task<ActionResult<MutualFundCompareResponse>> Compare([FromQuery] string? codes, CancellationToken cancellationToken)
    {
        var parsed = new List<int>();
        foreach (string part in (codes ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int code))
                return BadRequest(new { message = $"'{part}' is not a scheme code; codes is a comma-separated list such as 120716,100822." });
            parsed.Add(code);
        }

        var (compare, problem) = await _reports.CompareAsync(parsed, cancellationToken);
        if (compare is not null) return Ok(compare);
        return problem is not null && problem.StartsWith("No scheme", StringComparison.Ordinal) ? NotFound(new { message = problem }) : BadRequest(new { message = problem });
    }

    /// <summary>
    /// Asks for a fund-report job now: <paramref name="job"/> is benchmarks (NSE total-return indices), ter (AMFI's
    /// TER) or stats (the category ranking). It runs at the service's next look, outside 09:00–15:40 IST on trading
    /// days, never at the same time as another of them. Admin-only.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("analytics/{job}")]
    public async Task<ActionResult<object>> RunJob(string job, CancellationToken cancellationToken)
    {
        string key = (job ?? string.Empty).Trim().ToLowerInvariant();
        if (!MutualFundJobs.All.Contains(key))
            return NotFound(new { message = $"No job '{job}': benchmarks, ter or stats." });

        await MutualFundAnalyticsSettings.UpdateAsync(_settings, s =>
        {
            if (!s.Requested.Contains(key)) s.Requested.Add(key);
        }, cancellationToken);
        _status.WakeAnalytics();

        var now = _time.GetUtcNow().UtcDateTime;
        bool quiet = MarketIntelligenceSchedule.InQuietWindow(now, d => _sessions.GetSessionInfo(IstTime.MiddayUtc(d), "NSE", "CM").IsTradingDay);
        string until = TimeOnly.FromTimeSpan(MarketIntelligenceSchedule.QuietUntilOn(IstTime.DateOf(now))).ToString("HH:mm", CultureInfo.InvariantCulture);
        bool busy = _status.AnalyticsNow is not null || _status.SyncRunning;
        return Accepted(new
        {
            startsNow = !quiet && !busy,
            message = quiet
                ? $"Queued: the fund-report jobs do not run 09:00-{until} IST on trading days. It runs after {until} IST."
                : busy
                    ? "Queued: it runs as soon as the job running now finishes. Follow it on GET api/MutualFunds/coverage (analytics)."
                    : "Starting now. Follow it on GET api/MutualFunds/coverage (analytics).",
        });
    }

    /// <summary>
    /// Compares one scheme's stored NAVs with mfapi.in's copy of AMFI's history,
    /// day by day: matching, different, and missing on either side. Nothing is
    /// stored. Admin-only.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpGet("{code:int}/check")]
    public async Task<ActionResult<MutualFundSpotCheckResponse>> Check(int code, CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _queries.SpotCheckAsync(code, cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = $"mfapi.in did not answer: {ex.Message}" });
        }
    }

    /// <summary>Runs the daily sync now and answers with its result; it skips what is stored. Admin-only.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("sync")]
    public async Task<ActionResult<MutualFundSyncDto>> Sync(CancellationToken cancellationToken)
    {
        if (_status.SyncRunning)
            return Conflict(new { message = "A sync is running now; GET api/MutualFunds/coverage shows when it finishes." });

        var result = await _sync.RunAsync("manual", cancellationToken);
        return Ok(new MutualFundSyncDto
        {
            Trigger = result.Trigger,
            StartedUtc = result.StartedUtc,
            FinishedUtc = result.FinishedUtc,
            Ok = result.Ok,
            Message = result.Message,
            AsOfDate = result.AsOfDate,
            Rows = result.Rows,
            NewSchemes = result.NewSchemes,
            NavRowsWritten = result.NavRowsWritten,
        });
    }

    /// <summary>
    /// Starts the AMFI NAV history backfill, or resumes it where it stopped:
    /// 90-day windows, newest first, back to <paramref name="from"/> (default
    /// and earliest 2006-04-01). <paramref name="maxWindows"/> stops after that
    /// many windows (a trial); <paramref name="restart"/> starts again from
    /// yesterday. It runs outside 09:00–15:40 IST on trading days. Admin-only.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("backfill")]
    public async Task<ActionResult<MutualFundBackfillRequestResponse>> StartBackfill([FromQuery] string? from, [FromQuery] int? maxWindows,
        [FromQuery] bool restart = false, CancellationToken cancellationToken = default)
    {
        if (!TryDate(from, out var start))
            return BadRequest(new { message = "from is a date, yyyy-MM-dd." });
        if (start is { } wanted && wanted < MutualFundHistory.Earliest)
            return BadRequest(new { message = $"AMFI's NAV history starts on {MutualFundHistory.Earliest:yyyy-MM-dd}." });
        if (maxWindows is < 1)
            return BadRequest(new { message = "maxWindows must be at least 1." });
        if (_status.BackfillRunning)
            return Conflict(new { message = "The backfill is running now; pause it first (DELETE api/MutualFunds/backfill) to change it." });

        var now = _time.GetUtcNow().UtcDateTime;
        var state = await MutualFundSettings.BackfillAsync(_settings, cancellationToken);
        if (restart || state.Top is null)
        {
            state = new MutualFundBackfillState { Top = IstTime.DateOf(now).AddDays(-1), StartedUtc = now };
        }

        if (start is not null) state.From = start.Value;
        state.MaxWindows = maxWindows;
        state.Requested = true;
        state.State = "queued";
        state.LastError = null;
        state.UpdatedUtc = now;
        await MutualFundSettings.SaveBackfillAsync(_settings, state, cancellationToken);

        _status.StopBackfill = false;
        _status.Wake();

        bool quiet = MarketIntelligenceSchedule.InQuietWindow(now, d => _sessions.GetSessionInfo(IstTime.MiddayUtc(d), "NSE", "CM").IsTradingDay);
        string until = TimeOnly.FromTimeSpan(MarketIntelligenceSchedule.QuietUntilOn(IstTime.DateOf(now))).ToString("HH:mm", CultureInfo.InvariantCulture);
        return Accepted(new MutualFundBackfillRequestResponse
        {
            StartsNow = !quiet,
            Message = quiet
                ? $"Queued: the backfill does not run 09:00-{until} IST on trading days. It starts after {until} IST."
                : "Started: it skips what is stored. Follow it on GET api/MutualFunds/coverage (backfill).",
            Backfill = await _queries.BackfillAsync(cancellationToken),
        });
    }

    /// <summary>Pauses the history backfill before its next request; POST resumes it where it stopped. Admin-only.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpDelete("backfill")]
    public async Task<ActionResult<MutualFundBackfillRequestResponse>> PauseBackfill(CancellationToken cancellationToken)
    {
        _status.StopBackfill = true;
        var state = await MutualFundSettings.BackfillAsync(_settings, cancellationToken);
        state.Requested = false;
        if (!_status.BackfillRunning && state.State is not "done") state.State = "paused";
        state.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        await MutualFundSettings.SaveBackfillAsync(_settings, state, cancellationToken);

        return Ok(new MutualFundBackfillRequestResponse
        {
            StartsNow = false,
            Message = _status.BackfillRunning
                ? "Pausing: the request being read now finishes first. POST api/MutualFunds/backfill resumes it."
                : "Paused. POST api/MutualFunds/backfill resumes it.",
            Backfill = await _queries.BackfillAsync(cancellationToken),
        });
    }

    private static bool TryDate(string? text, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed;
        return true;
    }
}
