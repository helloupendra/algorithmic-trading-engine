using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Service interface for fetching and syncing historical market data (candles).
/// </summary>
public interface IMarketDataService
{
    /// <summary>
    /// Forces a synchronization of historical candles from the broker into the local database.
    /// </summary>
    Task<IReadOnlyList<CandleResponse>> SyncHistoryAsync(
        SyncHistoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves historical candles directly from the local database.
    /// </summary>
    Task<IReadOnlyList<CandleResponse>> GetStoredHistoryAsync(
        GetStoredCandlesRequest request,
        CancellationToken cancellationToken = default);
}
