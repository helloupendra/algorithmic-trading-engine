using AlgoTrading.Domain.Constants;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Market intelligence for the trader dashboard: news headlines from public
/// RSS feeds and day movers computed from public quote data. Everything served
/// here is informational market data — it is not investment advice, and the
/// movers are a mechanical sort by day change, not a recommendation.
/// </summary>
[RequireModule(PlatformModules.MarketData)]
[ApiController]
[Route("api/[controller]")]
public class MarketIntelController : ControllerBase
{
    private readonly IMarketIntelService _marketIntelService;

    public MarketIntelController(IMarketIntelService marketIntelService)
    {
        _marketIntelService = marketIntelService;
    }

    /// <summary>Headlines for a category: india, global or commodities.</summary>
    [HttpGet("news")]
    public async Task<IActionResult> GetNews(
        [FromQuery] string category = "india",
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _marketIntelService.GetNewsAsync(category, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Top day gainers/losers for an equity group (category).</summary>
    [HttpGet("movers")]
    public async Task<IActionResult> GetMovers(
        [FromQuery] string group = "NIFTY50_CONSTITUENTS",
        [FromQuery] int top = 10,
        CancellationToken cancellationToken = default)
    {
        if (top is < 1 or > 25)
        {
            return BadRequest(new { message = "top must be between 1 and 25." });
        }

        try
        {
            return Ok(await _marketIntelService.GetMoversAsync(group, top, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
