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

    /// <summary>
    /// Run the India VIX check now, instead of waiting for the one after the
    /// nightly archive: the last <paramref name="days"/> trading days through
    /// <paramref name="through"/>, every missing 1/5/15-minute bar asked of the
    /// history vendors. Only missing bars are written, so it is safe to repeat.
    /// It answers with what it found; it sends nothing to the System channel.
    /// </summary>
    /// <param name="through">The last IST trading day to check, yyyy-MM-dd. Defaults to the latest session that has closed.</param>
    /// <param name="days">Trading days to look back over, 1 to 60 (default 20).</param>
    [HttpPost("vix")]
    public async Task<IActionResult> CheckVix(
        [FromServices] VixBackfillService vix,
        [FromQuery] string? through,
        [FromQuery] int days = VixBackfillPlan.DefaultLookbackTradingDays,
        CancellationToken cancellationToken = default)
    {
        var (last, error) = VixCheckThrough(through, DateTime.UtcNow, vix);
        if (error is not null)
            return BadRequest(new { message = error });
        if (days is < 1 or > VixBackfillPlan.MaxLookbackTradingDays)
            return BadRequest(new { message = $"days must be 1 to {VixBackfillPlan.MaxLookbackTradingDays}." });

        var result = await vix.RunAsync(last, days, cancellationToken);
        return Ok(new
        {
            symbol = VixBackfillPlan.Symbol,
            through = result.Through,
            days = result.Days,
            barsFilled = result.BarsFilled,
            fetches = result.Fetches.Select(IncidentRedaction.Mask),
            gapDays = result.GapDays,
            holes = result.Holes.Select(h => h.ToString()),
            after = result.After.Select(c => new { day = c.Day, minutes = c.Minutes, expected = c.Expected, present = c.Present, state = c.State.ToString() }),
        });
    }

    /// <summary>
    /// The day the India VIX check runs through: <paramref name="through"/>, or
    /// the latest session that has closed; an error when it is not a date or its
    /// session has not closed yet. The close is the exchange calendar's, so a
    /// special session counts by its own hours: at 16:00 on a Muhurat Sunday the
    /// evening session has not happened, and checking it would report a gap and
    /// ask the vendors for a session still to come. Internal for tests.
    /// </summary>
    internal static (DateOnly Through, string? Error) VixCheckThrough(string? through, DateTime nowUtc, VixBackfillService vix)
    {
        DateOnly last;
        if (string.IsNullOrWhiteSpace(through))
        {
            last = IstTime.DateOf(nowUtc);
            for (int back = 0; back < 30 && !(vix.IsTradingDay(last) && vix.SessionClosed(last, nowUtc)); back++) last = last.AddDays(-1);
        }
        else if (!DateOnly.TryParseExact(through, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out last))
        {
            return (default, "through must be yyyy-MM-dd (IST).");
        }

        if (!vix.SessionClosed(last, nowUtc))
            return (default, "That session has not closed yet.");
        return (last, null);
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

