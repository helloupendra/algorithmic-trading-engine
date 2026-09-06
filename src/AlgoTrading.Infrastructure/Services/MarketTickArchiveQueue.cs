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
        // Large bounded queue for live-session safety.
        //
        // DropOldest, not Wait. This queue feeds the tick ARCHIVE — history,
        // read later. The live path that fills it is the one strategies trade
        // on. Under Wait, a reader that falls behind (or dies) applies back
        // pressure straight into the tick request, so a problem with storing
        // history becomes a problem with receiving prices. The archive is the
        // right thing to sacrifice: dropping the oldest rows costs a gap in a
        // chart, blocking costs the live feed.
        _channel = Channel.CreateBounded<MarketTickArchiveRequest>(
            new BoundedChannelOptions(100_000)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
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