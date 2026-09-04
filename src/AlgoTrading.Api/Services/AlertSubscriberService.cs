using System.Text.Json;
using StackExchange.Redis;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;

namespace AlgoTrading.Api.Services;

public class AlertSubscriberService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<AlertSubscriberService> _logger;
    private readonly string _botToken;
    private readonly string _chatId;
    private readonly HttpClient _httpClient;

    public AlertSubscriberService(
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        IConfiguration configuration,
        ILogger<AlertSubscriberService> logger)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
        _logger = logger;
        _botToken = configuration["Telegram:BotToken"] ?? string.Empty;
        _chatId = configuration["Telegram:ChatId"] ?? string.Empty;
        _httpClient = new HttpClient();
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

                bool delivered = false;
                if (!string.IsNullOrEmpty(_botToken) && !string.IsNullOrEmpty(_chatId))
                {
                    delivered = await SendToTelegramAsync(payload);
                }

                await SaveToDatabaseAsync(payload, delivered);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process alert event from Redis.");
            }
        });
    }

    private async Task<bool> SendToTelegramAsync(AlertEventPayload payload)
    {
        try
        {
            var text = $"🚨 ALERT: {payload.Title}!\n{payload.Message}";
            
            var requestBody = new
            {
                chat_id = _chatId,
                text = text,
                parse_mode = "HTML"
            };

            var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
            var response = await _httpClient.PostAsJsonAsync(url, requestBody);
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending alert to Telegram");
            return false;
        }
    }

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
    public int? SimulationRunId { get; set; }
}
