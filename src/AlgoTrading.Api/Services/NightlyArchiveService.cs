using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Api.Services
{
    /// <summary>
    /// Runs the daily candle archive once per IST day, after the day's last
    /// session has closed, and catches up any day it missed. After an NSE
    /// trading day it then checks India VIX and fills its gaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A day is due at <c>Archive:RunAtIst</c> (23:50), or twenty minutes after
    /// the last exchange that traded that day closed, whichever is later
    /// (<see cref="ArchiveSchedule.DueAtIst"/>, the closes from the session
    /// service). MCX closes at 23:30 while New York is on daylight saving and at
    /// 23:55 while it is on standard time, so from November to March a day is
    /// archived at 00:15 the next morning. Until 1 Oct 2026 the archive ran at
    /// 23:50 all year, and in the winter MCX's last five minutes never reached
    /// the candles.
    /// </para>
    /// <para>
    /// The last archived day is kept in system_settings, so an API that was
    /// down when a day fell due — a deploy, a reboot — archives that day on its
    /// next tick rather than losing it. Days with no live bars (weekends, holidays)
    /// finish in a query and are recorded as done all the same. The broker
    /// backfill part needs the day's token, which FYERS keeps alive until
    /// 06:00 the next morning; a catch-up run after that still archives the
    /// live bars and simply reports the broker part as failed.
    /// </para>
    /// <para>
    /// A day whose archive throws (its live_bars query timing out, say) is
    /// tried again after 5 minutes, then 15, then hourly
    /// (<see cref="ArchiveSchedule.RetryAfter"/>), not every minute: an error in
    /// the log the first time, a warning at most once an hour after that. The
    /// waits are kept in memory; a success clears them.
    /// </para>
    /// <para>
    /// The India VIX check (<see cref="VixBackfillService"/>) runs right after
    /// the archive, in the same tick, when at least one day it archived was an
    /// NSE trading day in the exchange calendar: so right after a trading
    /// day's archive (23:50 IST, or 00:15 in the US winter), or, when the API
    /// missed that, right after the catch-up archives the day. It looks at
    /// the last <c>Archive:VixLookbackTradingDays</c> (20) trading days through
    /// that day, fills missing bars from the history vendors, and reports a day
    /// the forecasts still cannot use (<see cref="VixBackfillReport"/>). It runs
    /// after the day's marker is saved, so a failure there never makes the
    /// archive run twice; the next trading night's check covers the same days
    /// again. <c>Archive:VixCheckEnabled=false</c> switches it off.
    /// </para>
    /// </remarks>
    public class NightlyArchiveService : BackgroundService
    {
        public const string LastArchivedDayKey = "archive.candles.lastDay";

        /// <summary>A day that keeps failing is logged as a warning at most this often; its other tries are information.</summary>
        private static readonly TimeSpan LogFailingEvery = TimeSpan.FromHours(1);

        private readonly ILogger<NightlyArchiveService> _logger;

        // In memory, one loop: a restart forgets it, and the day is then tried at once and logged as an error again.
        private readonly Dictionary<DateOnly, FailingDay> _failing = new();
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _runAtIst;
        private readonly bool _vixCheck;
        private readonly int _vixLookbackTradingDays;

        public NightlyArchiveService(ILogger<NightlyArchiveService> logger, IServiceScopeFactory scopeFactory, IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _runAtIst = ArchiveSchedule.ParseRunAt(configuration["Archive:RunAtIst"]);
            _vixCheck = !string.Equals(configuration["Archive:VixCheckEnabled"], "false", StringComparison.OrdinalIgnoreCase);
            _vixLookbackTradingDays = int.TryParse(configuration["Archive:VixLookbackTradingDays"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
                ? Math.Min(days, VixBackfillPlan.MaxLookbackTradingDays)
                : VixBackfillPlan.DefaultLookbackTradingDays;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "NightlyArchiveService is starting (a day is archived at {RunAt} IST, or {AfterClose} min after its last exchange close when that is later; India VIX check after each NSE trading day: {VixCheck}, last {VixDays} trading days).",
                _runAtIst, ArchiveSchedule.AfterLastClose.TotalMinutes, _vixCheck ? "on" : "off", _vixLookbackTradingDays);

            // Let the API finish booting (migrations, reconcilers) before the first look.
            try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueDaysAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "NightlyArchiveService tick failed.");
                }

                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        /// <summary>When a day whose archive failed is tried next; null when it is not failing. Internal for tests.</summary>
        internal DateTime? NextTryUtc(DateOnly day) => _failing.TryGetValue(day, out var failing) ? failing.NextTryUtc : null;

        /// <summary>One tick at <paramref name="nowUtc"/>: archives the days that are due, then checks India VIX. Internal for tests.</summary>
        /// <remarks>
        /// <para>
        /// Each day is archived in a scope of its own, so in a database context of
        /// its own: the archive clears its context's change tracker after each
        /// symbol, which would otherwise also drop the marker row this tick is
        /// about to save, and a catch-up of several days no longer drags one
        /// day's candles through the next day's saves.
        /// </para>
        /// <para>
        /// A day whose archive fails waits <see cref="ArchiveSchedule.RetryAfter"/>
        /// before its next try, and the days after it wait behind it: the marker
        /// is the last day done, in order. The first failure is an error in the
        /// log; the later ones are a warning at most once an hour, so Sentinel
        /// sees one incident, not one a minute.
        /// </para>
        /// </remarks>
        internal async Task<IReadOnlyList<DateOnly>> RunDueDaysAsync(DateTime nowUtc, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var sessions = scope.ServiceProvider.GetRequiredService<IMarketSessionService>();

            var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == LastArchivedDayKey, ct);
            DateOnly? last = setting is not null && DateOnly.TryParseExact(setting.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;

            var nowIst = IstTime.ToIst(nowUtc);
            var due = ArchiveSchedule.DueDays(last, nowIst, d => ArchiveSchedule.DueAtIst(d, _runAtIst, sessions));
            foreach (var gone in _failing.Keys.Where(d => !due.Contains(d)).ToList())
                _failing.Remove(gone);

            var archived = new List<DateOnly>();
            foreach (var day in due)
            {
                if (_failing.TryGetValue(day, out var failing) && nowUtc < failing.NextTryUtc)
                    break;

                try
                {
                    CandleArchiveResult result;
                    using (var dayScope = _scopeFactory.CreateScope())
                    {
                        var archive = dayScope.ServiceProvider.GetRequiredService<IDailyCandleArchiveService>();
                        result = await archive.ArchiveDayAsync(day, includeBrokerBackfill: true, ct);
                    }

                    foreach (var line in result.BrokerBackfills)
                        _logger.LogInformation("Candle archive {Day}: {Line}", day, line);

                    if (setting is null)
                    {
                        setting = new SystemSetting { Key = LastArchivedDayKey, UpdatedBy = "NightlyArchiveService", Reason = "daily candle archive" };
                        db.SystemSettings.Add(setting);
                    }
                    setting.Value = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    setting.UpdatedUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    NoteFailure(day, nowUtc, ex);
                    break;
                }

                _failing.Remove(day);
                archived.Add(day);
            }

            if (_vixCheck && archived.Count > 0)
                await CheckVixAsync(archived, ct);
            return archived;
        }

        /// <summary>Puts a failed day on its wait, and logs it: an error the first time, then a warning at most once an hour.</summary>
        private void NoteFailure(DateOnly day, DateTime nowUtc, Exception ex)
        {
            string dayText = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!_failing.TryGetValue(day, out var failing))
            {
                failing = new FailingDay { Failures = 1, NextTryUtc = nowUtc + ArchiveSchedule.RetryAfter(1), LoggedUtc = nowUtc };
                _failing[day] = failing;
                _logger.LogError(ex, "Candle archive of {Day} failed; it is tried again at {NextTry:HH:mm} IST, then less often while it keeps failing.",
                    dayText, IstTime.ToIst(failing.NextTryUtc));
                return;
            }

            failing.Failures++;
            failing.NextTryUtc = nowUtc + ArchiveSchedule.RetryAfter(failing.Failures);
            if (nowUtc - failing.LoggedUtc >= LogFailingEvery)
            {
                failing.LoggedUtc = nowUtc;
                _logger.LogWarning(ex, "Candle archive of {Day} is still failing ({Failures} tries); next try at {NextTry:HH:mm} IST.",
                    dayText, failing.Failures, IstTime.ToIst(failing.NextTryUtc));
            }
            else
            {
                _logger.LogInformation("Candle archive of {Day} failed again ({Failures} tries): {Error}; next try at {NextTry:HH:mm} IST.",
                    dayText, failing.Failures, ex.Message, IstTime.ToIst(failing.NextTryUtc));
            }
        }

        /// <summary>A day whose archive is failing: how often, when it is tried next, and when it was last logged as a warning or an error.</summary>
        private sealed class FailingDay
        {
            public int Failures { get; set; }

            public DateTime NextTryUtc { get; set; }

            public DateTime LoggedUtc { get; set; }
        }

        /// <summary>
        /// The India VIX check after the archive: through the latest trading day
        /// just archived, or not at all after a weekend or holiday night. Its own
        /// scope, so it does not drag the archive's thousands of tracked candles
        /// through its saves. Nothing it does can fail the archive; a failure is
        /// an error in the log and a System message.
        /// </summary>
        private async Task CheckVixAsync(IReadOnlyList<DateOnly> archived, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            var notifier = services.GetRequiredService<ISystemNotifier>();
            DateOnly? through = null;
            try
            {
                var vix = services.GetRequiredService<VixBackfillService>();
                through = VixBackfillPlan.CheckThrough(archived, vix.IsTradingDay);
                if (through is null)
                {
                    _logger.LogInformation(
                        "India VIX check skipped: no NSE trading day among the days archived ({Days}).",
                        string.Join(", ", archived.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
                    return;
                }

                var result = await vix.RunAsync(through.Value, _vixLookbackTradingDays, ct);
                await VixBackfillReport.ReportAsync(result, services.GetRequiredService<IProcessSettingsStore>(), notifier, _logger, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                string day = (through ?? archived.Max()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                _logger.LogError(ex, "India VIX check through {Day} failed.", day);
                await notifier.NotifyAsync(
                    NotificationCategory.System,
                    NotificationSeverity.Error,
                    VixBackfillReport.FailedTitle,
                    VixBackfillReport.FailedMessage(through ?? archived.Max(), ex),
                    symbol: VixBackfillPlan.Symbol,
                    cancellationToken: ct);
            }
        }
    }
}
