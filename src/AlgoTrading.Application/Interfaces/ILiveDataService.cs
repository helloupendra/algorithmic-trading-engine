using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Service interface for managing live market data feeds, watchlists, quotes, and ingestor health.
/// </summary>
public interface ILiveDataService
{
    /// <summary>
    /// Retrieves the current list of symbols actively subscribed to the live feed.
    /// </summary>
    Task<IReadOnlyList<LiveWatchlistItem>> GetWatchlistAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates a symbol in the live data watchlist.
    /// </summary>
    Task<LiveWatchlistItem> UpsertWatchlistItemAsync(
        UpsertWatchlistItemRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a symbol from the live data watchlist.
    /// </summary>
    Task RemoveWatchlistItemAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent price quote for a specific symbol.
    /// </summary>
    Task<LiveQuoteResponse?> GetLatestQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the latest price quotes for all actively tracked symbols.
    /// </summary>
    Task<IReadOnlyList<LiveQuoteResponse>> GetAllLatestQuotesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the latest quote snapshot in the database for a symbol.
    /// </summary>
    Task UpsertLatestQuoteAsync(
        UpsertLiveQuoteRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a heartbeat ping from a background ingestor worker.
    /// </summary>
    Task UpsertHeartbeatAsync(
        UpsertHeartbeatRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the health status of a specific ingestor worker.
    /// </summary>
    Task<IngestorStatusResponse?> GetIngestorStatusAsync(
        string sourceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the health status of all registered ingestor workers.
    /// </summary>
    Task<IReadOnlyList<IngestorStatusResponse>> GetAllIngestorStatusesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Identifies symbols that have not received price updates within the specified timeframe.
    /// </summary>
    Task<IReadOnlyList<StaleQuoteResponse>> GetStaleQuotesAsync(
        int staleAfterSeconds,
        CancellationToken cancellationToken = default);

    // ✅ NEW METHODS FOR TICKS + BARS

    /// <summary>
    /// Appends a new raw tick to the historical tick storage.
    /// </summary>
    Task AppendLiveTickAsync(
        UpsertLiveTickRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the most recent ticks for a symbol, used to build real-time views.
    /// </summary>
    Task<IReadOnlyList<LiveTickResponse>> GetRecentTicksAsync(
        string symbol,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves recently aggregated bars (candles) for a symbol.
    /// </summary>
    Task<IReadOnlyList<LiveBarResponse>> GetRecentBarsAsync(
        string symbol,
        string resolution,
        int take,
        CancellationToken cancellationToken = default);
}