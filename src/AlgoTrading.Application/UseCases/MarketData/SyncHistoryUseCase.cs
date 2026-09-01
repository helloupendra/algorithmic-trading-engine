using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Application.UseCases.MarketData
{
    /// <summary>
    /// Use case for forcing a direct synchronization of historical data from the broker.
    /// </summary>
    public class SyncHistoryUseCase
    {
        private readonly IMarketDataService _marketDataService;

        /// <summary>
        /// Initializes a new instance of <see cref="SyncHistoryUseCase"/>.
        /// </summary>
        public SyncHistoryUseCase(IMarketDataService marketDataService)
        {
            _marketDataService = marketDataService;
        }

        /// <summary>
        /// Executes the sync process and returns the newly downloaded candles.
        /// </summary>
        public Task<IReadOnlyList<CandleResponse>> ExecuteAsync(
            SyncHistoryRequest request,
            CancellationToken cancellationToken = default)
        { 
            return _marketDataService.SyncHistoryAsync(request, cancellationToken);
        }
    }
}
