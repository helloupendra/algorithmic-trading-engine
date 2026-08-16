using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Application.UseCases.MarketData;

/// <summary>
/// Use case for querying historical candle data from the local database.
/// </summary>
public class GetStoredCandlesUseCase
{
    private readonly IMarketDataService _marketDataService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetStoredCandlesUseCase"/>.
    /// </summary>
    public GetStoredCandlesUseCase(IMarketDataService marketDataService)
    { 
        _marketDataService = marketDataService;
    }

    /// <summary>
    /// Fetches the candles for the requested symbol and timeframe.
    /// </summary>
    public Task<IReadOnlyList<CandleResponse>> ExecuteAsync(
        GetStoredCandlesRequest request,
        CancellationToken cancellationToken = default)
    { 
        return _marketDataService.GetStoredHistoryAsync(request, cancellationToken);
    }
}

