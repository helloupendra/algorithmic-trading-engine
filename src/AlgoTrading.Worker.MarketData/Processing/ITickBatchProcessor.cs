// src/AlgoTrading.Worker.MarketData/Processing/ITickBatchProcessor.cs
using AlgoTrading.Worker.MarketData.Models;

namespace AlgoTrading.Worker.MarketData.Processing;

public interface ITickBatchProcessor
{
    /// <param name="redelivered">
    /// True when these entries were delivered before and may already be stored:
    /// a batch whose write committed but whose acknowledgement to Redis was lost
    /// comes round again, and must not be archived twice.
    /// </param>
    Task ProcessAsync(
        IReadOnlyList<MarketTickStreamMessage> messages,
        CancellationToken cancellationToken = default,
        bool redelivered = false);
}
