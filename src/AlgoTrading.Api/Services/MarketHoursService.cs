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
        private readonly TimeZoneInfo _istZone;
        private bool _hasShutdownToday;
        private DateTime _lastShutdownDate;

        public MarketHoursService(ILogger<MarketHoursService> logger, IServiceScopeFactory scopeFactory, IngestorSupervisor ingestor)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _ingestor = ingestor;
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

                            // Stop the data ingestor (managed, or adopted after an API restart).
                            var ingestorStop = await _ingestor.StopAsync(MarketClosedReason, stoppingToken);
                            _logger.LogInformation("Market close: ingestor {Outcome}.", ingestorStop.Message);

                            // Stop all running strategies, squaring off their open positions.
                            using (var scope = _scopeFactory.CreateScope())
                            {
                                var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
                                var stopped = await control.StopAllAsync(MarketClosedReason, flatten: true, by: "market-hours", stoppingToken);
                                _logger.LogInformation("Market close: stopped {Count} strategy run(s).", stopped);
                            }

                            _hasShutdownToday = true;
                            _lastShutdownDate = nowIst.Date;

                            _logger.LogInformation("Auto-shutdown completed successfully.");
                        }
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
    }
}
