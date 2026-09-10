using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The market at a glance — indices, large caps, commodities — for every
/// signed-in user. Read-only; the universe is the platform's.
/// </summary>
[Authorize]
[ApiController]
[Route("api/MarketPulse")]
public class MarketPulseController : ControllerBase
{
    private readonly IMarketPulseService _pulse;

    public MarketPulseController(IMarketPulseService pulse)
    {
        _pulse = pulse;
    }

    [HttpGet]
    public async Task<ActionResult<MarketPulseResponse>> Get(CancellationToken cancellationToken)
        => Ok(await _pulse.GetAsync(cancellationToken));
}
