using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.UseCases.LiveData
{

    /// <summary>
    /// Use case for appending a new raw market tick to the historical data store.
    /// </summary>
    public class UpsertLiveTickUseCase
    {
        private readonly ILiveDataService _liveDataService;

        /// <summary>
        /// Initializes a new instance of <see cref="UpsertLiveTickUseCase"/>.
        /// </summary>
        public UpsertLiveTickUseCase(ILiveDataService liveDataService)
        {
            _liveDataService = liveDataService;
        }

        /// <summary>
        /// Appends the new tick data.
        /// </summary>
        public Task ExecuteAsync(
            UpsertLiveTickRequest request,
            CancellationToken cancellationToken = default)
        {
            return _liveDataService.AppendLiveTickAsync(request, cancellationToken);
        }
    }

}
