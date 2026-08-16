using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData
{
    /// <summary>
    /// Use case for retrieving the most recent price snapshot for a single symbol.
    /// </summary>
    public class GetLatestQuoteUseCase
    {
        private readonly ILiveDataService _liveDataService;

        /// <summary>
        /// Initializes a new instance of <see cref="GetLatestQuoteUseCase"/>.
        /// </summary>
        public GetLatestQuoteUseCase(ILiveDataService liveDataService)
        {
            _liveDataService = liveDataService;
        }

        /// <summary>
        /// Fetches the latest quote for the specified symbol.
        /// </summary>
        public Task<LiveQuoteResponse?> ExecuteAsync(
            string symbol,
            CancellationToken cancellationToken = default)
        {
            return _liveDataService.GetLatestQuoteAsync(symbol, cancellationToken);
        }
    }

}
