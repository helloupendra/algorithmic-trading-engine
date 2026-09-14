using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Api.Services;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The option chain: what every strike costs, how much is written on it, and
/// how that has moved through the session.
/// </summary>
/// <remarks>
/// Live and replay are the same endpoint. Ask for the chain now and you get the
/// newest capture; pass <c>asOfUtc</c> and you get the one that was true then —
/// which is what a backtest does as its clock advances. There is no separate
/// "historical chain" path to drift out of step with the live one.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/OptionChain")]
public class OptionChainController : ControllerBase
{
    private readonly OptionChainService _chain;
    private readonly ChainPollerSupervisor _poller;

    public OptionChainController(OptionChainService chain, ChainPollerSupervisor poller)
    {
        _chain = chain;
        _poller = poller;
    }

    /// <summary>
    /// The strike ladder for one underlying and expiry.
    /// </summary>
    /// <param name="asOfUtc">
    /// The replay clock. Omit for the newest chain.
    /// </param>
    [HttpGet]
    public async Task<ActionResult<OptionChainResponse>> GetChain(
        [FromQuery] string underlying,
        [FromQuery] DateOnly? expiry,
        [FromQuery] DateTime? asOfUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        return Ok(await _chain.GetChainAsync(underlying, expiry, asOfUtc?.ToUniversalTime(), cancellationToken));
    }

    /// <summary>
    /// Open paper positions on this underlying's contracts, from running strategy
    /// runs and manual books, marked at the live price. An admin sees every
    /// book; anyone else sees their own.
    /// </summary>
    [HttpGet("positions")]
    public async Task<ActionResult<IReadOnlyList<OptionChainPositionResponse>>> GetPositions(
        [FromQuery] string underlying,
        [FromServices] TradingDbContext db,
        [FromServices] ILotSizeResolver lotSizes,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        string key = underlying.Trim().ToUpperInvariant();
        bool admin = User.IsAdmin();
        long? userId = User.GetUserId();
        if (!admin && userId is null) return Ok(Array.Empty<OptionChainPositionResponse>());

        var rows = await (
                from p in db.PaperPositions.AsNoTracking()
                join r in db.SimulationRuns.AsNoTracking() on p.SimulationRunId equals r.Id
                join i in db.Instruments.AsNoTracking() on p.Symbol equals i.Symbol
                join u in db.AppUsers.AsNoTracking() on r.UserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                join q in db.LiveQuotesLatest.AsNoTracking() on p.Symbol equals q.Symbol into quotes
                from q in quotes.DefaultIfEmpty()
                where p.Status == "Open" && p.Quantity > 0 && r.Status == "Running" && i.Underlying == key
                      && (admin || r.UserId == userId)
                orderby p.OpenedUtc
                select new
                {
                    RunId = r.Id,
                    r.StrategyName,
                    UserName = u != null ? u.UserName : string.Empty,
                    p.GroupId,
                    p.Symbol,
                    i.InstrumentType,
                    i.OptionType,
                    i.StrikePrice,
                    i.ExpiryDate,
                    p.Direction,
                    p.Quantity,
                    p.AveragePrice,
                    p.LastMarkPrice,
                    p.UpdatedUtc,
                    QuotePrice = q != null ? q.LastTradedPrice : null,
                    QuoteUtc = q != null ? (DateTime?)q.UpdatedUtc : null,
                    p.StopLossPrice,
                    p.TargetPrice,
                    p.OpenedUtc,
                })
            .ToListAsync(cancellationToken);

        var lots = rows.Count == 0
            ? new Dictionary<string, LotSizeInfo>()
            : await lotSizes.ResolveManyAsync(rows.Select(x => x.Symbol).Distinct().ToList(), cancellationToken);

        var result = rows.Select(x =>
        {
            int lotSize = lots.TryGetValue(x.Symbol, out var info) && info.LotSize > 0 ? info.LotSize : 1;
            // The live quote wins when it is newer than the engine's last mark:
            // the manual book is marked only when something touches it.
            bool quoteNewer = x.QuotePrice is > 0 && x.QuoteUtc is { } qu && qu >= x.UpdatedUtc;
            decimal? mark = quoteNewer ? x.QuotePrice : x.LastMarkPrice ?? x.QuotePrice;
            return new OptionChainPositionResponse
            {
                RunId = x.RunId,
                StrategyName = x.StrategyName,
                IsManual = string.Equals(x.StrategyName, ManualOrdersController.BookStrategyName, StringComparison.Ordinal),
                UserName = x.UserName,
                GroupId = x.GroupId,
                Symbol = x.Symbol,
                InstrumentType = string.IsNullOrEmpty(x.OptionType) ? x.InstrumentType : x.OptionType,
                StrikePrice = x.StrikePrice,
                ExpiryDate = x.ExpiryDate,
                Direction = x.Direction,
                Quantity = x.Quantity,
                LotSize = lotSize,
                AveragePrice = x.AveragePrice,
                MarkPrice = mark,
                MarkUtc = quoteNewer ? x.QuoteUtc : x.UpdatedUtc,
                UnrealizedPnl = mark is { } m ? PaperPnl.Unrealized(x.Direction, x.AveragePrice, m, x.Quantity, lotSize) : null,
                StopLossPrice = x.StopLossPrice,
                TargetPrice = x.TargetPrice,
                OpenedUtc = x.OpenedUtc,
            };
        }).ToList();

        return Ok(result);
    }

    /// <summary>
    /// The chain as the console reads it: the newest capture with every strike
    /// that has a fresh live quote brought up to the second, plus the header
    /// strip (spot, future, VIX, support/resistance, totals, lot size, market
    /// state, and the time and source of both layers).
    /// </summary>
    /// <param name="asOfUtc">The replay clock: snapshots only, no live overlay.</param>
    [HttpGet("view")]
    public async Task<ActionResult<OptionChainResponse>> GetView(
        [FromQuery] string underlying,
        [FromQuery] DateOnly? expiry,
        [FromQuery] DateTime? asOfUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        return Ok(await _chain.GetViewAsync(underlying, expiry, asOfUtc?.ToUniversalTime(), cancellationToken));
    }

    /// <summary>
    /// The session's trend, one point per capture: spot, total OI each side, PCR.
    /// The session is the IST day of the newest capture at or before <c>toUtc</c>.
    /// </summary>
    [HttpGet("trend")]
    public async Task<ActionResult<OptionChainTrendResponse>> GetTrend(
        [FromQuery] string underlying,
        [FromQuery] DateOnly? expiry,
        [FromQuery] DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        return Ok(await _chain.GetTrendAsync(underlying, expiry, toUtc?.ToUniversalTime(), cancellationToken: cancellationToken));
    }

    /// <summary>
    /// One strike through the session — the OI-change curves.
    /// </summary>
    [HttpGet("series")]
    public async Task<ActionResult<OptionChainSeriesResponse>> GetSeries(
        [FromQuery] string underlying,
        [FromQuery] decimal strike,
        [FromQuery] DateOnly? expiry,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        if (strike <= 0)
            return BadRequest(new { message = "strike must be positive." });

        return Ok(await _chain.GetSeriesAsync(
            underlying, expiry, strike,
            fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime(), cancellationToken));
    }

    /// <summary>Which expiries have been captured for an underlying.</summary>
    [HttpGet("expiries")]
    public async Task<ActionResult<List<DateOnly>>> GetExpiries(
        [FromQuery] string underlying,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        return Ok(await _chain.GetExpiriesAsync(underlying, cancellationToken));
    }

    /// <summary>
    /// Records one poll of the chain. Posted by the ingestor's chain poller.
    /// </summary>
    /// <remarks>
    /// This is the only way open interest enters the platform — the broker's
    /// tick feed does not carry it — so a session with the poller stopped has
    /// prices and volume and no OI at all, for good. It cannot be backfilled.
    /// </remarks>
    [HttpPost("snapshots")]
    public async Task<IActionResult> StoreSnapshots(
        [FromBody] StoreOptionChainRequest? request,
        CancellationToken cancellationToken)
    {
        if (request?.Rows is null || request.Rows.Count == 0)
            return BadRequest(new { message = "rows is required." });

        int stored = await _chain.StoreAsync(request.Rows, cancellationToken);
        return Ok(new { stored });
    }

    // ------------------------------------------------------------------
    // The poller
    // ------------------------------------------------------------------

    /// <summary>
    /// Launches option_chain_poller.py.
    /// </summary>
    /// <remarks>
    /// Worth starting alongside the ingestor, every session. Open interest
    /// enters the platform through this process and nowhere else, and it cannot
    /// be backfilled — a session where this was not running has no OI at all,
    /// for good, including in any backtest of that day.
    /// </remarks>
    [HttpPost("poller/start")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> StartPoller(CancellationToken cancellationToken)
    {
        var outcome = await _poller.StartAsync(cancellationToken);

        HttpContext.Describe(outcome.Started
            ? $"Started the option chain poller (pid {outcome.ProcessId})."
            : $"Could not start the option chain poller: {outcome.Message}");

        if (!outcome.Started)
            return StatusCode(outcome.StatusCode, new { message = outcome.Message, processId = outcome.ProcessId });

        return Ok(new { message = outcome.Message, processId = outcome.ProcessId });
    }

    [HttpPost("poller/stop")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> StopPoller(CancellationToken cancellationToken)
    {
        var userName = User.GetUserName() ?? "unknown";
        var outcome = await _poller.StopAsync($"Stopped by {userName}", cancellationToken);

        HttpContext.Describe(outcome.WasRunning
            ? $"Stopped the option chain poller (pid {outcome.ProcessId}). No open interest is being recorded."
            : "Stopped the option chain poller — it was not running.");

        return Ok(new
        {
            message = outcome.Message,
            wasRunning = outcome.WasRunning,
            processId = outcome.ProcessId,
            source = outcome.Source,
        });
    }

    /// <summary>
    /// { isRunning, managed, processId, source, lastCapturedUtc } — source is
    /// "managed", "adopted" (alive from a previous API instance) or "none".
    /// </summary>
    [HttpGet("poller/status")]
    public async Task<IActionResult> GetPollerStatus(CancellationToken cancellationToken)
    {
        var status = await _poller.GetStatusAsync(cancellationToken);

        // The process being up is not the same as it working. A poller that
        // started but cannot reach the broker looks identical from the outside,
        // so the last capture time is reported next to it.
        var lastCaptured = await _chain.GetLastCaptureUtcAsync(cancellationToken);

        return Ok(new
        {
            isRunning = status.IsRunning,
            managed = status.Managed,
            processId = status.ProcessId,
            source = status.Source,
            lastCapturedUtc = lastCaptured,
        });
    }

    /// <summary>Recent stdout/stderr — where the broker's real field names are logged on first run.</summary>
    [HttpGet("poller/logs")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult GetPollerLogs([FromQuery] int take = 200)
        => Ok(_poller.GetLogs(take));
}
