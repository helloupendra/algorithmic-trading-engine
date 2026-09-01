using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.UseCases.LiveData
{
    /// <summary>
    /// Use case for fetching the most recent aggregated real-time bars (candles) for a symbol.
    /// Useful for feeding live charts.
    /// </summary>
    public class GetRecentBarsUseCase
    {
        private readonly ILiveDataService _liveDataService;

        /// <summary>
        /// Initializes a new instance of <see cref="GetRecentBarsUseCase"/>.
        /// </summary>
        public GetRecentBarsUseCase(ILiveDataService liveDataService)
        {
            _liveDataService = liveDataService;
        }

        /// <summary>
        /// Fetches the recent bars.
        /// </summary>
        public Task<IReadOnlyList<LiveBarResponse>> ExecuteAsync(
            string symbol,
            string resolution,
            int take,
            CancellationToken cancellationToken = default)
        {
            return _liveDataService.GetRecentBarsAsync(symbol, resolution, take, cancellationToken);
        }
    }
}
