using System.Diagnostics;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>What the indicator scanner last did, for the page. A snapshot; counters are since this API started.</summary>
public sealed record IndicatorScannerSnapshot(
    bool Enabled,
    DateTime StartedUtc,
    DateTime? LastScanUtc,
    double? LastScanMilliseconds,
    IndicatorScanOutcome? LastOutcome,
    DateTime? LastErrorUtc,
    string? LastError,
    DateTime? LastTelegramUtc,
    int TelegramMessagesSent,
    int TelegramMessagesSuppressed,
    int TelegramMessagesFailed,
    string? LastTelegramProblem);

/// <summary>Shared between the hosted scanner (writer) and the controller (reader).</summary>
public sealed class IndicatorScannerState
{
    private readonly object _gate = new();
    private IndicatorScannerSnapshot _snapshot = new(true, DateTime.UtcNow, null, null, null, null, null, null, 0, 0, 0, null);

    public IndicatorScannerSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate) _snapshot = _snapshot with { Enabled = enabled };
    }

    public void ScanCompleted(IndicatorScanOutcome outcome, double milliseconds)
    {
        lock (_gate) _snapshot = _snapshot with { LastScanUtc = outcome.ScannedUtc, LastScanMilliseconds = milliseconds, LastOutcome = outcome };
    }

    /// <summary>A scan that found nothing to scan (no config file): it ran, and watched nothing.</summary>
    public void ScanSkipped(DateTime nowUtc)
    {
        lock (_gate) _snapshot = _snapshot with { LastScanUtc = nowUtc, LastScanMilliseconds = 0, LastOutcome = null };
    }

    public void ScanFailed(DateTime nowUtc, string error)
    {
        lock (_gate) _snapshot = _snapshot with { LastErrorUtc = nowUtc, LastError = error };
    }

    public void TelegramSent(DateTime nowUtc)
    {
        lock (_gate) _snapshot = _snapshot with { LastTelegramUtc = nowUtc, TelegramMessagesSent = _snapshot.TelegramMessagesSent + 1 };
    }

    public void TelegramSuppressed(string reason)
    {
        lock (_gate) _snapshot = _snapshot with { TelegramMessagesSuppressed = _snapshot.TelegramMessagesSuppressed + 1, LastTelegramProblem = reason };
    }

    public void TelegramFailed(string reason)
    {
        lock (_gate) _snapshot = _snapshot with { TelegramMessagesFailed = _snapshot.TelegramMessagesFailed + 1, LastTelegramProblem = reason };
    }
}

/// <summary>Alerts on candles that closed at the same minute, sent as one Telegram message.</summary>
public sealed record IndicatorTelegramBatch(DateTime ClosedAtUtc, IReadOnlyList<RecordedIndicator> Alerts);

/// <summary>Grouping for delivery, pure so it can be pinned by tests.</summary>
public static class IndicatorTelegramBatches
{
    /// <summary>
    /// Only alerts that are to be sent; one batch per closing minute, oldest
    /// first; inside it, a symbol's rules on one candle next to each other so
    /// the message can put them on one line.
    /// </summary>
    public static IReadOnlyList<IndicatorTelegramBatch> From(IEnumerable<RecordedIndicator> recorded) =>
        recorded
            .Where(r => r.Notify)
            .GroupBy(r => new DateTime(r.Occurrence.BarEndUtc.Ticks - r.Occurrence.BarEndUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new IndicatorTelegramBatch(
                g.Key,
                g.OrderBy(r => UnderlyingCatalog.SortRank(UnderlyingCatalog.InferUnderlying(r.Occurrence.Symbol)))
                    .ThenBy(r => r.Occurrence.Symbol, StringComparer.Ordinal)
                    .ThenBy(r => r.Occurrence.TimeframeMinutes)
                    .ThenBy(r => r.Occurrence.Rule.Kind)
                    .ThenBy(r => r.Occurrence.Rule.Key, StringComparer.Ordinal)
                    .ToList()))
            .ToList();
}

/// <summary>
/// Runs the indicator scanner inside the API, on the candle-pattern scanner's
/// clock (<see cref="CandlePatternAlertService.Settings"/>: every 20 seconds,
/// 10 seconds behind the clock, 3 minutes past the close), and delivers what it
/// finds to the same Telegram channel as the patterns.
/// </summary>
/// <remarks>
/// <para>
/// A service of its own rather than a step in the pattern service's loop, so
/// a config file that breaks this scanner cannot stop the patterns, and each
/// has its own health on the page.
/// </para>
/// <para>
/// Reads config/indicator-alerts.txt on every scan (re-parsed only when it
/// changed). No file: nothing is watched and the page says where it looked.
/// </para>
/// <para>
/// Off with <c>IndicatorAlerts:Enabled=false</c>; when that is not set it
/// follows <c>PatternAlerts:Enabled</c>, so the one switch a second API pointed
/// at the same database already sets turns both scanners off.
/// </para>
/// </remarks>
public sealed class IndicatorAlertService : BackgroundService
{
    public const int MaxMessages = 4;
    public static readonly TimeSpan MessageWindow = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IndicatorScannerState _state;
    private readonly IndicatorAlertConfigSource _configSource;
    private readonly TelegramSender _telegram;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IndicatorAlertService> _logger;
    private readonly SlidingWindowLimiter _limiter = new(MaxMessages, MessageWindow);

    public IndicatorAlertService(
        IServiceScopeFactory scopeFactory,
        IndicatorScannerState state,
        IndicatorAlertConfigSource configSource,
        TelegramSender telegram,
        IConfiguration configuration,
        ILogger<IndicatorAlertService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _configSource = configSource;
        _telegram = telegram;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary><c>IndicatorAlerts:Enabled</c>, or <c>PatternAlerts:Enabled</c> when that is not set; on by default.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue<bool?>("IndicatorAlerts:Enabled") ?? configuration.GetValue("PatternAlerts:Enabled", true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool enabled = IsEnabled(_configuration);
        _state.SetEnabled(enabled);
        if (!enabled)
        {
            _logger.LogInformation("Indicator alerts are off (IndicatorAlerts:Enabled / PatternAlerts:Enabled is false).");
            return;
        }

        using var timer = new PeriodicTimer(CandlePatternAlertService.Settings.Interval);
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
            var read = _configSource.Read();
            if (read.Config is null)
            {
                _state.ScanSkipped(DateTime.UtcNow);
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var scanner = scope.ServiceProvider.GetRequiredService<IndicatorAlertScanner>();
            var outcome = await scanner.ScanAsync(DateTime.UtcNow, read.Config, CandlePatternAlertService.Settings, stoppingToken);
            _state.ScanCompleted(outcome, started.Elapsed.TotalMilliseconds);

            if (outcome.Recorded.Count > 0)
            {
                _logger.LogInformation("Indicator alerts: recorded {Count} new alert(s), {Notify} for Telegram.",
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
            _logger.LogError(ex, "Indicator scan failed.");
            _state.ScanFailed(DateTime.UtcNow, ex.Message);
        }
    }

    private async Task DeliverAsync(TradingDbContext db, IReadOnlyList<RecordedIndicator> recorded, CancellationToken cancellationToken)
    {
        var batches = IndicatorTelegramBatches.From(recorded);
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
                    "Indicator alerts: {Count} alert(s) for candles closed {Clock} IST not sent — more than {Max} messages in {Minutes} minutes. They are on the Pattern alerts page.",
                    batch.Alerts.Count, PatternAlertText.IstClock(batch.ClosedAtUtc), MaxMessages, MessageWindow.TotalMinutes);
                _state.TelegramSuppressed($"Rate limit: more than {MaxMessages} messages in {MessageWindow.TotalMinutes:0} minutes; {batch.Alerts.Count} alert(s) recorded only.");
                continue;
            }

            var text = IndicatorAlertText.TelegramMessage(batch.ClosedAtUtc, batch.Alerts.Select(a => a.Occurrence).ToList());
            // Market information, not trades: the system channel, with the patterns.
            if (!await _telegram.SendHtmlAsync(text, TelegramChannel.System, cancellationToken))
            {
                _state.TelegramFailed("Telegram refused or could not be reached; see the API log.");
                continue;
            }

            _state.TelegramSent(DateTime.UtcNow);
            var ids = batch.Alerts.Select(a => a.EventId).ToList();
            var rows = await db.AlertEvents.Where(e => ids.Contains(e.Id)).ToListAsync(cancellationToken);
            foreach (var row in rows) row.DeliveredToTelegram = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
