using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Contracts.MarketData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AlgoTrading.Api.Security;


namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to manually trigger historical data backfill for a symbol over a specified date range.
/// </summary>
// [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class BackfillController : ControllerBase
{
    private readonly EnsureHistoryCoverageUseCase _ensureHistoryCoverageUseCase;

    public BackfillController(EnsureHistoryCoverageUseCase ensureHistoryCoverageUseCase)
    {
        _ensureHistoryCoverageUseCase = ensureHistoryCoverageUseCase;
    }

    [HttpPost("history")]
    public async Task<IActionResult> BackfillHistory(
        [FromBody] BackfillHistoryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            return BadRequest(new { message = "Symbol is required" });

        }

        if (request.FromDate > request.ToDate)
        {
            return BadRequest(new { message = "FromDate cannot be greater than ToDate." });
        }

        var result = await _ensureHistoryCoverageUseCase.ExecuteAsync(request, cancellationToken);
        return Ok(result);
    }
}

