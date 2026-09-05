using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Publishes system notifications onto the same Redis channel the strategy
/// alerter uses, so they take the identical path to Telegram and to the
/// <c>alert_events</c> table.
/// </summary>
public class RedisSystemNotifier : ISystemNotifier
{
    /// <summary>The channel <c>AlertSubscriberService</c> listens on.</summary>
    private const string Channel = "alerts:new";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisSystemNotifier> _logger;

    public RedisSystemNotifier(IConnectionMultiplexer redis, ILogger<RedisSystemNotifier> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task NotifyAsync(
        NotificationCategory category,
        NotificationSeverity severity,
        string title,
        string message,
        string? underlying = null,
        string? symbol = null,
        long? simulationRunId = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            Title = title,
            Message = message,

            // The stream shows this, so make it read like a place, not a class name.
            Source = category.ToString().ToLowerInvariant(),
            Underlying = underlying,
            Symbol = symbol,
            Severity = severity.ToString().ToLowerInvariant(),
            SimulationRunId = simulationRunId,
        };

        try
        {
            await _redis.GetSubscriber().PublishAsync(
                RedisChannel.Literal(Channel),
                JsonSerializer.Serialize(payload));
        }
        catch (Exception ex)
        {
            // Telling the operator must never break the thing being reported: a
            // dead Redis cannot be allowed to stop a strategy from starting.
            _logger.LogWarning(
                ex,
                "Could not publish system notification '{Title}' — the event happened, the notification did not.",
                title);
        }
    }
}
