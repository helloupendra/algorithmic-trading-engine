using System.Diagnostics;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.LevelAlerts;

/// <summary>
/// Runs the level-alert scanner every <see cref="Interval"/> inside the API and
/// tells the owner: the console at once, and Telegram's Desk System chat for
/// an admin's rule.
/// </summary>
/// <remarks>
/// <para>
/// The console hears through a desk event (kind <c>alert</c>) to the owner's
/// connections and the admins'; it is a hint to fetch again, so a lost one
/// costs a badge thirty seconds, never an alert. The fire itself is already a
/// row by then.
/// </para>
/// <para>
/// Telegram goes to the Desk System chat, which is the operator's, and only
/// for rules an admin owns: the desk has no Telegram chat per trader, and a
/// trader's levels in the operator's channel would be noise to him and a
/// leak to them. A scan's fires go as one message, at most
/// <see cref="MaxMessages"/> in <see cref="MessageWindow"/>; one over the limit
/// stays on the page, marked so.
/// </para>
/// <para>
/// Off with <c>LevelAlerts:Enabled=false</c>; when that is not set it follows
/// <c>PatternAlerts:Enabled</c>, the one switch a second API pointed at the
/// same database already sets.
/// </para>
/// </remarks>
public sealed class LevelAlertService : BackgroundService
{
    /// <summary>Time between scans. A scan in session is one small query for the rules and one for the prices.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    public const int MaxMessages = 6;
    public static readonly TimeSpan MessageWindow = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LevelAlertState _state;
    private readonly TelegramSender _telegram;
    private readonly IConfiguration _configuration;
    private readonly ILogger<LevelAlertService> _logger;
    private readonly SlidingWindowLimiter _limiter = new(MaxMessages, MessageWindow);

    public LevelAlertService(
        IServiceScopeFactory scopeFactory,
        LevelAlertState state,
        TelegramSender telegram,
        IConfiguration configuration,
        ILogger<LevelAlertService> logger)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _telegram = telegram;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read so that a value like "off" cannot throw and stop the host (AlertSwitch).
        bool enabled = AlertSwitch.Read(_configuration, fallback: true, out var problem, "LevelAlerts:Enabled", "PatternAlerts:Enabled");
        _state.SetEnabled(enabled);
        if (problem is not null)
        {
            _logger.LogWarning("Level alerts are off: {Problem}", problem);
            _state.ScanFailed(DateTime.UtcNow, problem);
        }

        if (!enabled)
        {
            if (problem is null) _logger.LogInformation("Level alerts are off (LevelAlerts:Enabled or PatternAlerts:Enabled is false).");
            return;
        }

        using var timer = new PeriodicTimer(Interval);
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
            var scanner = scope.ServiceProvider.GetRequiredService<LevelAlertScanner>();
            var outcome = await scanner.ScanAsync(DateTime.UtcNow, stoppingToken);
            _state.ScanTook(started.Elapsed.TotalMilliseconds);

            if (outcome.Fired.Count == 0) return;

            _logger.LogInformation("Level alerts: {Count} fired ({Titles}).",
                outcome.Fired.Count, string.Join("; ", outcome.Fired.Select(f => f.Fire.Title)));

            var deskEvents = scope.ServiceProvider.GetService<IDeskEventPublisher>();
            foreach (var f in outcome.Fired)
            {
                deskEvents.TryPublish(new DeskEvent(DeskEventKinds.Alert, null, f.Fire.UserId, f.Fire.Target, f.Fire.FiredUtc, f.Fire.Title));
            }

            await DeliverAsync(scope.ServiceProvider.GetRequiredService<TradingDbContext>(), outcome.Fired, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // A bad scan must not end the loop; the next one starts clean.
            _logger.LogError(ex, "Level-alert scan failed.");
            _state.ScanFailed(DateTime.UtcNow, ex.Message);
        }
    }

    private async Task DeliverAsync(TradingDbContext db, IReadOnlyList<RecordedLevelFire> fired, CancellationToken cancellationToken)
    {
        var due = fired.Where(f => f.OwnerIsAdmin && f.Notify).Select(f => f.Fire).ToList();
        if (due.Count == 0) return;

        string? skipped = null;
        if (!_telegram.IsConfigured)
        {
            skipped = "Telegram is not configured on the server.";
            _state.TelegramSuppressed(skipped);
        }
        else if (!_limiter.TryAcquire(DateTime.UtcNow))
        {
            skipped = $"Not sent: more than {MaxMessages} level-alert messages in {MessageWindow.TotalMinutes:0} minutes.";
            _logger.LogWarning("Level alerts: {Count} alert(s) not sent to Telegram — {Reason}", due.Count, skipped);
            _state.TelegramSuppressed(skipped);
        }
        else if (await _telegram.SendHtmlAsync(LevelAlertText.TelegramMessage(due), TelegramChannel.System, cancellationToken))
        {
            _state.TelegramSent(DateTime.UtcNow);
        }
        else
        {
            skipped = "Telegram refused it or could not be reached; see the API log.";
            _state.TelegramFailed(skipped);
        }

        // The rows are tracked by the scan's context, which recorded them.
        foreach (var fire in due)
        {
            fire.DeliveredToTelegram = skipped is null;
            fire.TelegramNote = skipped;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
