using System;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Api.Services
{
    /// <summary>
    /// Keeps the market-pulse universe on the live feed: once shortly after
    /// boot, then hourly, so a commodity contract that expired overnight is
    /// replaced by the next one before the MCX open.
    /// </summary>
    public class MarketPulseSubscriptionService : BackgroundService
    {
        private readonly ILogger<MarketPulseSubscriptionService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public MarketPulseSubscriptionService(ILogger<MarketPulseSubscriptionService> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
            catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var pulse = scope.ServiceProvider.GetRequiredService<IMarketPulseService>();
                    await pulse.EnsureSubscribedAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Market pulse subscription check failed.");
                }

                try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
