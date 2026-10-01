using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.Today;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The owner's one page, Today: everything at a glance, nothing to answer (<see cref="TodayBuilder"/>).
/// </summary>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class TodayController(TodayBuilder today) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) => Ok(await today.BuildAsync(cancellationToken));

    /// <summary>Records a decision in the log the page shows (newest first; the same date and title replaces it).</summary>
    [HttpPost("decisions")]
    public async Task<IActionResult> Record([FromBody] TodayDecision? decision, CancellationToken cancellationToken)
    {
        if (decision is null || string.IsNullOrWhiteSpace(decision.Title) || string.IsNullOrWhiteSpace(decision.Decided))
        {
            return BadRequest(new { error = "Send date, title, decided, by and status." });
        }

        if (!DateOnly.TryParseExact(decision.Date, "yyyy-MM-dd", out _)) return BadRequest(new { error = "date is yyyy-MM-dd." });
        if (decision.Status is not ("decided" or "default" or "open")) return BadRequest(new { error = "status is decided, default or open." });
        if (decision.Title.Length > 200 || decision.Decided.Length > 1000) return BadRequest(new { error = "title is at most 200 characters, decided 1000." });

        string by = User.GetUserName() ?? User.Identity?.Name ?? "admin";
        return Ok(await today.RecordAsync(decision with { Title = decision.Title.Trim(), Decided = decision.Decided.Trim() }, by, cancellationToken));
    }
}
