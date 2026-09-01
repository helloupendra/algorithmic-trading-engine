// src/AlgoTrading.Application/Interfaces/IMarketTickArchiveQueue.cs
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.Interfaces;

public interface IMarketTickArchiveQueue
{
    ValueTask EnqueueAsync(
        MarketTickArchiveRequest request,
        CancellationToken cancellationToken = default);
}
