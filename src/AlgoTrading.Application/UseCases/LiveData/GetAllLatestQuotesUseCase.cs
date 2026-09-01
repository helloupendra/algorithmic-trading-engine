using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;


namespace AlgoTrading.Application.UseCases.LiveData
{
    /// <summary>
    /// Use case for retrieving the most recent price snapshots for all actively tracked symbols.
    /// </summary>
    public class GetAllLatestQuotesUseCase
    {
        private readonly ILiveDataService _liveDataService;

        /// <summary>
        /// Initializes a new instance of <see cref="GetAllLatestQuotesUseCase"/>.
        /// </summary>
        public GetAllLatestQuotesUseCase(ILiveDataService liveDataService)
        {
            _liveDataService = liveDataService;
        }

        /// <summary>
        /// Fetches all available live quotes.
        /// </summary>
        public Task<IReadOnlyList<LiveQuoteResponse>> ExecuteAsync(
            CancellationToken cancellationToken = default)
        {
            return _liveDataService.GetAllLatestQuotesAsync(cancellationToken);
        }
    }

}
