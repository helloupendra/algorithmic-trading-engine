// src/AlgoTrading.Worker.MarketData/Processing/ITickBatchProcessor.cs
using AlgoTrading.Worker.MarketData.Models;

namespace AlgoTrading.Worker.MarketData.Processing;

public interface ITickBatchProcessor
{
    Task ProcessAsync(
        IReadOnlyList<MarketTickStreamMessage> messages,
        CancellationToken cancellationToken = default);
}
