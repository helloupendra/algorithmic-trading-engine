using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using AlgoTrading.Api.Hubs;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to query the active watchlist, fetch the latest cached market quotes, and view live ingestor health.
/// Also provides endpoints to view locally built real-time bars and ticks.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class LiveDataController : ControllerBase
{
    private readonly GetWatchlistUseCase _getWatchlistUseCase;
    private readonly UpsertWatchlistItemUseCase _upsertWatchlistItemUseCase;
    private readonly RemoveWatchlistItemUseCase _removeWatchlistItemUseCase;
    private readonly GetLatestQuoteUseCase _getLatestQuoteUseCase;
    private readonly GetAllLatestQuotesUseCase _getAllLatestQuotesUseCase;
    private readonly UpsertLiveQuoteUseCase _upsertLiveQuoteUseCase;

    private readonly UpsertHeartbeatUseCase _upsertHeartbeatUseCase;
    private readonly GetIngestorStatusUseCase _getIngestorStatusUseCase;
    private readonly GetAllIngestorStatusesUseCase _getAllIngestorStatusesUseCase;
    private readonly GetStaleQuotesUseCase _getStaleQuotesUseCase;


    private readonly UpsertLiveTickUseCase _upsertLiveTickUseCase;
    private readonly GetRecentTicksUseCase _getRecentTicksUseCase;
    private readonly GetRecentBarsUseCase _getRecentBarsUseCase;
    private readonly IHubContext<LiveFeedHub> _hubContext;

    public LiveDataController(
        GetWatchlistUseCase getWatchlistUseCase,
        UpsertWatchlistItemUseCase upsertWatchlistItemUseCase,
        RemoveWatchlistItemUseCase removeWatchlistItemUseCase,
        GetLatestQuoteUseCase getLatestQuoteUseCase,
        GetAllLatestQuotesUseCase getAllLatestQuotesUseCase,
        UpsertLiveQuoteUseCase upsertLiveQuoteUseCase,
        UpsertHeartbeatUseCase upsertHeartbeatUseCase,
        GetIngestorStatusUseCase getIngestorStatusUseCase,
        GetAllIngestorStatusesUseCase getAllIngestorStatusesUseCase,
        GetStaleQuotesUseCase getStaleQuotesUseCase,
        UpsertLiveTickUseCase upsertLiveTickUseCase,
        GetRecentTicksUseCase getRecentTicksUseCase,
        GetRecentBarsUseCase getRecentBarsUseCase,
        IHubContext<LiveFeedHub> hubContext)
    {
        _getWatchlistUseCase = getWatchlistUseCase;
        _hubContext = hubContext;
        _upsertWatchlistItemUseCase = upsertWatchlistItemUseCase;
        _removeWatchlistItemUseCase = removeWatchlistItemUseCase;
        _getLatestQuoteUseCase = getLatestQuoteUseCase;
        _getAllLatestQuotesUseCase = getAllLatestQuotesUseCase;
        _upsertLiveQuoteUseCase = upsertLiveQuoteUseCase;

        _upsertHeartbeatUseCase = upsertHeartbeatUseCase;
        _getIngestorStatusUseCase = getIngestorStatusUseCase;
        _getAllIngestorStatusesUseCase = getAllIngestorStatusesUseCase;
        _getStaleQuotesUseCase = getStaleQuotesUseCase;

        _upsertLiveTickUseCase = upsertLiveTickUseCase;
        _getRecentTicksUseCase = getRecentTicksUseCase;
        _getRecentBarsUseCase = getRecentBarsUseCase;
    }

    [HttpGet("watchlist")]
    public async Task<IActionResult> GetWatchlist(CancellationToken cancellationToken)
    {
        var result = await _getWatchlistUseCase.ExecuteAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("watchlist")]
    public async Task<IActionResult> UpsertWatchlist(
        [FromBody] UpsertWatchlistItemRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "Symbol is required." });

        if (request.DataType != "lite" && request.DataType != "symbolUpdate")
            return BadRequest(new { message = "DataType must be 'lite' or 'symbolUpdate'." });

        var result = await _upsertWatchlistItemUseCase.ExecuteAsync(request, cancellationToken);
        return Ok(result);
    }

    [HttpDelete("watchlist/{id:long}")]
    public async Task<IActionResult> RemoveWatchlist(long id, CancellationToken cancellationToken)
    {
        await _removeWatchlistItemUseCase.ExecuteAsync(id, cancellationToken);
        return Ok(new { message = "Watchlist item removed successfully." });
    }

    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest([FromQuery] string symbol, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        var result = await _getLatestQuoteUseCase.ExecuteAsync(symbol, cancellationToken);

        if (result is null)
            return NotFound(new { message = "No live quote found for symbol." });

        return Ok(result);
    }

    [HttpGet("latest/all")]
    public async Task<IActionResult> GetAllLatest(CancellationToken cancellationToken)
    {
        var result = await _getAllLatestQuotesUseCase.ExecuteAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("latest/upsert")]
    public async Task<IActionResult> UpsertLatest(
        [FromBody] UpsertLiveQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "Symbol is required." });

        await _upsertLiveQuoteUseCase.ExecuteAsync(request, cancellationToken);
        return Ok(new { message = "Live quote upserted successfully." });
    }

    // NEW
    [HttpPost("heartbeat")]
    public async Task<IActionResult> UpsertHeartbeat(
        [FromBody] UpsertHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.SourceName))
            return BadRequest(new { message = "SourceName is required." });

        await _upsertHeartbeatUseCase.ExecuteAsync(request, cancellationToken);
        return Ok(new { message = "Heartbeat updated successfully." });
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromQuery] string sourceName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return BadRequest(new { message = "sourceName is required." });

        var result = await _getIngestorStatusUseCase.ExecuteAsync(sourceName, cancellationToken);

        if (result is null)
            return NotFound(new { message = "No ingestor status found for source." });

        return Ok(result);
    }

    [HttpGet("status/all")]
    public async Task<IActionResult> GetAllStatuses(CancellationToken cancellationToken)
    {
        var result = await _getAllIngestorStatusesUseCase.ExecuteAsync(cancellationToken);
        return Ok(result);
    }

    [HttpGet("stale")]
    public async Task<IActionResult> GetStaleQuotes(
        [FromQuery] int staleAfterSeconds = 60,
        CancellationToken cancellationToken = default)
    {
        if (staleAfterSeconds <= 0)
            return BadRequest(new { message = "staleAfterSeconds must be greater than 0." });

        var result = await _getStaleQuotesUseCase.ExecuteAsync(staleAfterSeconds, cancellationToken);
        return Ok(result);
    }


    [HttpPost("ticks/upsert")]
    public async Task<IActionResult> UpsertTick(
            [FromBody] UpsertLiveTickRequest request,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "Symbol is required." });

        await _upsertLiveTickUseCase.ExecuteAsync(request, cancellationToken);
        await _hubContext.Clients.All.SendAsync("ReceiveTick", request, cancellationToken);
        return Ok(new { message = "Live tick appended successfully." });
    }

    [HttpGet("ticks")]
    public async Task<IActionResult> GetRecentTicks(
        [FromQuery] string symbol,
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        if (take <= 0)
            return BadRequest(new { message = "take must be greater than 0." });

        var result = await _getRecentTicksUseCase.ExecuteAsync(symbol, take, cancellationToken);
        return Ok(result);
    }

    [HttpGet("bars")]
    public async Task<IActionResult> GetRecentBars(
        [FromQuery] string symbol,
        [FromQuery] string resolution = "1m",
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        if (take <= 0)
            return BadRequest(new { message = "take must be greater than 0." });

        var result = await _getRecentBarsUseCase.ExecuteAsync(symbol, resolution, take, cancellationToken);
        return Ok(result);
    }

    [HttpGet("ticks/history")]
    public async Task<IActionResult> GetHistoricalTicks(
    [FromQuery] string symbol,
    [FromQuery] DateTime fromUtc,
    [FromQuery] DateTime toUtc,
    [FromServices] IMarketTickArchiveService marketTickArchiveService,
    [FromQuery] int take = 10000,
    CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        if (fromUtc >= toUtc)
            return BadRequest(new { message = "fromUtc must be earlier than toUtc." });

        try
        {
            var result = await marketTickArchiveService.GetRangeAsync(
                symbol,
                fromUtc,
                toUtc,
                take,
                cancellationToken);

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

}
