// src/AlgoTrading.Api/Controllers/DeskController.cs
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Desk;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// What the Desk reads that belongs to no other module.
/// </summary>
/// <remarks>
/// Admin-only: the morning plan names every account it deploys into, and it
/// is the operator's, not a trader's.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class DeskController : ControllerBase
{
    private readonly DeskPlanBuilder _plan;

    public DeskController(DeskPlanBuilder plan)
    {
        _plan = plan;
    }

    /// <summary>
    /// The morning plan (config/morning-plan.txt, or Desk:PlanFile), read as
    /// scripts/market-open.sh deploys it: its accounts, its lines, and every run
    /// it asks for with whether that run is live now and its run id. 404 when
    /// there is no plan file, naming where it was looked for.
    /// </summary>
    [HttpGet("plan")]
    public async Task<ActionResult<DeskPlanResponse>> GetPlan(CancellationToken cancellationToken)
    {
        var plan = await _plan.BuildAsync(cancellationToken);
        if (plan is null)
        {
            // Not "an empty plan": the morning job refuses to start anything
            // without the file, and the Desk must say that, not show no runs.
            var searched = _plan.Locate().Searched;
            return NotFound(new { message = "No morning plan file was found.", searched });
        }

        return Ok(plan);
    }
}
