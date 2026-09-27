using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// What every market-intelligence recorder shares: its switch, a scope per
/// run, and the rule that one run failing never ends the loop.
/// </summary>
/// <remarks>
/// A source failing inside a run (one feed of 33) is logged by the
/// recorder, once per condition (see <see cref="RecorderHealth"/>). A run that
/// fails as a whole reaches this class, and is logged the same way: when it
/// starts failing, when the error changes, and when it works again, not on
/// every poll. None of these services opens a broker or feed connection: they
/// read public pages and archives, and write only the market-intelligence tables.
/// </remarks>
public abstract class MarketIntelligenceLoop : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly bool _enabled;
    private readonly string _switch;
    private readonly Dictionary<string, string> _failing = new(StringComparer.Ordinal);

    protected MarketIntelligenceLoop(IServiceScopeFactory scopes, ILogger logger, bool enabled, string switchName)
    {
        _scopes = scopes;
        Logger = logger;
        _enabled = enabled;
        _switch = switchName;
    }

    protected ILogger Logger { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            Logger.LogInformation("{Service} is switched off ({Section}:{Switch}=false).", GetType().Name, MarketIntelligenceOptions.SectionName, _switch);
            return;
        }

        try
        {
            await LoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The service's own schedule; returns only when the API stops.</summary>
    protected abstract Task LoopAsync(CancellationToken ct);

    /// <summary>Runs one piece of work in its own scope; false when it threw.</summary>
    protected async Task<bool> InScopeAsync(string what, Func<IServiceProvider, Task> work, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            await work(scope.ServiceProvider);
            if (_failing.Remove(what)) Logger.LogInformation("{Service}: {What} works again.", GetType().Name, what);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (!_failing.TryGetValue(what, out var previous) || previous != ex.Message)
                Logger.LogWarning(ex, "{Service}: {What} failed: {Error}. Logged once until it recovers or fails differently.", GetType().Name, what, ex.Message);
            else
                Logger.LogDebug("{Service}: {What} still failing: {Error}", GetType().Name, what, ex.Message);

            _failing[what] = ex.Message;
            return false;
        }
    }

    protected static Task SleepAsync(TimeSpan delay, CancellationToken ct) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, ct) : Task.CompletedTask;
}

/// <summary>
/// Every news feed, every five minutes, all day and every day: RSS keeps no
/// archive, so a headline not recorded today is lost. See <see cref="NewsRecorder"/>.
/// </summary>
public sealed class NewsRecorderService : MarketIntelligenceLoop
{
    public NewsRecorderService(IServiceScopeFactory scopes, ILogger<NewsRecorderService> logger, IOptions<MarketIntelligenceOptions> options)
        : base(scopes, logger, options.Value.NewsEnabled, nameof(MarketIntelligenceOptions.NewsEnabled))
    {
    }

    protected override async Task LoopAsync(CancellationToken ct)
    {
        // Soon after start: the API restarts every morning, and every minute
        // without polling is a minute a short-lived headline can be missed.
        await SleepAsync(TimeSpan.FromSeconds(20), ct);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            await InScopeAsync("news poll", sp => sp.GetRequiredService<NewsRecorder>().RecordAsync(ct), ct);
            await SleepAsync(started + MarketIntelligenceSchedule.NewsEvery - DateTime.UtcNow, ct);
        }
    }
}

/// <summary>
/// NSE's corporate announcements every 10 minutes and its board-meeting
/// calendar every 4 hours, 06:00–23:30 IST, every day. See
/// <see cref="CorporateFilingsRecorder"/> for how a burst or a restart is caught up.
/// </summary>
public sealed class CorporateFilingsRecorderService : MarketIntelligenceLoop
{
    public CorporateFilingsRecorderService(IServiceScopeFactory scopes, ILogger<CorporateFilingsRecorderService> logger, IOptions<MarketIntelligenceOptions> options)
        : base(scopes, logger, options.Value.AnnouncementsEnabled, nameof(MarketIntelligenceOptions.AnnouncementsEnabled))
    {
    }

    protected override async Task LoopAsync(CancellationToken ct)
    {
        await SleepAsync(TimeSpan.FromSeconds(45), ct);

        // Until one poll has succeeded since start, each poll asks for the
        // missed days too: the API was down, and filings went on.
        bool caughtUp = false;
        DateTime? lastAnnouncements = null, lastCalendar = null;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (MarketIntelligenceSchedule.InFilingsWindow(now))
            {
                if (lastAnnouncements is null || now - lastAnnouncements >= MarketIntelligenceSchedule.AnnouncementsEvery)
                {
                    lastAnnouncements = now;
                    bool firstSinceStart = !caughtUp;
                    if (await InScopeAsync("announcements poll", sp => sp.GetRequiredService<CorporateFilingsRecorder>().RecordAnnouncementsAsync(firstSinceStart, ct), ct))
                        caughtUp = true;
                }

                if (lastCalendar is null || now - lastCalendar >= MarketIntelligenceSchedule.CalendarEvery)
                {
                    lastCalendar = now;
                    await InScopeAsync("event calendar poll", sp => sp.GetRequiredService<CorporateFilingsRecorder>().RecordCalendarAsync(ct), ct);
                }
            }

            await SleepAsync(TimeSpan.FromSeconds(30), ct);
        }
    }
}

/// <summary>
/// GIFT Nifty and every global key, every 15 minutes from 06:00 to 16:00 IST
/// on weekdays, including 08:45 for the 08:50 forecasts.
/// </summary>
/// <remarks>
/// The loop sleeps until the next slot rather than polling, so a slot is taken
/// on its minute. The morning job restarts the API at about 08:45; a slot
/// missed by a restart is taken on start if it is less than ten minutes old
/// (<see cref="MarketIntelligenceSchedule.DueSlot"/>), and a slot already
/// taken before the restart (a snapshot stored at or after it) is not taken
/// twice.
/// </remarks>
public sealed class QuoteSnapshotRecorderService : MarketIntelligenceLoop
{
    public QuoteSnapshotRecorderService(IServiceScopeFactory scopes, ILogger<QuoteSnapshotRecorderService> logger, IOptions<MarketIntelligenceOptions> options)
        : base(scopes, logger, options.Value.QuoteSnapshotsEnabled, nameof(MarketIntelligenceOptions.QuoteSnapshotsEnabled))
    {
    }

    protected override async Task LoopAsync(CancellationToken ct)
    {
        await SleepAsync(TimeSpan.FromSeconds(10), ct);

        DateTime? lastTaken = null;
        var latest = MarketIntelligenceSchedule.LatestSlot(DateTime.UtcNow);
        if (latest is not null)
        {
            await InScopeAsync("snapshot check", async sp =>
            {
                var db = sp.GetRequiredService<TradingDbContext>();
                if (await db.MarketQuoteSnapshots.AsNoTracking().AnyAsync(x => x.FetchedUtc >= latest.Value, ct)) lastTaken = latest;
            }, ct);
        }

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var due = MarketIntelligenceSchedule.DueSlot(now, lastTaken);
            if (due is not null)
            {
                // Marked before it runs: a failing source must not make the
                // loop take the same slot again and again until the next one.
                lastTaken = due;
                await InScopeAsync("snapshot", sp => sp.GetRequiredService<GlobalMarketsRecorder>().SnapshotAsync(ct), ct);
            }

            var next = MarketIntelligenceSchedule.NextSlot(DateTime.UtcNow);
            await SleepAsync(Min(next - DateTime.UtcNow, TimeSpan.FromMinutes(10)), ct);
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}

/// <summary>
/// The overseas daily bars: the day's update once after 07:00 IST, and the
/// history from 2020 of any market that has none, outside market hours.
/// </summary>
public sealed class GlobalDailyRecorderService : MarketIntelligenceLoop
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HistoryRetry = TimeSpan.FromHours(1);

    private readonly IMarketSessionService _sessions;
    private readonly MarketIntelligenceStatus _status;

    public GlobalDailyRecorderService(IServiceScopeFactory scopes, ILogger<GlobalDailyRecorderService> logger, IOptions<MarketIntelligenceOptions> options,
        IMarketSessionService sessions, MarketIntelligenceStatus status)
        : base(scopes, logger, options.Value.GlobalDailyEnabled, nameof(MarketIntelligenceOptions.GlobalDailyEnabled))
    {
        _sessions = sessions;
        _status = status;
    }

    protected override async Task LoopAsync(CancellationToken ct)
    {
        await SleepAsync(TimeSpan.FromMinutes(2), ct);

        DateOnly? lastDaily = null;
        DateTime? lastHistoryTry = null;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            bool quiet = MarketIntelligenceSchedule.InQuietWindow(now, IsTradingDay);
            var progress = _status.Backfill(MarketIntelligenceNames.GlobalDaily);

            bool dailyDue = MarketIntelligenceSchedule.GlobalDailyDue(now, lastDaily);
            bool historyDue = false;
            if (!quiet && (progress.Requested || lastHistoryTry is null || now - lastHistoryTry >= HistoryRetry))
            {
                await InScopeAsync("history check", async sp =>
                {
                    var db = sp.GetRequiredService<TradingDbContext>();
                    int symbols = await db.MarketGlobalDaily.AsNoTracking().Select(x => x.Symbol).Distinct().CountAsync(ct);
                    historyDue = progress.Requested || symbols < GlobalMarketKeys.All.Count;
                }, ct);
            }

            if (dailyDue || historyDue)
            {
                if (historyDue)
                {
                    lastHistoryTry = now;
                    progress.Requested = false;
                    progress.State = "running";
                    progress.LastStartedUtc = now;
                }

                GlobalRecordReport? report = null;
                bool ok = await InScopeAsync("daily bars", async sp =>
                    report = await sp.GetRequiredService<GlobalMarketsRecorder>().UpdateDailyAsync(allowHistory: !quiet, ct), ct);

                if (ok && dailyDue) lastDaily = IstTime.DateOf(now);
                if (historyDue)
                {
                    progress.LastFinishedUtc = DateTime.UtcNow;
                    progress.State = report is { Failed: 0 } ? "done" : "idle";
                    progress.LastMessage = report is null ? "failed" : $"{report.Stored} bar(s) written, {report.Failed} market(s) failed";
                    progress.LastError = report?.Errors.LastOrDefault();
                }
            }

            await _status.WaitForRequestAsync(Every, ct);
        }
    }

    private bool IsTradingDay(DateOnly date) => _sessions.GetSessionInfo(IstTime.MiddayUtc(date), "NSE", "CM").IsTradingDay;
}
