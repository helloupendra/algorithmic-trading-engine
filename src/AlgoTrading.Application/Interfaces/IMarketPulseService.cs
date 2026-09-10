using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Application.Interfaces;

public interface IMarketPulseService
{
    /// <summary>The pulse with the last saved quote on every row.</summary>
    Task<MarketPulseResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Make sure every pulse symbol is on the live feed's watchlist — resolving
    /// each commodity to its nearest unexpired future and retiring the contract
    /// it replaced. Returns the symbols now subscribed.
    /// </summary>
    Task<IReadOnlyList<string>> EnsureSubscribedAsync(CancellationToken cancellationToken = default);
}
