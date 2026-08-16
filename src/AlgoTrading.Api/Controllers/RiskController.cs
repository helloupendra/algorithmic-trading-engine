// src/AlgoTrading.Api/Controllers/RiskController.cs
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RiskController : ControllerBase
{
    private readonly IRiskManagementService _riskManagementService;
    private readonly IPaperTradingService _paperTradingService;

    public RiskController(IRiskManagementService riskManagementService, IPaperTradingService paperTradingService)
    {
        _riskManagementService = riskManagementService;
        _paperTradingService = paperTradingService;
    }

    [HttpPost("killswitch/activate")]
    public async Task<IActionResult> ActivateKillSwitch(CancellationToken cancellationToken)
    {
        await _riskManagementService.ActivateKillSwitchAsync(cancellationToken);
        
        // Flatten all open positions across all strategies
        await _paperTradingService.FlattenAllPositionsAsync(cancellationToken);

        return Ok(new { message = "GLOBAL KILL SWITCH ACTIVATED. ALL STRATEGIES PAUSED. ALL POSITIONS FLATTENED." });
    }

    [HttpPost("killswitch/deactivate")]
    public async Task<IActionResult> DeactivateKillSwitch(CancellationToken cancellationToken)
    {
        await _riskManagementService.DeactivateKillSwitchAsync(cancellationToken);
        
        return Ok(new { message = "GLOBAL KILL SWITCH DEACTIVATED. TRADING RESUMED." });
    }

    [HttpGet("killswitch/status")]
    public async Task<IActionResult> GetKillSwitchStatus(CancellationToken cancellationToken)
    {
        bool isActive = await _riskManagementService.IsKillSwitchActiveAsync(cancellationToken);
        return Ok(new { IsActive = isActive });
    }
}
