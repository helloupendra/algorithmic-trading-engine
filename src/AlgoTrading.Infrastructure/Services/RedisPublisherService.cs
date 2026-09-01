using AlgoTrading.Application.Interfaces;
using StackExchange.Redis;

namespace AlgoTrading.Infrastructure.Services;

public class RedisPublisherService : IRedisPublisherService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _watchlistChannel = "watchlist_updates";

    public RedisPublisherService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task PublishWatchlistUpdateAsync(CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        // We simply publish a notification signal. 
        // The python subscriber will re-fetch the active watchlist upon receiving this.
        await db.PublishAsync(_watchlistChannel, "watchlist_changed");
    }
}
