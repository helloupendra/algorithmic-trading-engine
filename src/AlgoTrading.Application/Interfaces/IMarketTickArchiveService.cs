// src/AlgoTrading.Application/Interfaces/IMarketTickArchiveService.cs
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.Interfaces;

public interface IMarketTickArchiveService
{
    Task ArchiveAsync(
        MarketTickArchiveRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MarketTickDto>> GetRangeAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        int take,
        CancellationToken cancellationToken = default);
}