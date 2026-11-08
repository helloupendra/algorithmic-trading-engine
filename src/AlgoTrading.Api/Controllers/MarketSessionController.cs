using AlgoTrading.Application.Interfaces;
using AlgoTrading.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;



/// <summary>
/// Provides an endpoint to check the current market session status (open, closed, next open time) 
/// based on exchange and segment rules (e.g., NSE CM).
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MarketSessionController : ControllerBase
{
    private readonly IMarketSessionService _marketSessionService;

    public MarketSessionController(IMarketSessionService marketSessionService)
    {
        _marketSessionService = marketSessionService;
    }

    /// <summary>
    /// Whether an exchange segment is open now, why not (a weekend, a holiday) and when it next opens. Anonymous: the
    /// public homepage and the sign-in page show the Indian market's state before anyone has signed in. The answer
    /// is the exchange calendar, the same for every caller, with nothing about any user or account in it.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("check")]
    public IActionResult Check(
        [FromQuery] string exchange = "NSE",
        [FromQuery] string segment = "CM",
        [FromQuery] DateTime? utc = null)
    {
        var utcNow = utc ?? DateTime.UtcNow;

        var result = _marketSessionService.GetSessionInfo(
            utcNow,
            exchange,
            segment);

        return Ok(result);
    }
}
