namespace AlgoTrading.Application.Interfaces;

public interface IRedisPublisherService
{
    Task PublishWatchlistUpdateAsync(CancellationToken cancellationToken = default);
}
