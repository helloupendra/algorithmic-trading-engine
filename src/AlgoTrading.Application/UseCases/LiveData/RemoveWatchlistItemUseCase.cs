using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData
{

    /// <summary>
    /// Use case for untracking a symbol from the live market data feed.
    /// </summary>
    public class RemoveWatchlistItemUseCase
    {
        private readonly ILiveDataService _liveDataService;
        private readonly IRedisPublisherService _redisPublisherService;

        /// <summary>
        /// Initializes a new instance of <see cref="RemoveWatchlistItemUseCase"/>.
        /// </summary>
        public RemoveWatchlistItemUseCase(
            ILiveDataService liveDataService,
            IRedisPublisherService redisPublisherService)
        {
            _liveDataService = liveDataService;
            _redisPublisherService = redisPublisherService;
        }

        /// <summary>
        /// Removes the item with the specified ID.
        /// </summary>
        public async Task ExecuteAsync(long id, CancellationToken cancellationToken = default)
        {
            await _liveDataService.RemoveWatchlistItemAsync(id, cancellationToken);
            await _redisPublisherService.PublishWatchlistUpdateAsync(cancellationToken);
        }
    }

}
