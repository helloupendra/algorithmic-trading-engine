using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
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
    /// (MCX closes at 23:30), and catches up any day it missed.
    /// </summary>
    /// <remarks>
    /// The last archived day is kept in system_settings, so an API that was
    /// down at 23:50 — a deploy, a reboot — archives that day on its next
    /// tick rather than losing it. Days with no live bars (weekends, holidays)
    /// finish in a query and are recorded as done all the same. The broker
    /// backfill part needs the day's token, which FYERS keeps alive until
    /// 06:00 the next morning; a catch-up run after that still archives the
    /// live bars and simply reports the broker part as failed.
    /// </remarks>
    public class NightlyArchiveService : BackgroundService
    {
        public const string LastArchivedDayKey = "archive.candles.lastDay";

        private readonly ILogger<NightlyArchiveService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeSpan _runAtIst;

        public NightlyArchiveService(ILogger<NightlyArchiveService> logger, IServiceScopeFactory scopeFactory, IConfiguration configuration)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _runAtIst = ArchiveSchedule.ParseRunAt(configuration["Archive:RunAtIst"]);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("NightlyArchiveService is starting (runs at {RunAt} IST).", _runAtIst);

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
            }
        }
    }
}
