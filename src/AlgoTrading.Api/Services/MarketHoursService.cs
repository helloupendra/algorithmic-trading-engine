using Microsoft.EntityFrameworkCore;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Application.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Api.Services
{
    /// <summary>
    /// Auto-shutdown at market close (15:30 IST, weekdays): stops the data ingestor
    /// and every running strategy — squaring off their open paper positions — so
    /// nothing keeps consuming the host after the session ends.
    /// </summary>
    public class MarketHoursService : BackgroundService
    {
        public const string MarketClosedReason = "Market closed (15:30 IST)";

        private readonly ILogger<MarketHoursService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IngestorSupervisor _ingestor;
        private readonly ChainPollerSupervisor _poller;
        private readonly AlertsSupervisor _alerts;
        private readonly IMarketSessionService _marketSession;
        private readonly TimeZoneInfo _istZone;
        private bool _hasShutdownToday;
        private DateTime _lastShutdownDate;
        // Set at 15:30 when the ingestor is left running for MCX; cleared at the MCX close.
        private bool _mcxFeedKeptOpen;
        public const string McxClosedReason = "MCX closed";

        public MarketHoursService(ILogger<MarketHoursService> logger, IServiceScopeFactory scopeFactory, IngestorSupervisor ingestor, ChainPollerSupervisor poller, AlertsSupervisor alerts, IMarketSessionService marketSession)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _ingestor = ingestor;
            _poller = poller;
            _alerts = alerts;
            _marketSession = marketSession;
            try
            {
                // Windows uses "India Standard Time", Linux/macOS uses "Asia/Kolkata"
                _istZone = TimeZoneInfo.FindSystemTimeZoneById(
                    OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
            }
            catch (TimeZoneNotFoundException)
            {
                _logger.LogWarning("IST timezone not found. Using UTC as fallback. Ensure tzdata is installed.");
                _istZone = TimeZoneInfo.Utc;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("MarketHoursService is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    var nowIst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, _istZone);

                    // Reset shutdown flag if it's a new day
                    if (_hasShutdownToday && nowIst.Date > _lastShutdownDate)
                    {
                        _hasShutdownToday = false;
                    }

                    // Check if it's a weekday and time is exactly 15:30 IST (3:30 PM) or shortly after
                    if (!_hasShutdownToday &&
                        nowIst.DayOfWeek != DayOfWeek.Saturday &&
                        nowIst.DayOfWeek != DayOfWeek.Sunday)
                    {
                        if (nowIst.TimeOfDay >= new TimeSpan(15, 30, 0))
                        {
                            _logger.LogInformation("Market has closed (15:30 IST). Triggering auto-shutdown of heavy processes to save system load.");

                            // Stop the data ingestor (managed, or adopted after an API restart) —
                            // unless the commodity session is still on and the list carries
                            // MCX symbols: MCX trades until 23:30, and stopping the feed at the
                            // equity close left the Commodity page frozen at 15:30 on the first
                            // evening on the server. The ingestor is then stopped at the MCX
                            // close instead (below), so the morning starts a fresh one.
                            if (await McxStillWantsTheFeedAsync(nowUtc, stoppingToken))
                            {
                                _mcxFeedKeptOpen = true;
                                _logger.LogInformation("Market close: ingestor kept running for the MCX session (MCX symbols on the recording list).");
                            }
                            else
                            {
                                var ingestorStop = await _ingestor.StopAsync(MarketClosedReason, stoppingToken);
                                _logger.LogInformation("Market close: ingestor {Outcome}.", ingestorStop.Message);
                            }

                            // The chain poller too. Nothing in a chain moves after the close,
                            // and left running it spent the evening of 2026-09-08 storing the
                            // same numbers every five seconds until FYERS answered 429
                            // ("request limit reached") — a quota the next morning needs.
                            var pollerStop = await _poller.StopAsync(MarketClosedReason, stoppingToken);
                            _logger.LogInformation("Market close: chain poller {Outcome}.", pollerStop.Message);

                            // Stop all running strategies, squaring off their open positions.
                            using (var scope = _scopeFactory.CreateScope())
                            {
                                var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
                                var stopped = await control.StopAllAsync(MarketClosedReason, flatten: true, by: "market-hours", stoppingToken);
                                _logger.LogInformation("Market close: stopped {Count} strategy run(s).", stopped);
                            }

                            // The alerter too: it watches the same closed market, and left
                            // running it sat "waiting for ticks" until the next reboot.
                            var alerterStop = await _alerts.StopAsync(MarketClosedReason, stoppingToken);
                            _logger.LogInformation("Market close: alerter {Outcome}.", alerterStop.WasRunning ? "stopped" : "was not running");

                            _hasShutdownToday = true;
                            _lastShutdownDate = nowIst.Date;

                            _logger.LogInformation("Auto-shutdown completed successfully.");
                        }
                    }

                    // The MCX close: the feed that stayed up for commodities is stopped
                    // once that session ends, so no ingestor lives through the night on a
                    // token that expires at 06:00 — market-open would otherwise find it
                    // "already running" and leave it deaf all day.
                    if (_mcxFeedKeptOpen && !_marketSession.IsMarketOpen(nowUtc, "MCX", "COM"))
                    {
                        var ingestorStop = await _ingestor.StopAsync(McxClosedReason, stoppingToken);
                        _logger.LogInformation("MCX close: ingestor {Outcome}.", ingestorStop.Message);
                        _mcxFeedKeptOpen = false;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred in MarketHoursService check loop.");
                }

                // Check every 1 minute
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("MarketHoursService is stopping.");
        }

        /// <summary>
        /// True when the commodity session is open and the recording list holds
        /// an active MCX symbol — the one case the equity close must not take
        /// the tick feed down with it.
        /// </summary>
        private async Task<bool> McxStillWantsTheFeedAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            if (!_marketSession.IsMarketOpen(nowUtc, "MCX", "COM")) return false;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            return await db.LiveWatchlistItems.AsNoTracking()
                .AnyAsync(x => x.IsActive && x.Symbol.StartsWith("MCX:"), cancellationToken);
        }

    }
}
