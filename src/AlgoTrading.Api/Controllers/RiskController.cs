using AlgoTrading.Api.Services;
// src/AlgoTrading.Api/Controllers/RiskController.cs
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
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

    private readonly ISystemNotifier _notifier;
    private readonly IDeskEventPublisher? _deskEvents;

    public RiskController(
        IRiskManagementService riskManagementService,
        IPaperTradingService paperTradingService,
        ISystemNotifier notifier,
        IDeskEventPublisher? deskEvents = null)
    {
        _riskManagementService = riskManagementService;
        _paperTradingService = paperTradingService;
        _notifier = notifier;
        _deskEvents = deskEvents;
    }

    /// <summary>
    /// The kill switch changed: every admin's console is told. It is no one
    /// run's, so no one owner's; each run it flattened tells its owner itself
    /// (PaperTradingService.FlattenAllPositionsAsync).
    /// </summary>
    private void PublishKillSwitch(string detail)
        => _deskEvents.TryPublish(new DeskEvent(DeskEventKinds.Risk, RunId: null, UserId: null, Symbol: null, DateTime.UtcNow, detail));

    [HttpPost("killswitch/activate")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> ActivateKillSwitch(
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        await _riskManagementService.ActivateKillSwitchAsync(
            User.GetUserName(), reason, cancellationToken);

        await _paperTradingService.FlattenAllPositionsAsync(cancellationToken);
        PublishKillSwitch($"Kill switch activated by {User.GetUserName() ?? "admin"}: every position flattened");

        HttpContext.Describe(
            "Pulled the kill switch — every strategy paused and all positions flattened"
                + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"),
            "killswitch");

        await _notifier.NotifyAsync(
            NotificationCategory.Risk,
            NotificationSeverity.Error,
            "Kill switch ACTIVATED",
            $"All strategies paused and every open position flattened. By {User.GetUserName() ?? "admin"}"
                + (string.IsNullOrWhiteSpace(reason) ? "." : $" — {reason}."),
            cancellationToken: cancellationToken);

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
        PublishKillSwitch($"Kill switch released by {User.GetUserName() ?? "admin"}: trading resumed");

        HttpContext.Describe(
            "Released the kill switch — trading resumed"
                + (string.IsNullOrWhiteSpace(reason) ? "." : $": {reason}"),
            "killswitch");

        await _notifier.NotifyAsync(
            NotificationCategory.Risk,
            NotificationSeverity.Warning,
            "Kill switch released",
            $"Trading resumed. By {User.GetUserName() ?? "admin"}"
                + (string.IsNullOrWhiteSpace(reason) ? "." : $" — {reason}."),
            cancellationToken: cancellationToken);

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

    /// <summary>
    /// The live runs and their P&amp;L: every account's for an admin, the
    /// caller's own for a trader.
    /// </summary>
    /// <remarks>
    /// Behind the strategies grant, like the runs, positions and orders it
    /// sums, and scoped the same way (<see cref="ClaimsPrincipalExtensions.ScopeUserId"/>).
    /// It was admin-only, so the trader Library's warning about runs already
    /// live, which reads this, met a 403 and never showed for a trader.
    /// </remarks>
    [HttpGet("exposure")]
    [RequireModule(PlatformModules.Strategies)]
    public async Task<IActionResult> GetExposure(
        [FromServices] AlgoTrading.Api.Services.StrategyProcessRegistry registry,
        [FromServices] AlgoTrading.Api.Services.LiveRunHistoryBuilder historyBuilder,
        CancellationToken cancellationToken)
    {
        long? scopeUserId = User.ScopeUserId(null);
        var activeProcesses = registry.List()
            .Where(run => scopeUserId is null || run.UserId == scopeUserId)
            .ToList();

        var response = new RiskExposureResponse
        {
            ActiveRunsCount = activeProcesses.Count
        };

        if (activeProcesses.Count > 0)
        {
            // We use LiveRunHistoryBuilder to get the PnL calculations for active runs
            var allRuns = await historyBuilder.ListAsync(
                new AlgoTrading.Api.Services.LiveRunHistoryFilter(
                    scopeUserId, null, null, AlgoTrading.Api.Services.StrategyRunControl.RunStatusRunning, null, null, 1000, 0),
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
