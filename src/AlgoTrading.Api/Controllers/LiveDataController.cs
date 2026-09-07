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
    private readonly UpsertLiveTicksUseCase _upsertLiveTicksUseCase;
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
        UpsertLiveTicksUseCase upsertLiveTicksUseCase,
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
        _upsertLiveTicksUseCase = upsertLiveTicksUseCase;
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

        // Not awaited, and not the whole request.
        //
        // Awaited, every open browser tab sat in the ingest path: a slow or
        // stalled SignalR client applied back pressure straight to the tick
        // write, so watching the dashboard could slow down the feed the
        // strategies trade on. The broadcast is a convenience for a screen —
        // it must never be able to delay storing a price.
        //
        // Only the fields a screen renders are sent. `RawPayload` is the
        // broker's complete message and has no business on a websocket.
        _ = BroadcastTickAsync(request);

        return Ok(new { message = "Live tick appended successfully." });
    }

    /// <summary>The most ticks one batch may carry.</summary>
    /// <remarks>
    /// Sized so a flush at the ingestor's interval fits comfortably even during
    /// an open-bell burst, while still bounding the work one request can ask for.
    /// </remarks>
    private const int MaxTickBatch = 500;

    /// <summary>
    /// Appends many ticks in one request.
    /// </summary>
    /// <remarks>
    /// The single-tick endpoint above costs a full HTTP request, model binding,
    /// DI scope and auth pass per price. At the ~39 ticks/second this feed
    /// actually produces across its symbols, the ingestor's five poster threads
    /// were finishing roughly 38 of those a second — level with arrivals, so the
    /// queue drifted instead of draining. Measured on 2026-09-07, a tick took
    /// 0.9s to land at 13:35 IST and 60s by 13:40, and once the ingestor's
    /// bounded queue saturates it drops the newest prices outright.
    ///
    /// Batching removes that overhead from the per-tick path: the database work
    /// is unchanged, but it is no longer paid for one HTTP round-trip at a time.
    ///
    /// Each tick is stored independently — one bad row is reported, not fatal —
    /// because losing a whole flush over a single malformed symbol would be a
    /// worse failure than the one it guards against.
    /// </remarks>
    [HttpPost("ticks/upsert-batch")]
    public async Task<IActionResult> UpsertTickBatch(
        [FromBody] List<UpsertLiveTickRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests is null || requests.Count == 0)
            return BadRequest(new { message = "At least one tick is required." });

        if (requests.Count > MaxTickBatch)
            return BadRequest(new { message = $"At most {MaxTickBatch} ticks per batch; got {requests.Count}." });

        var usable = requests.Where(r => !string.IsNullOrWhiteSpace(r.Symbol)).ToList();
        int skipped = requests.Count - usable.Count;

        if (usable.Count > 0)
        {
            await _upsertLiveTicksUseCase.ExecuteAsync(usable, cancellationToken);
        }

        // Same reasoning as the single-tick path: a screen must never be able to
        // apply back pressure to the feed the strategies trade on.
        _ = BroadcastTickBatchAsync(usable);

        return Ok(new { stored = usable.Count, skipped });
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


    /// <summary>
    /// Pushes a trimmed tick to the console, and never lets that failing matter.
    /// </summary>
    private async Task BroadcastTickAsync(UpsertLiveTickRequest request)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("ReceiveTick", new
            {
                symbol = request.Symbol,
                lastTradedPrice = request.LastTradedPrice,
                bidPrice = request.BidPrice,
                askPrice = request.AskPrice,
                volume = request.Volume,
                openInterest = request.OpenInterest,
                impliedVolatility = request.ImpliedVolatility,
                exchangeTimestampUtc = request.ExchangeTimestampUtc,
            });
        }
        catch
        {
            // Deliberately swallowed. The tick is already stored; a browser
            // that could not be reached is not a data problem, and letting it
            // surface here would only add noise to every session.
        }
    }

    /// <summary>
    /// Pushes a whole flush to the console as one message.
    /// </summary>
    /// <remarks>
    /// One send per batch rather than one per tick: the console applies each
    /// price into the same cache either way, and a batch of forty individual
    /// SignalR frames costs forty round-trips to every open tab for no extra
    /// information. Sent under "ReceiveTicks" — plural — so an older client
    /// that only knows "ReceiveTick" keeps working off its poll instead of
    /// mis-reading an array as a single quote.
    /// </remarks>
    private async Task BroadcastTickBatchAsync(IReadOnlyList<UpsertLiveTickRequest> requests)
    {
        try
        {
            var payload = requests
                .Where(r => !string.IsNullOrWhiteSpace(r.Symbol))
                .Select(r => new
                {
                    symbol = r.Symbol,
                    lastTradedPrice = r.LastTradedPrice,
                    bidPrice = r.BidPrice,
                    askPrice = r.AskPrice,
                    volume = r.Volume,
                    openInterest = r.OpenInterest,
                    impliedVolatility = r.ImpliedVolatility,
                    exchangeTimestampUtc = r.ExchangeTimestampUtc,
                })
                .ToList();

            if (payload.Count == 0)
                return;

            await _hubContext.Clients.All.SendAsync("ReceiveTicks", payload);
        }
        catch
        {
            // As above: an unreachable browser is not a data problem.
        }
    }
}
