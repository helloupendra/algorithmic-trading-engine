using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.UseCases.LiveData
{
    /// <summary>
    /// Use case for retrieving the most recent raw tick data for a specific symbol.
    /// </summary>
    public class GetRecentTicksUseCase
    {
        private readonly ILiveDataService _liveDataService;

        /// <summary>
        /// Initializes a new instance of <see cref="GetRecentTicksUseCase"/>.
        /// </summary>
        public GetRecentTicksUseCase(ILiveDataService liveDataService)
        {
            _liveDataService = liveDataService;
        }

        /// <summary>
        /// Fetches the recent ticks.
        /// </summary>
        public Task<IReadOnlyList<LiveTickResponse>> ExecuteAsync(
            string symbol,
            int take,
            CancellationToken cancellationToken = default)
        {
            return _liveDataService.GetRecentTicksAsync(symbol, take, cancellationToken);
        }
    }
}
