using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// NSE's daily archives for market intelligence: the evening's breadth, and
/// the history of breadth and participant-wise open interest back to 2020.
/// </summary>
/// <remarks>
/// <para>
/// One loop does all of it, so the evening run and the backfills never fetch
/// the same day at once, and every request goes through the shared
/// <see cref="NseRequestPacer"/> (about one a second, together with the
/// market-factor sync).
/// </para>
/// <para>
/// Evening (18:00–23:30 IST, every 30 minutes): the last ten sessions' breadth,
/// fetching only what is missing, so the day is stored once NSE posts it.
/// </para>
/// <para>
/// History: outside 09:00–15:40 IST on trading days, the backfills run until
/// every session since 2020 is stored or known to have no file. A backfill in
/// progress stops at 09:00 and carries on after 15:40, and gives way to the
/// evening run whenever that is due. Participant OI stops short of the last
/// 40 sessions, which <see cref="MarketFactorsSyncService"/> already keeps.
/// A finished backfill is checked again every six hours (a few database reads)
/// in case a day failed. An admin can ask for a run now
/// (<c>POST api/MarketIntelligence/backfill/{dataset}</c>); it still waits for
/// the market to close.
/// </para>
/// </remarks>
public sealed class MarketIntelligenceBackfillService : MarketIntelligenceLoop
{
    private static readonly TimeSpan EveningEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RecheckDone = TimeSpan.FromHours(6);
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(5);
    private const int EveningLookbackDays = 16;
    private const int ParticipantEveningSessions = 40;

    private readonly IMarketSessionService _sessions;
    private readonly IMarketCalendar _calendar;
    private readonly MarketIntelligenceStatus _status;
    private readonly MarketIntelligenceOptions _options;
    private DateTime? _lastEvening;

    public MarketIntelligenceBackfillService(IServiceScopeFactory scopes, ILogger<MarketIntelligenceBackfillService> logger,
        IOptions<MarketIntelligenceOptions> options, IMarketSessionService sessions, IMarketCalendar calendar, MarketIntelligenceStatus status)
        : base(scopes, logger, options.Value.BreadthEnabled || options.Value.BackfillEnabled,
            $"{nameof(MarketIntelligenceOptions.BreadthEnabled)} and {nameof(MarketIntelligenceOptions.BackfillEnabled)}")
    {
        _sessions = sessions;
        _calendar = calendar;
        _status = status;
        _options = options.Value;
    }

    protected override async Task LoopAsync(CancellationToken ct)
    {
        // After migrations, the calendar load and the market-factor sync's first look.
        await SleepAsync(TimeSpan.FromMinutes(4), ct);

        var lastHistoryRun = new Dictionary<string, DateTime>();
        while (!ct.IsCancellationRequested)
        {
            if (_options.BreadthEnabled && EveningDue(DateTime.UtcNow)) await RunEveningAsync(ct);

            if (_options.BackfillEnabled)
            {
                foreach (string dataset in new[] { MarketIntelligenceNames.ParticipantOi, MarketIntelligenceNames.Breadth })
                {
                    var now = DateTime.UtcNow;
                    if (Quiet(now)) break;

                    var progress = _status.Backfill(dataset);
                    bool due = progress.Requested || progress.State != "done"
                        || !lastHistoryRun.TryGetValue(dataset, out var last) || now - last >= RecheckDone;
                    if (!due) continue;

                    progress.Requested = false;
                    lastHistoryRun[dataset] = now;
                    await RunHistoryAsync(dataset, ct);

                    if (_options.BreadthEnabled && EveningDue(DateTime.UtcNow)) await RunEveningAsync(ct);
                }
            }

            await _status.WaitForRequestAsync(Look, ct);
        }
    }

    private async Task RunEveningAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _lastEvening = now;
        var health = _status.Recorder(MarketIntelligenceNames.Breadth);
        health.Attempted(now);

        await InScopeAsync("evening breadth", async sp =>
        {
            var dataset = MarketIntelligenceDatasets.Breadth(sp.GetRequiredService<BreadthRecorder>());
            var to = MarketIntelligenceDatasets.LatestPublishedDay(now);
            var report = await sp.GetRequiredService<DailyBackfillRunner>()
                .RunAsync(dataset, to.AddDays(-EveningLookbackDays), to, () => true, trackProgress: false, ct);

            health.Finished(DateTime.UtcNow, report.Describe(), report.Failed > 0 ? report.LastError : null);
            if (report.Failed > 0) Logger.LogWarning("Evening breadth: {Report}", report.Describe());
            else if (report.Stored > 0) Logger.LogInformation("Evening breadth: {Report}", report.Describe());
        }, ct);
    }

    private async Task RunHistoryAsync(string name, CancellationToken ct)
    {
        await InScopeAsync($"{name} backfill", async sp =>
        {
            var now = DateTime.UtcNow;
            DailyDataset dataset;
            DateOnly to;
            if (name == MarketIntelligenceNames.ParticipantOi)
            {
                var sync = sp.GetRequiredService<MarketFactorsSync>();
                sync.Pace = TimeSpan.Zero;   // the shared pacer spaces the requests
                dataset = MarketIntelligenceDatasets.ParticipantOi(sync);
                var recent = MarketFactorsSync.RecentSessions(now, ParticipantEveningSessions,
                    d => _calendar.HolidayOn("NSE", d) is { Closure: MarketClosure.FullDay });
                to = recent.Count > 0 ? recent[^1].AddDays(-1) : MarketIntelligenceDatasets.LatestPublishedDay(now);
            }
            else
            {
                dataset = MarketIntelligenceDatasets.Breadth(sp.GetRequiredService<BreadthRecorder>());
                to = MarketIntelligenceDatasets.LatestPublishedDay(now);
            }

            await sp.GetRequiredService<DailyBackfillRunner>().RunAsync(dataset, dataset.Earliest, to,
                mayContinue: () => !ct.IsCancellationRequested && !Quiet(DateTime.UtcNow) && !(_options.BreadthEnabled && EveningDue(DateTime.UtcNow)),
                trackProgress: true, ct);
        }, ct);
    }

    private bool EveningDue(DateTime nowUtc) =>
        MarketIntelligenceSchedule.InEveningWindow(nowUtc) && (_lastEvening is null || nowUtc - _lastEvening >= EveningEvery);

    private bool Quiet(DateTime nowUtc) => MarketIntelligenceSchedule.InQuietWindow(nowUtc, IsTradingDay);

    private bool IsTradingDay(DateOnly date) => _sessions.GetSessionInfo(IstTime.MiddayUtc(date), "NSE", "CM").IsTradingDay;
}
