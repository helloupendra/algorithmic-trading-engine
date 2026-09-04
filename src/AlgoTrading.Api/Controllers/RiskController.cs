// src/AlgoTrading.Api/Controllers/RiskController.cs
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Contracts.Strategies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> ActivateKillSwitch(
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        await _riskManagementService.ActivateKillSwitchAsync(
            User.GetUserName(), reason, cancellationToken);

        await _paperTradingService.FlattenAllPositionsAsync(cancellationToken);

        return Ok(new { message = "GLOBAL KILL SWITCH ACTIVATED. ALL STRATEGIES PAUSED. ALL POSITIONS FLATTENED." });
    }

    [HttpPost("killswitch/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> DeactivateKillSwitch(
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        await _riskManagementService.DeactivateKillSwitchAsync(
            User.GetUserName(), reason, cancellationToken);

        return Ok(new { message = "GLOBAL KILL SWITCH DEACTIVATED. TRADING RESUMED." });
    }

    [HttpGet("killswitch/status")]
    [Authorize]
    public async Task<IActionResult> GetKillSwitchStatusOld(CancellationToken cancellationToken)
    {
        var state = await _riskManagementService.GetKillSwitchStateAsync(cancellationToken);
        return Ok(state);
    }

    [HttpGet("status")]
    [Authorize]
    public async Task<IActionResult> GetKillSwitchStatus(CancellationToken cancellationToken)
    {
        var state = await _riskManagementService.GetKillSwitchStateAsync(cancellationToken);
        return Ok(state);
    }

    [HttpGet("limits")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult GetLimits([FromServices] IRiskLimitsStore limitsStore)
    {
        var limits = limitsStore.GetLimits();
        return Ok(limits);
    }

    [HttpPost("limits")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> UpdateLimits(
        [FromBody] RiskLimitsDto limits,
        [FromServices] IRiskLimitsStore limitsStore,
        CancellationToken cancellationToken)
    {
        await limitsStore.UpdateLimitsAsync(limits, User.GetUserName() ?? "system", cancellationToken);
        return Ok(limitsStore.GetLimits());
    }

    [HttpGet("exposure")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetExposure(
        [FromServices] AlgoTrading.Api.Services.StrategyProcessRegistry registry,
        [FromServices] AlgoTrading.Api.Services.LiveRunHistoryBuilder historyBuilder,
        CancellationToken cancellationToken)
    {
        var activeProcesses = registry.List();
        
        var response = new RiskExposureResponse
        {
            ActiveRunsCount = activeProcesses.Count
        };

        if (activeProcesses.Count > 0)
        {
            // We use LiveRunHistoryBuilder to get the PnL calculations for active runs
            var allRuns = await historyBuilder.ListAsync(
                new AlgoTrading.Api.Services.LiveRunHistoryFilter(
                    null, null, null, AlgoTrading.Api.Services.StrategyRunControl.RunStatusRunning, null, null, 1000, 0),
                cancellationToken);

            var pnlMap = allRuns.ToDictionary(r => r.RunId);

            foreach (var process in activeProcesses)
            {
                pnlMap.TryGetValue(process.RunId, out var summary);

                response.ActiveRuns.Add(new ActiveRunExposure
                {
                    RunId = process.RunId,
                    StrategyName = process.Name,
                    Underlying = process.Underlying,
                    RiskRules = process.Risk,
                    UnrealizedPnL = summary?.UnrealizedPnl ?? 0m,
                    RealizedPnL = summary?.RealizedPnl ?? 0m
                });

                response.TotalUnrealizedPnL += summary?.UnrealizedPnl ?? 0m;
                response.TotalRealizedPnL += summary?.RealizedPnl ?? 0m;
            }
        }

        return Ok(response);
    }

    [HttpGet("events")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetRiskEvents(
        [FromServices] AlgoTrading.Infrastructure.Persistence.TradingDbContext dbContext,
        [FromQuery] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        var events = await dbContext.RiskEvents
            .AsNoTracking()
            .OrderByDescending(x => x.OccurredUtc)
            .Take(limit)
            .Select(x => new RiskEventDto
            {
                Id = x.Id,
                OccurredUtc = x.OccurredUtc,
                Kind = x.Kind,
                ActorUserId = x.ActorUserId,
                ActorName = x.ActorName,
                Reason = x.Reason,
                DetailsJson = x.DetailsJson,
                SimulationRunId = x.SimulationRunId,
                Symbol = x.Symbol
            })
            .ToListAsync(cancellationToken);

        return Ok(events);
    }
}
