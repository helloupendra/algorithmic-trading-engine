using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private readonly TradingDbContext _dbContext;

    public MarketDataController(
        SyncHistoryUseCase syncHistoryUseCase,
        GetStoredCandlesUseCase getStoredCandlesUseCase,
        TradingDbContext dbContext)
    {
        _syncHistoryUseCase = syncHistoryUseCase;
        _getStoredCandlesUseCase = getStoredCandlesUseCase;
        _dbContext = dbContext;
    }

    /// <summary>
    /// The data inventory: every (symbol, resolution) this installation has
    /// candles for, with the exact date range and bar count — so a user can see
    /// at a glance what is chartable before picking anything.
    /// Sources: "backfill" = the candles table (FYERS history sync),
    /// "live" = 1-minute bars aggregated during live ingestion sessions.
    /// </summary>
    [HttpGet("coverage")]
    public async Task<IActionResult> GetCoverage(CancellationToken cancellationToken)
    {
        var backfill = await _dbContext.Candles
            .AsNoTracking()
            .GroupBy(c => new { c.Symbol, c.Resolution })
            .Select(g => new
            {
                symbol = g.Key.Symbol,
                resolution = g.Key.Resolution,
                fromUtc = g.Min(x => x.TimeStampUtc),
                toUtc = g.Max(x => x.TimeStampUtc),
                barCount = g.Count(),
                source = "backfill",
            })
            .ToListAsync(cancellationToken);

        var live = await _dbContext.LiveBars
            .AsNoTracking()
            .GroupBy(b => new { b.Symbol, b.Resolution })
            .Select(g => new
            {
                symbol = g.Key.Symbol,
                resolution = g.Key.Resolution,
                fromUtc = g.Min(x => x.BarStartUtc),
                toUtc = g.Max(x => x.BarStartUtc),
                barCount = g.Count(),
                source = "live",
            })
            .ToListAsync(cancellationToken);

        var rows = backfill.Concat(live)
            .OrderBy(r => r.symbol)
            .ThenBy(r => r.source)
            .ThenBy(r => r.resolution)
            .ToList();

        return Ok(rows);
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
