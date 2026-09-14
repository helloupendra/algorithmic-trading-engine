using System.Diagnostics;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>Candles that closed at the same minute, sent as one Telegram message.</summary>
public sealed record PatternTelegramBatch(DateTime ClosedAtUtc, IReadOnlyList<RecordedPattern> Patterns);

/// <summary>Grouping for delivery, pure so it can be pinned by tests.</summary>
public static class PatternTelegramBatches
{
    /// <summary>Only patterns a rule sends; one batch per closing minute, oldest first.</summary>
    public static IReadOnlyList<PatternTelegramBatch> From(IEnumerable<RecordedPattern> recorded) =>
        recorded
            .Where(r => r.Notify)
            .GroupBy(r => new DateTime(r.Occurrence.BarEndUtc.Ticks - r.Occurrence.BarEndUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new PatternTelegramBatch(
                g.Key,
                g.OrderBy(r => UnderlyingCatalog.SortRank(UnderlyingCatalog.InferUnderlying(r.Occurrence.Symbol)))
                    .ThenBy(r => r.Occurrence.Symbol, StringComparer.Ordinal)
                    .ThenBy(r => r.Occurrence.TimeframeMinutes)
                    .ThenBy(r => r.Occurrence.Pattern)
                    .ToList()))
            .ToList();
}

/// <summary>
/// Runs the candle-pattern scanner every <see cref="PatternScanSettings.Interval"/>
/// inside the API and delivers what it finds to Telegram.
/// </summary>
/// <remarks>
/// <para>
/// Delivery goes straight to <see cref="TelegramSender"/>, not through Redis
/// <c>alerts:new</c>: that path writes its own <c>alert_events</c> row, and a
/// pattern alert has already been recorded, with its candle in the metadata and
/// a key that stops it being recorded twice. Sending after the row exists also
/// means a restart can never notify the same candle again.
/// </para>
/// <para>
/// Rate limited to <see cref="MaxMessages"/> messages in <see cref="MessageWindow"/>.
/// Several patterns closing at the same minute are already one message; the
/// limit is for a minute that is noisy in a way nobody anticipated. A message
/// over the limit is not sent and its alerts stay "recorded only" on the page.
/// </para>
/// <para>
/// Off with <c>PatternAlerts:Enabled=false</c>, for a second API pointed at a
/// database another API already scans.
/// </para>
/// </remarks>
public sealed class CandlePatternAlertService : BackgroundService
{
    public const int MaxMessages = 4;
    public static readonly TimeSpan MessageWindow = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PatternScannerState _state;
    private readonly TelegramSender _telegram;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CandlePatternAlertService> _logger;
    private readonly SlidingWindowLimiter _limiter = new(MaxMessages, MessageWindow);

    public CandlePatternAlertService(
        IServiceScopeFactory scopeFactory,
        PatternScannerState state,
        TelegramSender telegram,
        IConfiguration configuration,
        ILogger<CandlePatternAlertService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _telegram = telegram;
        _configuration = configuration;
        _logger = logger;
    }

    public static PatternScanSettings Settings { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool enabled = _configuration.GetValue("PatternAlerts:Enabled", true);
        _state.SetEnabled(enabled);
        if (!enabled)
        {
            _logger.LogInformation("Candle-pattern alerts are off (PatternAlerts:Enabled=false).");
            return;
        }

        using var timer = new PeriodicTimer(Settings.Interval);
        do
        {
            await ScanOnceAsync(stoppingToken);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ScanOnceAsync(CancellationToken stoppingToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var scanner = scope.ServiceProvider.GetRequiredService<CandlePatternScanner>();
            var outcome = await scanner.ScanAsync(DateTime.UtcNow, Settings, stoppingToken);
            _state.ScanCompleted(outcome, started.Elapsed.TotalMilliseconds);

            if (outcome.Recorded.Count > 0)
            {
                _logger.LogInformation("Candle patterns: recorded {Count} new alert(s), {Notify} for Telegram.",
                    outcome.Recorded.Count, outcome.Recorded.Count(r => r.Notify));
                await DeliverAsync(scope.ServiceProvider.GetRequiredService<TradingDbContext>(), outcome.Recorded, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // A bad scan must not end the loop; the next one starts clean.
            _logger.LogError(ex, "Candle-pattern scan failed.");
            _state.ScanFailed(DateTime.UtcNow, ex.Message);
        }
    }

    private async Task DeliverAsync(TradingDbContext db, IReadOnlyList<RecordedPattern> recorded, CancellationToken cancellationToken)
    {
        var batches = PatternTelegramBatches.From(recorded);
        if (batches.Count == 0) return;

        if (!_telegram.IsConfigured)
        {
            _state.TelegramSuppressed("Telegram is not configured (Telegram:BotToken / Telegram:ChatId).");
            return;
        }

        foreach (var batch in batches)
        {
            if (!_limiter.TryAcquire(DateTime.UtcNow))
            {
                _logger.LogWarning(
                    "Candle patterns: {Count} alert(s) for candles closed {Clock} IST not sent — more than {Max} messages in {Minutes} minutes. They are on the Pattern alerts page.",
                    batch.Patterns.Count, PatternAlertText.IstClock(batch.ClosedAtUtc), MaxMessages, MessageWindow.TotalMinutes);
                _state.TelegramSuppressed($"Rate limit: more than {MaxMessages} messages in {MessageWindow.TotalMinutes:0} minutes; {batch.Patterns.Count} alert(s) recorded only.");
                continue;
            }

            var text = PatternAlertText.TelegramMessage(batch.ClosedAtUtc, batch.Patterns.Select(p => p.Occurrence).ToList());
            if (!await _telegram.SendHtmlAsync(text, cancellationToken))
            {
                _state.TelegramFailed("Telegram refused or could not be reached; see the API log.");
                continue;
            }

            _state.TelegramSent(DateTime.UtcNow);
            var ids = batch.Patterns.Select(p => p.EventId).ToList();
            var rows = await db.AlertEvents.Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);
            foreach (var row in rows) row.DeliveredToTelegram = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
