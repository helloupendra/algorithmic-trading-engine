// src/AlgoTrading.Api/Controllers/PositionsController.cs
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Open positions across every run and manual book, on every underlying.
/// </summary>
/// <remarks>
/// Behind the strategies grant, like the runs they belong to. Ownership comes
/// from the token: a trader sees their own legs whatever userId they pass; an
/// admin sees every account's, or one account's with userId.
/// </remarks>
[RequireModule(PlatformModules.Strategies)]
[ApiController]
[Route("api/[controller]")]
public class PositionsController : ControllerBase
{
    private readonly OpenPositionsBuilder _positions;

    public PositionsController(OpenPositionsBuilder positions)
    {
        _positions = positions;
    }

    /// <summary>
    /// Every open leg of the live runs (Running or Stopping) and manual books
    /// in scope, each marked at its latest live quote (its stored mark when no
    /// quote is known), with the mark's age, the unrealized P&amp;L at it, the
    /// carry-forward tick, its own stop-loss and target, and its greeks where
    /// a source can price them.
    /// </summary>
    [HttpGet("open")]
    public async Task<ActionResult<OpenPositionsResponse>> GetOpen([FromQuery] long? userId, CancellationToken cancellationToken)
    {
        long? scopeUserId = User.ScopeUserId(userId);
        return Ok(await _positions.BuildAsync(scopeUserId, DateTime.UtcNow, cancellationToken));
    }
}
