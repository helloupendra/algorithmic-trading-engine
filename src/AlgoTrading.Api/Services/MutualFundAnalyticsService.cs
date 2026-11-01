using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using AlgoTrading.Infrastructure.Services.MutualFunds;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The fund report's nightly jobs, one after another in one loop so they never run at once: the benchmark sync
/// (NSE total-return indices, and each index's history the first time), the TER sync, and the category ranking.
/// The schedule is <see cref="MutualFundAnalyticsSchedule"/>.
/// </summary>
/// <remarks>
/// They run after the 23:30 IST NAV sync (23:45 IST), never 09:00–15:40 IST on trading days (15:55 from 7 Dec 2026),
/// and never while the NAV sync is running. An admin's request (<c>POST api/MutualFunds/analytics/{job}</c>) is kept
/// in <c>system_settings</c> and runs at the next look outside those hours. The ranking reads a few days of NAVs for
/// every scheme, not whole histories, so it is light enough for the small server. Set
/// <c>MutualFunds:AnalyticsEnabled</c> to false to switch all three off.
/// </remarks>
public sealed class MutualFundAnalyticsService : BackgroundService
{
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MutualFundAnalyticsService> _logger;
    private readonly IMarketSessionService _sessions;
    private readonly MutualFundStatus _status;
    private readonly bool _enabled;

    public MutualFundAnalyticsService(IServiceScopeFactory scopes, ILogger<MutualFundAnalyticsService> logger, IMarketSessionService sessions,
        MutualFundStatus status, IOptions<MutualFundsOptions> options)
    {
        _scopes = scopes;
        _logger = logger;
        _sessions = sessions;
        _status = status;
        _enabled = options.Value.AnalyticsEnabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("MutualFundAnalyticsService is switched off (MutualFunds:AnalyticsEnabled=false).");
            return;
        }

        try
        {
            // After the NAV sync's and the backfill's first looks.
            await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await LookAsync(stoppingToken);
                await _status.WaitAnalyticsAsync(Look, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private bool Quiet(DateTime nowUtc) =>
        MarketIntelligenceSchedule.InQuietWindow(nowUtc, d => _sessions.GetSessionInfo(IstTime.MiddayUtc(d), "NSE", "CM").IsTradingDay);

    private async Task LookAsync(CancellationToken ct)
    {
        if (Quiet(DateTime.UtcNow) || _status.SyncRunning) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var services = scope.ServiceProvider;
            var settings = services.GetRequiredService<IProcessSettingsStore>();
            var state = await MutualFundAnalyticsSettings.ReadAsync(settings, ct);
            var sync = services.GetRequiredService<MutualFundNavSync>();
            var expected = MutualFundSchedule.ExpectedNavDate(DateTime.UtcNow, sync.IsBusinessDay);

            string? Trigger(string job, string? due) => state.Requested.Contains(job) ? "requested" : due;

            if (Trigger(MutualFundJobs.Benchmarks, MutualFundAnalyticsSchedule.NightlyDue(DateTime.UtcNow, state.Benchmarks, expected)) is { } benchmarks)
            {
                _status.AnalyticsNow = MutualFundJobs.Benchmarks;
                var run = await services.GetRequiredService<MutualFundBenchmarkSync>()
                    .RunAsync(benchmarks, () => !ct.IsCancellationRequested && !Quiet(DateTime.UtcNow), ct);
                state = await Save(settings, MutualFundJobs.Benchmarks, s => s.Benchmarks = run, ct);
            }

            if (Quiet(DateTime.UtcNow)) return;
            if (Trigger(MutualFundJobs.Ter, MutualFundAnalyticsSchedule.NightlyDue(DateTime.UtcNow, state.Ter)) is { } ter)
            {
                _status.AnalyticsNow = MutualFundJobs.Ter;
                var run = await services.GetRequiredService<MutualFundTerSync>().RunAsync(ter, ct);
                state = await Save(settings, MutualFundJobs.Ter, s => s.Ter = run, ct);
            }

            if (Quiet(DateTime.UtcNow) || _status.SyncRunning) return;
            var latestFull = await sync.FreshnessAsync(ct);
            var backfill = await MutualFundSettings.BackfillAsync(settings, ct);
            var statsDue = MutualFundAnalyticsSchedule.StatsDue(DateTime.UtcNow, state.Stats, latestFull.LatestFull, backfill.Cursor, state.StatsBackfillCursor);
            if (Trigger(MutualFundJobs.Stats, statsDue) is { } stats)
            {
                _status.AnalyticsNow = MutualFundJobs.Stats;
                var run = await services.GetRequiredService<MutualFundStatsJob>().RunAsync(stats, ct);
                await Save(settings, MutualFundJobs.Stats, s =>
                {
                    s.Stats = run;
                    s.StatsBackfillCursor = backfill.Cursor;
                }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Each job reports its own source and database faults; this is the settings store or the scope failing.
            _logger.LogWarning(ex, "Mutual fund report jobs could not run: {Error}", ex.Message);
        }
        finally
        {
            _status.AnalyticsNow = null;
        }
    }

    /// <summary>Records a run and clears the job's request, on a fresh read so a request made meanwhile is kept for the others.</summary>
    private static Task<MutualFundAnalyticsState> Save(IProcessSettingsStore settings, string job, Action<MutualFundAnalyticsState> change, CancellationToken ct) =>
        MutualFundAnalyticsSettings.UpdateAsync(settings, s =>
        {
            change(s);
            s.Requested.Remove(job);
        }, ct);
}
