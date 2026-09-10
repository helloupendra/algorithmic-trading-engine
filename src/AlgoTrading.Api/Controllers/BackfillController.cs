using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Contracts.MarketData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using AlgoTrading.Api.Security;


namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to manually trigger historical data backfill for a symbol over a specified date range.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class BackfillController : ControllerBase
{
    private readonly EnsureHistoryCoverageUseCase _ensureHistoryCoverageUseCase;
    private readonly IDailyCandleArchiveService _archive;

    public BackfillController(EnsureHistoryCoverageUseCase ensureHistoryCoverageUseCase, IDailyCandleArchiveService archive)
    {
        _ensureHistoryCoverageUseCase = ensureHistoryCoverageUseCase;
        _archive = archive;
    }

    /// <summary>
    /// Run the daily candle archive for one IST day now, instead of waiting
    /// for the nightly run: 1/5/15-minute candles from every symbol's live
    /// bars, plus the broker's candles for the index symbols.
    /// </summary>
    /// <param name="day">The IST trading day, yyyy-MM-dd. Defaults to today.</param>
    /// <param name="broker">Also ask the broker for the index candles (needs a live FYERS session).</param>
    [HttpPost("archive")]
    public async Task<IActionResult> ArchiveDay([FromQuery] string? day, [FromQuery] bool broker = true, CancellationToken cancellationToken = default)
    {
        DateOnly istDay;
        if (string.IsNullOrWhiteSpace(day))
            istDay = IstTime.DateOf(DateTime.UtcNow);
        else if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out istDay))
            return BadRequest(new { message = "day must be yyyy-MM-dd (IST)." });

        if (istDay > IstTime.DateOf(DateTime.UtcNow))
            return BadRequest(new { message = "That day has not happened yet." });

        var result = await _archive.ArchiveDayAsync(istDay, broker, cancellationToken);
        return Ok(result);
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

