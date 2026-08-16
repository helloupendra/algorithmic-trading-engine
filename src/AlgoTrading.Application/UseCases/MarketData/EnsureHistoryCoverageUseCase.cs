using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;



namespace AlgoTrading.Contracts.MarketData
{
    /// <summary>
    /// Use case for verifying and backfilling required historical data before starting a backtest.
    /// </summary>
    public class EnsureHistoryCoverageUseCase
    {
        private readonly ISymbolUniverseService _symbolUniverseService;

        /// <summary>
        /// Initializes a new instance of <see cref="EnsureHistoryCoverageUseCase"/>.
        /// </summary>
        public EnsureHistoryCoverageUseCase(ISymbolUniverseService symbolUniverseService)
        {
            _symbolUniverseService = symbolUniverseService;
        }

        /// <summary>
        /// Executes the coverage check and triggers downloads if data is missing.
        /// </summary>
        public Task<BackfillHistoryResponse> ExecuteAsync(
            BackfillHistoryRequest request,
            CancellationToken cancellationToken = default)
        {
            return _symbolUniverseService.EnsureHistoryCoverageAsync(request, cancellationToken);
        }
    }
}
