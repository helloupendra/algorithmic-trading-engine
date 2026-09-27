using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Runs the news scorer, <c>python -m analysis news-score</c>, every ten
/// minutes from 06:00 to 23:30 IST, except 08:40–15:40 on a trading day. It
/// fills the scoring columns of the headlines and filings nobody has scored
/// yet (<see cref="IScoredText"/>).
/// </summary>
/// <remarks>
/// <para>
/// The scorer is a local model on the CPU (no paid API, no key) that needs
/// over a gigabyte while it runs, so it keeps out of the session and the
/// runners' start (<see cref="QuietFrom"/>): the box runs the strategies then.
/// The 08:50 forecast uses the scores that exist by then (a headline of 08:41
/// counts as not yet scored), and what arrives during the session is scored
/// from 15:40. It also
/// runs below normal priority. One run at a
/// time: the loop waits for each run to end before it counts ten minutes to
/// the next, so a slow run is never joined by a second.
/// </para>
/// <para>
/// Until <c>analysis/news.py</c> exists the scheduler waits and says so once,
/// rather than failing every ten minutes on a command that is not there yet.
/// A run that fails is logged every time and sent to the System channel at
/// most once a day (<see cref="SystemSettingKeys.NewsScoringLastFailureNotice"/>):
/// the unscored rows keep until the scorer is fixed, so there is nothing
/// urgent to repeat. Set <c>MarketIntelligence:NewsScoringEnabled</c> to
/// false to switch it off.
/// </para>
/// </remarks>
public sealed class NewsScoringScheduler : MarketIntelligenceLoop
{
    /// <summary>The command, as the Python CLI defines it.</summary>
    public static readonly IReadOnlyList<string> Arguments = ["-m", "analysis", "news-score"];

    /// <summary>Generous for a CPU model over a backlog; the next run starts only after this one ends.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    private readonly ForecastJobRunner _runner;
    private readonly MarketIntelligenceStatus _status;
    private readonly IMarketSessionService _sessions;

    public NewsScoringScheduler(IServiceScopeFactory scopes, ILogger<NewsScoringScheduler> logger, IOptions<MarketIntelligenceOptions> options,
        ForecastJobRunner runner, MarketIntelligenceStatus status, IMarketSessionService sessions)
        : base(scopes, logger, options.Value.NewsScoringEnabled, nameof(MarketIntelligenceOptions.NewsScoringEnabled))
    {
        _runner = runner;
        _status = status;
        _sessions = sessions;
    }

    private bool IsTradingDay(DateOnly date) => _sessions.GetSessionInfo(IstTime.MiddayUtc(date), "NSE", "CM").IsTradingDay;

    /// <summary>
    /// On a trading day the scorer also keeps out of the twenty minutes before
    /// the session: the morning job starts every strategy runner at 08:45, and
    /// on the server a scoring run peaked at 1.25 GB (27 Sep).
    /// </summary>
    public static readonly TimeSpan QuietFrom = new(8, 40, 0);

    /// <summary>
    /// Whether a run is due: inside 06:00–23:30 IST, outside 08:40–15:40 on a
    /// trading day, and ten minutes after the last one started.
    /// </summary>
    public static bool Due(DateTime nowUtc, DateTime? lastStartedUtc, Func<DateOnly, bool> isTradingDay) =>
        MarketIntelligenceSchedule.InFilingsWindow(nowUtc)
        && !InSessionQuiet(nowUtc, isTradingDay)
        && (lastStartedUtc is null || nowUtc - lastStartedUtc >= MarketIntelligenceSchedule.ScoringEvery);

    private static bool InSessionQuiet(DateTime nowUtc, Func<DateOnly, bool> isTradingDay)
    {
        var time = IstTime.ToIst(nowUtc).TimeOfDay;
        return time >= QuietFrom && time < MarketIntelligenceSchedule.QuietUntil && isTradingDay(IstTime.DateOf(nowUtc));
    }

    /// <summary>Whether a failure today should go to the System channel: only the first of the IST day.</summary>
    public static bool ShouldNotify(DateTime nowUtc, string? lastNoticeDay) =>
        !string.Equals(lastNoticeDay?.Trim(), IstTime.DateString(nowUtc), StringComparison.Ordinal);

    protected override async Task LoopAsync(CancellationToken ct)
    {
        await SleepAsync(TimeSpan.FromMinutes(1), ct);

        var health = _status.Recorder(MarketIntelligenceNames.NewsScoring);
        string scorer = Path.Combine(_runner.EngineDirectory, "analysis", "news.py");
        bool saidWaiting = false;
        DateTime? lastStarted = null;

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            if (Due(now, lastStarted, IsTradingDay))
            {
                if (!File.Exists(scorer))
                {
                    if (!saidWaiting) Logger.LogInformation("News scoring waits for {Path}, which is not there yet.", scorer);
                    saidWaiting = true;
                    health.Finished(now, $"waiting: {scorer} is not there yet");
                }
                else
                {
                    saidWaiting = false;
                    lastStarted = now;
                    health.Attempted(now);
                    var result = await _runner.RunAsync("news-score", Arguments, Timeout, lowPriority: true, ct);
                    if (result.Succeeded)
                    {
                        health.Finished(DateTime.UtcNow, result.LastLine ?? "done");
                    }
                    else
                    {
                        health.Finished(DateTime.UtcNow, "failed", result.Failure);
                        await NotifyOnceADayAsync(result, ct);
                    }
                }
            }

            await SleepAsync(CheckEvery, ct);
        }
    }

    private async Task NotifyOnceADayAsync(ForecastRunResult result, CancellationToken ct)
    {
        await InScopeAsync("failure notice", async sp =>
        {
            var store = sp.GetRequiredService<IProcessSettingsStore>();
            var now = DateTime.UtcNow;
            if (!ShouldNotify(now, await store.GetAsync(SystemSettingKeys.NewsScoringLastFailureNotice, ct))) return;

            // Recorded before sending, so a notifier that throws cannot turn into one message per run.
            await store.SetAsync(SystemSettingKeys.NewsScoringLastFailureNotice, IstTime.DateString(now), nameof(NewsScoringScheduler), ct);
            await sp.GetRequiredService<ISystemNotifier>().NotifyAsync(
                NotificationCategory.System,
                NotificationSeverity.Warning,
                "News scoring failed",
                $"python {string.Join(' ', Arguments)} {result.Failure} at {IstTime.ToIst(now).ToString("HH:mm", CultureInfo.InvariantCulture)} IST. "
                + "It runs again every 10 minutes; headlines keep being recorded and wait unscored. This is the only message today."
                + (result.LastLine is { } line ? $" Last output: {line}" : string.Empty),
                cancellationToken: ct);
        }, ct);
    }
}
