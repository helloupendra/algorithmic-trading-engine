using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for subscribing a new symbol to the live market data feed.
/// </summary>
public class UpsertWatchlistItemUseCase
{
    private readonly ILiveDataService _liveDataService;
    private readonly IRedisPublisherService _redisPublisherService;

    /// <summary>
    /// Initializes a new instance of <see cref="UpsertWatchlistItemUseCase"/>.
    /// </summary>
    public UpsertWatchlistItemUseCase(
        ILiveDataService liveDataService,
        IRedisPublisherService redisPublisherService)
    {
        _liveDataService = liveDataService;
        _redisPublisherService = redisPublisherService;
    }

    /// <summary>
    /// Adds or updates the symbol in the watchlist.
    /// </summary>
    public async Task<LiveWatchlistItem> ExecuteAsync(
        UpsertWatchlistItemRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _liveDataService.UpsertWatchlistItemAsync(request, cancellationToken);
        await _redisPublisherService.PublishWatchlistUpdateAsync(cancellationToken);
        return result;
    }
}
