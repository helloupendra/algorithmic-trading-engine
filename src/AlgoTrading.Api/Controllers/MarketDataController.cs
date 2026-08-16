using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Contracts.MarketData;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to sync historical OHLCV data directly from the broker into the local database, 
/// and to query the locally cached history.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MarketDataController : ControllerBase
{
    private readonly SyncHistoryUseCase _syncHistoryUseCase;
    private readonly GetStoredCandlesUseCase _getStoredCandlesUseCase;

    public MarketDataController(
        SyncHistoryUseCase syncHistoryUseCase,
        GetStoredCandlesUseCase getStoredCandlesUseCase)
    {
        _syncHistoryUseCase = syncHistoryUseCase;
        _getStoredCandlesUseCase = getStoredCandlesUseCase;

    }

    [HttpPost("history/sync")]
    public async Task<IActionResult> SyncHistory(
        [FromBody] SyncHistoryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            return BadRequest(new { message = "Symbol is required." });
        }

        var result = await _syncHistoryUseCase.ExecuteAsync(request, cancellationToken);
        return Ok(result);
    }

    [HttpGet("history/local")]
    public async Task<IActionResult> GetStoredHistory(
        [FromQuery] GetStoredCandlesRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            return BadRequest(new { message = "Symbol is required" });
        }

        var result = await _getStoredCandlesUseCase.ExecuteAsync(request, cancellationToken);
        return Ok(result);
    }
}
