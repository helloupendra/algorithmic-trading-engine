using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AlgoTrading.Api.Hubs;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Exposes endpoints to query the active watchlist, fetch the latest cached market quotes, and view live ingestor health.
/// Also provides endpoints to view locally built real-time bars and ticks.
/// </summary>
/// <remarks>
/// The price and feed-health writes (<c>latest/upsert</c>, <c>heartbeat</c>,
/// <c>ticks/upsert</c>, <c>ticks/upsert-batch</c>) are for the feeds, which
/// sign in as the Service account, and for an admin. Until 28 Sep any signed-in
/// trader could post them: a price for any contract, stamped in 2030 so every
/// real tick after it read as older and was refused, and every account's paper
/// fills and risk marks priced from it — pushed to every browser following the
/// symbol as well. The reads stay open to every signed-in caller.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
public class LiveDataController : ControllerBase
{
    /// <summary>Who may write prices and feed health: the feeds' Service account, and an admin.</summary>
    public const string Writers = $"{UserRoles.Admin},{UserRoles.Service}";

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
    private readonly LiveTickDispatcher _liveFeed;

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
        LiveTickDispatcher liveFeed)
    {
        _getWatchlistUseCase = getWatchlistUseCase;
        _liveFeed = liveFeed;
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

    /// <summary>
    /// Recording-list rows the feed can no longer serve — expired contracts,
    /// and symbols silent all session while others tick — with the reason each.
    /// </summary>
    [HttpGet("watchlist/stale")]
    public Task<StaleWatchlistResponse> GetStaleWatchlist([FromServices] WatchlistPruneService pruner, CancellationToken cancellationToken)
        => pruner.FindAsync(cancellationToken);

    /// <summary>
    /// Removes the stale rows in one go (the same set GET watchlist/stale
    /// lists, or only the ids given) and tells the ingestor to resubscribe.
    /// </summary>
    [HttpPost("watchlist/prune")]
    public async Task<ActionResult<PruneWatchlistResponse>> PruneWatchlist(
        [FromServices] WatchlistPruneService pruner,
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PruneWatchlistRequest? request,
        CancellationToken cancellationToken)
        => Ok(await pruner.PruneAsync(request?.Ids, cancellationToken));

    /// <remarks>
    /// <c>replay=true</c> is a recap run asking: the market replay's price while one is on (nothing when the
    /// replay has none for the symbol). With no desk replay on, a recap trades a vendor's evening recap
    /// (TrueData), whose prices are the live table's, so it is answered from there as before.
    /// </remarks>
    [HttpGet("latest")]
    public async Task<IActionResult> GetLatest([FromQuery] string symbol, CancellationToken cancellationToken,
        [FromQuery] bool replay = false, [FromServices] IMarketReplayBook? replayBook = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        if (replay && replayBook?.Day is not null)
        {
            var replayed = replayBook.Quote(symbol);
            return replayed is null ? NotFound(new { message = "No replayed quote for symbol." }) : Ok(replayed);
        }

        var result = await _getLatestQuoteUseCase.ExecuteAsync(symbol, cancellationToken);

        if (result is null)
            return NotFound(new { message = "No live quote found for symbol." });

        return Ok(result);
    }

    [HttpGet("latest/all")]
    public async Task<IActionResult> GetAllLatest(CancellationToken cancellationToken,
        [FromQuery] bool replay = false, [FromServices] IMarketReplayBook? replayBook = null)
    {
        if (replay && replayBook?.Day is not null) return Ok(replayBook.AllQuotes());

        var result = await _getAllLatestQuotesUseCase.ExecuteAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("latest/upsert")]
    [Authorize(Roles = Writers)]
    public async Task<IActionResult> UpsertLatest(
        [FromBody] UpsertLiveQuoteRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "Symbol is required." });

        await _upsertLiveQuoteUseCase.ExecuteAsync(request, cancellationToken);
        return Ok(new { message = "Live quote upserted successfully." });
    }

    // A heartbeat also records the feed's pid, which the API later adopts and
    // may stop, and it sets the feed's gauges on /metrics.
    [HttpPost("heartbeat")]
    [Authorize(Roles = Writers)]
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
    [Authorize(Roles = Writers)]
    public async Task<IActionResult> UpsertTick(
            [FromBody] UpsertLiveTickRequest request,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "Symbol is required." });

        await _upsertLiveTickUseCase.ExecuteAsync(request, cancellationToken);

        // Queued, not sent. Awaited, every open browser tab sat in the ingest
        // path: a slow or stalled SignalR client applied back pressure straight
        // to the tick write, so watching the dashboard could slow down the feed
        // the strategies trade on. The dispatcher pushes it on its own clock,
        // to the connections that asked for this symbol.
        _liveFeed.Enqueue(request);

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
    [Authorize(Roles = Writers)]
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

        // Queued before the write, not after it. After the write, every open
        // chain waited for the database too: 60 ms at p50 on 2026-09-15, and
        // whatever an open-bell write burst costs on top. Queuing is a
        // dictionary write; the push happens on the dispatcher's clock and can
        // never apply back pressure to this request (the single-tick path
        // explains why that matters).
        _liveFeed.Enqueue(usable);

        if (usable.Count > 0)
        {
            await _upsertLiveTicksUseCase.ExecuteAsync(usable, cancellationToken);
        }

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

    /// <remarks>
    /// <c>untilUtc</c> bounds the bars to a moment: a recap run reads a past day as it stood at the
    /// replay's clock, the minute in progress built from the replayed ticks (<see cref="IMarketReplayBook"/>).
    /// </remarks>
    [HttpGet("bars")]
    public async Task<IActionResult> GetRecentBars(
        [FromQuery] string symbol,
        [FromQuery] string resolution = "1m",
        [FromQuery] int take = 100,
        CancellationToken cancellationToken = default,
        [FromQuery] DateTime? untilUtc = null,
        [FromServices] IMarketReplayBook? replayBook = null,
        [FromServices] ILiveDataService? liveData = null)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        if (take <= 0)
            return BadRequest(new { message = "take must be greater than 0." });

        if (untilUtc is DateTime until && liveData is not null)
        {
            var current = replayBook?.Day is null ? null : replayBook.CurrentMinute(symbol);
            return Ok(await liveData.GetBarsUntilAsync(symbol, resolution, take, until, current, cancellationToken));
        }

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
