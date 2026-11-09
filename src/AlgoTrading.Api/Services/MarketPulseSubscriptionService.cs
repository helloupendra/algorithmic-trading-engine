using System;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Api.Services
{
    /// <summary>
    /// Keeps the market-pulse universe on the live feed: once shortly after
    /// boot, then hourly, so a commodity contract that expired overnight is
    /// replaced by the next one before the MCX open. Missing reference closes
    /// are refreshed once a minute through the shared broker rate gate.
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

            var nextSubscriptionCheck = DateTime.MinValue;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var pulse = scope.ServiceProvider.GetRequiredService<IMarketPulseService>();
                    if (DateTime.UtcNow >= nextSubscriptionCheck)
                    {
                        await pulse.EnsureSubscribedAsync(stoppingToken);
                        nextSubscriptionCheck = DateTime.UtcNow.AddHours(1);
                    }
                    // A missing websocket previous-close packet must not leave
                    // the overview blank. Refresh in the background, never on
                    // a user's request path, sharing Dhan's REST pacing gate.
                    var reader = scope.ServiceProvider.GetService<DhanReferencePriceReader>();
                    if (reader is not null)
                    {
                        var overview = await pulse.GetAsync(stoppingToken);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        await reader.RefreshAsync(overview.Groups.SelectMany(g => g.Items).Select(i => i.Symbol).ToArray(), timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Market pulse subscription/reference-price refresh failed.");
                }

                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
