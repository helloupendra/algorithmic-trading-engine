using System.Text.Json;
using System.Text.Json.Serialization;
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

    public Task NotifyAsync(
        NotificationCategory category,
        NotificationSeverity severity,
        string title,
        string message,
        string? underlying = null,
        string? symbol = null,
        long? simulationRunId = null,
        CancellationToken cancellationToken = default)
        => PublishAsync(Payload(category, severity, title, message, underlying, symbol, simulationRunId, recordOnly: false), title);

    /// <inheritdoc />
    /// <remarks>
    /// The same payload with <c>RecordOnly</c> set: the subscriber writes the
    /// row and leaves Telegram alone.
    /// </remarks>
    public Task RecordAsync(
        NotificationCategory category,
        NotificationSeverity severity,
        string title,
        string message,
        string? underlying = null,
        string? symbol = null,
        long? simulationRunId = null,
        CancellationToken cancellationToken = default)
        => PublishAsync(Payload(category, severity, title, message, underlying, symbol, simulationRunId, recordOnly: true), title);

    /// <summary>
    /// The JSON published on <c>alerts:new</c>, in the PascalCase shape
    /// <c>AlertEventPayload</c> binds (the subscriber deserializes with no
    /// options, so binding is case-sensitive).
    /// </summary>
    public static string Payload(
        NotificationCategory category,
        NotificationSeverity severity,
        string title,
        string message,
        string? underlying,
        string? symbol,
        long? simulationRunId,
        bool recordOnly)
        => JsonSerializer.Serialize(new Published(
            title,
            message,
            // The stream shows this, so make it read like a place, not a class name.
            category.ToString().ToLowerInvariant(),
            underlying,
            symbol,
            severity.ToString().ToLowerInvariant(),
            simulationRunId,
            recordOnly));

    /// <param name="RecordOnly">
    /// Written only when true, so every message that goes to Telegram is
    /// exactly what it was before record-only existed.
    /// </param>
    private sealed record Published(
        string Title,
        string Message,
        string Source,
        string? Underlying,
        string? Symbol,
        string Severity,
        long? SimulationRunId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RecordOnly);

    private async Task PublishAsync(string payload, string title)
    {
        try
        {
            await _redis.GetSubscriber().PublishAsync(RedisChannel.Literal(Channel), payload);
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
