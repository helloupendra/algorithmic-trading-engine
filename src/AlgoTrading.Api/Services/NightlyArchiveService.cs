using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
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
    /// Runs the daily candle archive once per IST day, after the last session
    /// (MCX closes at 23:30), and catches up any day it missed. After an NSE
    /// trading day it then checks India VIX and fills its gaps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last archived day is kept in system_settings, so an API that was
    /// down at 23:50 — a deploy, a reboot — archives that day on its next
    /// tick rather than losing it. Days with no live bars (weekends, holidays)
    /// finish in a query and are recorded as done all the same. The broker
    /// backfill part needs the day's token, which FYERS keeps alive until
    /// 06:00 the next morning; a catch-up run after that still archives the
    /// live bars and simply reports the broker part as failed.
    /// </para>
    /// <para>
    /// The India VIX check (<see cref="VixBackfillService"/>) runs right after
    /// the archive, in the same tick, when at least one day it archived was an
    /// NSE trading day in the exchange calendar: so at 23:50 IST on a trading
    /// day, or, when the API missed that, right after the catch-up archives
    /// the day. It looks at
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

        private readonly ILogger<NightlyArchiveService> _logger;
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
                "NightlyArchiveService is starting (runs at {RunAt} IST; India VIX check after each NSE trading day: {VixCheck}, last {VixDays} trading days).",
                _runAtIst, _vixCheck ? "on" : "off", _vixLookbackTradingDays);

            // Let the API finish booting (migrations, reconcilers) before the first look.
            try { await Task.Delay(TimeSpan.FromSeconds(90), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunDueDaysAsync(stoppingToken);
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

        private async Task RunDueDaysAsync(CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var archive = scope.ServiceProvider.GetRequiredService<IDailyCandleArchiveService>();

            var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == LastArchivedDayKey, ct);
            DateOnly? last = setting is not null && DateOnly.TryParseExact(setting.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : null;

            var nowIst = IstTime.ToIst(DateTime.UtcNow);
            var archived = new List<DateOnly>();
            foreach (var day in ArchiveSchedule.DueDays(last, nowIst, _runAtIst))
            {
                var result = await archive.ArchiveDayAsync(day, includeBrokerBackfill: true, ct);
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
                archived.Add(day);
            }

            if (_vixCheck && archived.Count > 0)
                await CheckVixAsync(archived, ct);
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
                    $"The nightly India VIX check through {day} stopped: {IncidentRedaction.Mask(ex.Message)}. "
                    + "A VIX gap may be going unfilled; the check runs again after the next trading day's archive (23:50 IST). See NightlyArchiveService in logs/api.log.",
                    symbol: VixBackfillPlan.Symbol,
                    cancellationToken: ct);
            }
        }
    }
}
