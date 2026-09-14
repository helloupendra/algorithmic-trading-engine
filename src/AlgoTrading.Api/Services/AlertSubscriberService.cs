using System.Text.Json;
using StackExchange.Redis;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services;

public class AlertSubscriberService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<AlertSubscriberService> _logger;
    private readonly TelegramSender _telegram;

    public AlertSubscriberService(
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        TelegramSender telegram,
        ILogger<AlertSubscriberService> logger)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
        _telegram = telegram;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sub = _redis.GetSubscriber();
        _logger.LogInformation("Subscribing to alerts:new Redis channel");
        
        await sub.SubscribeAsync("alerts:new", async (channel, message) =>
        {
            if (stoppingToken.IsCancellationRequested) return;

            try
            {
                var payload = JsonSerializer.Deserialize<AlertEventPayload>((string)message!);
                if (payload == null) return;

                bool delivered = _telegram.IsConfigured && await SendToTelegramAsync(payload);

                await SaveToDatabaseAsync(payload, delivered);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process alert event from Redis.");
            }
        });
    }

    /// <summary>
    /// Through the shared <see cref="TelegramSender"/>, which reads the same
    /// Telegram:BotToken / Telegram:ChatId and logs failures without the token.
    /// </summary>
    private Task<bool> SendToTelegramAsync(AlertEventPayload payload)
        => _telegram.SendHtmlAsync($"🚨 ALERT: {payload.Title}!\n{payload.Message}");

    private async Task SaveToDatabaseAsync(AlertEventPayload payload, bool delivered)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var alert = new AlertEvent
        {
            OccurredUtc = DateTime.UtcNow,
            Source = payload.Source ?? "system",
            Underlying = payload.Underlying ?? "UNKNOWN",
            Symbol = payload.Symbol,
            Severity = payload.Severity ?? "info",
            Title = payload.Title ?? "Alert",
            Message = payload.Message ?? "",
            MetadataJson = JsonSerializer.Serialize(payload),
            DeliveredToTelegram = delivered,
            SimulationRunId = payload.SimulationRunId
        };

        dbContext.AlertEvents.Add(alert);
        await dbContext.SaveChangesAsync();
    }
}

public class AlertEventPayload
{
    public string? Title { get; set; }
    public string? Message { get; set; }
    public string? Source { get; set; }
    public string? Underlying { get; set; }
    public string? Severity { get; set; }
    public string? Symbol { get; set; }
    public long? SimulationRunId { get; set; }
}
