using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly NotifierSupervisor _notifier;

    // The backend's one-line run start and stop: StrategyController's
    // "X started on Y" and "X stopped on Y". The same rule as is_superseded()
    // in scripts/telegram_notifier.py — deliberately narrow, so "Feed stalled
    // — X on Y", "Feed recovered — X on Y" and the risk alerts still go out.
    private static readonly Regex BackendRunStartStop = new(@"\b(started|stopped) on \b", RegexOptions.CultureInvariant);

    public AlertSubscriberService(
        IServiceProvider serviceProvider,
        IConnectionMultiplexer redis,
        TelegramSender telegram,
        NotifierSupervisor notifier,
        ILogger<AlertSubscriberService> logger)
    {
        _serviceProvider = serviceProvider;
        _redis = redis;
        _telegram = telegram;
        _notifier = notifier;
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

                bool delivered = false;
                if (_telegram.IsConfigured && !IsRecordOnly(payload))
                {
                    if (IsSupersededByNotifier(payload) && await NotifierIsRunningAsync(stoppingToken))
                    {
                        _logger.LogDebug("Not sent to Telegram; the notifier reports it in full: {Title}", payload.Title);
                    }
                    else
                    {
                        delivered = await SendToTelegramAsync(payload);
                    }
                }

                await SaveToDatabaseAsync(payload, delivered);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process alert event from Redis.");
            }
        });
    }

    /// <summary>
    /// True for the backend's own run start/stop line, which the notifier
    /// reports in full ("[admin] Strategy started · …", with lots, risk, P&amp;L
    /// and who stopped it).
    /// </summary>
    /// <remarks>
    /// The supervised notifier runs with --no-forward, so its own filter never
    /// runs: its messages come back through this subscriber like any other.
    /// Every start and stop reached Telegram twice, once terse and once in
    /// full, until 27 Sep 2026.
    /// </remarks>
    public static bool IsSupersededByNotifier(AlertEventPayload payload)
    {
        if (!string.Equals(payload.Source, "strategyrun", StringComparison.OrdinalIgnoreCase)) return false;

        var title = payload.Title ?? string.Empty;
        if (title.StartsWith("Strategy started", StringComparison.Ordinal)
            || title.StartsWith("Strategy stopped", StringComparison.Ordinal))
        {
            return false; // the notifier's own
        }

        return BackendRunStartStop.IsMatch(title);
    }

    /// <summary>
    /// True for an event published to be recorded but not sent
    /// (<c>ISystemNotifier.RecordAsync</c>): the row is written, Telegram is not
    /// asked. The runners' repeated feed-stall reports go this way — every one
    /// on the record, one per underlying on the channel (25 Sep 2026: 572
    /// messages, 350 refused by Telegram).
    /// </summary>
    public static bool IsRecordOnly(AlertEventPayload payload) => payload.RecordOnly == true;

    /// <summary>
    /// True only while a notifier verified as ours is running. Anything less —
    /// stopped, restarting, a pid that cannot be verified, a status that cannot
    /// be read — sends the backend's line: a duplicate is noise, silence is
    /// the failure this desk exists to prevent.
    /// </summary>
    private async Task<bool> NotifierIsRunningAsync(CancellationToken cancellationToken)
    {
        try
        {
            return _notifier.IsVerified(await _notifier.GetStatusAsync(cancellationToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not tell whether the Telegram notifier is running; sending the backend's message.");
            return false;
        }
    }

    /// <summary>
    /// Through the shared <see cref="TelegramSender"/>, which reads the same
    /// Telegram:BotToken / Telegram:ChatId and logs failures without the token.
    /// </summary>
    private Task<bool> SendToTelegramAsync(AlertEventPayload payload)
        => _telegram.SendHtmlAsync($"🚨 ALERT: {payload.Title}!\n{payload.Message}", ChannelFor(payload.Source));

    /// <summary>
    /// The channel a payload belongs in, by its source: the desk's own
    /// processes, connectors and system notices go to the system channel;
    /// strategy runs, risk and anything not recognised stay with the trades,
    /// where a trading message can never be lost among the system's.
    /// </summary>
    public static TelegramChannel ChannelFor(string? source) =>
        (source ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "process" or "connector" or "system" => TelegramChannel.System,
            _ => TelegramChannel.Trades,
        };

    private async Task SaveToDatabaseAsync(AlertEventPayload payload, bool delivered)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        dbContext.AlertEvents.Add(ToAlertEvent(payload, delivered, DateTime.UtcNow));
        await dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// The row for one alert, each text cut to its column. The whole payload is
    /// kept, uncut, in MetadataJson (a text column).
    /// </summary>
    /// <remarks>
    /// Uncut, a message over 1,000 characters failed the whole save: on 28 Sep
    /// 387 alerts reached Telegram but never the alert log ("22001: value too
    /// long for type character varying(1000)").
    /// </remarks>
    internal static AlertEvent ToAlertEvent(AlertEventPayload payload, bool delivered, DateTime occurredUtc) => new()
    {
        OccurredUtc = occurredUtc,
        Source = Clip(payload.Source ?? "system", 60),
        Underlying = Clip(payload.Underlying ?? "UNKNOWN", 40),
        Symbol = payload.Symbol is null ? null : Clip(payload.Symbol, 100),
        Severity = Clip(payload.Severity ?? "info", 20),
        Title = Clip(payload.Title ?? "Alert", 200),
        Message = Clip(payload.Message ?? "", 1000),
        MetadataJson = JsonSerializer.Serialize(payload),
        DeliveredToTelegram = delivered,
        SimulationRunId = payload.SimulationRunId
    };

    /// <summary>The text itself when it fits; otherwise its start and an ellipsis, <paramref name="max"/> characters in all.</summary>
    internal static string Clip(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");
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

    /// <summary>True: record the event, do not send it to Telegram. Absent on everything else.</summary>
    public bool? RecordOnly { get; set; }
}
