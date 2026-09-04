// src/AlgoTrading.Api/Controllers/RiskController.cs
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The global trading halt. Admin-only in its entirety: activating it flattens
/// every open position across every user.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
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

    /// <param name="reason">
    /// Optional operator note recorded alongside the halt, shown on the admin panel
    /// so whoever considers lifting it can see why it was raised.
    /// </param>
    [HttpPost("killswitch/activate")]
    public async Task<IActionResult> ActivateKillSwitch(
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        await _riskManagementService.ActivateKillSwitchAsync(
            User.GetUserName(), reason, cancellationToken);

        // Flatten all open positions across all strategies
        await _paperTradingService.FlattenAllPositionsAsync(cancellationToken);

        return Ok(new { message = "GLOBAL KILL SWITCH ACTIVATED. ALL STRATEGIES PAUSED. ALL POSITIONS FLATTENED." });
    }

    [HttpPost("killswitch/deactivate")]
    public async Task<IActionResult> DeactivateKillSwitch(
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        await _riskManagementService.DeactivateKillSwitchAsync(
            User.GetUserName(), reason, cancellationToken);

        return Ok(new { message = "GLOBAL KILL SWITCH DEACTIVATED. TRADING RESUMED." });
    }

    /// <summary>
    /// Current halt state plus who set it, when and why.
    /// </summary>
    [HttpGet("killswitch/status")]
    public async Task<IActionResult> GetKillSwitchStatus(CancellationToken cancellationToken)
    {
        var state = await _riskManagementService.GetKillSwitchStateAsync(cancellationToken);
        return Ok(state);
    }

    [HttpGet("limits")]
    public IActionResult GetLimits([FromServices] IRiskLimitsStore limitsStore)
    {
        var limits = limitsStore.GetLimits();
        return Ok(limits);
    }

    [HttpPost("limits")]
    public async Task<IActionResult> UpdateLimits(
        [FromBody] AlgoTrading.Contracts.Risk.RiskLimitsDto limits,
        [FromServices] IRiskLimitsStore limitsStore,
        CancellationToken cancellationToken)
    {
        await limitsStore.UpdateLimitsAsync(limits, User.GetUserName() ?? "system", cancellationToken);
        return Ok(limitsStore.GetLimits());
    }

    [HttpGet("events")]
    public async Task<IActionResult> GetRiskEvents(
        [FromServices] AlgoTrading.Infrastructure.Persistence.TradingDbContext dbContext,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var events = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            dbContext.RiskEvents
                .OrderByDescending(x => x.OccurredUtc)
                .Take(limit),
            cancellationToken);

        return Ok(events);
    }
}
