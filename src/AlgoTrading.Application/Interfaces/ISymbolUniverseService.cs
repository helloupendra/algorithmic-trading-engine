using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Application.Interfaces
{
    /// <summary>
    /// Service interface responsible for ensuring data prerequisites are met before backtesting.
    /// </summary>
    public interface ISymbolUniverseService
    {
        /// <summary>
        /// Checks the local database for missing time ranges and backfills them from the broker if necessary.
        /// </summary>
        Task<BackfillHistoryResponse> EnsureHistoryCoverageAsync(
            BackfillHistoryRequest request,
            CancellationToken cancellationToken = default);
    }
}
