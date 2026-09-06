using AlgoTrading.Api.Security;
using AlgoTrading.Contracts.OptionChain;
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

    public OptionChainController(OptionChainService chain) => _chain = chain;

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
}
