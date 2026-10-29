using AlgoTrading.Api.Security;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The at-the-money straddle through a session, and the ATM IV day by day:
/// how fast the premium is decaying today against other days, and whether
/// today's IV is high or low for this underlying.
/// </summary>
/// <remarks>
/// Both are read from the chain the recorder already writes every minute,
/// one expiry per day (the nearest on or after it), so neither needs a
/// recorder or a table of its own. Read-only, for anyone holding the
/// market-data grant, like the option chain page they sit on.
/// </remarks>
[Authorize]
[RequireModule(PlatformModules.MarketData)]
[ApiController]
[Route("api/Straddle")]
public class StraddleController : ControllerBase
{
    private readonly StraddleService _straddles;
    private readonly TimeProvider _time;

    public StraddleController(StraddleService straddles, TimeProvider? time = null)
    {
        _straddles = straddles;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// One session's ATM straddle, a point per capture minute: spot, the ATM
    /// strike, each leg's price and how it was read (mid or last trade), the
    /// straddle and the ATM IV.
    /// </summary>
    /// <param name="date">The IST session day. Omitted: today, or the newest recorded day while today has nothing yet.</param>
    [HttpGet]
    public async Task<ActionResult<StraddleSeriesResponse>> GetSeries(
        [FromQuery] string underlying,
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        if (date is { } day && day > IstTime.DateOf(_time.GetUtcNow().UtcDateTime))
            return BadRequest(new { message = "date is in the future; nothing has been recorded for it." });

        return Ok(await _straddles.GetSeriesAsync(underlying, date, cancellationToken));
    }

    /// <summary>
    /// Every recorded session of the last year with its ATM IV near the open
    /// and the close, and the IV rank and percentile of the current value over
    /// the sessions before it, with how many there were.
    /// </summary>
    [HttpGet("iv-history")]
    public async Task<ActionResult<IvHistoryResponse>> GetIvHistory(
        [FromQuery] string underlying,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        return Ok(await _straddles.GetIvHistoryAsync(underlying, cancellationToken));
    }
}
