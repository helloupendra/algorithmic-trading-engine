// src/AlgoTrading.Infrastructure/Services/MarketTickArchiveQueue.cs
using System.Threading.Channels;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Infrastructure.Services;

public class MarketTickArchiveQueue : IMarketTickArchiveQueue
{
    private readonly Channel<MarketTickArchiveRequest> _channel;

    public MarketTickArchiveQueue()
    {
        // Large bounded queue for live-session safety
        _channel = Channel.CreateBounded<MarketTickArchiveRequest>(
            new BoundedChannelOptions(100_000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    public ValueTask EnqueueAsync(
        MarketTickArchiveRequest request,
        CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(request, cancellationToken);
    }

    internal ChannelReader<MarketTickArchiveRequest> Reader => _channel.Reader;
}