using AlgoTrading.Application.Interfaces;
using AlgoTrading.Api.Controllers;
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
