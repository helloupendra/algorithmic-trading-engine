using System;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Api.Controllers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Api.Services
{
    public class MarketHoursService : BackgroundService
    {
        private readonly ILogger<MarketHoursService> _logger;
        private readonly TimeZoneInfo _istZone;
        private bool _hasShutdownToday;
        private DateTime _lastShutdownDate;

        public MarketHoursService(ILogger<MarketHoursService> logger)
        {
            _logger = logger;
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
                            
                            // Stop the data ingestor
                            IngestorController.StopAll();
                            
                            // Stop all running strategies
                            StrategyController.StopAll();

                            _hasShutdownToday = true;
                            _lastShutdownDate = nowIst.Date;
                            
                            _logger.LogInformation("Auto-shutdown completed successfully.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred in MarketHoursService check loop.");
                }

                // Check every 1 minute
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }

            _logger.LogInformation("MarketHoursService is stopping.");
        }
    }
}
