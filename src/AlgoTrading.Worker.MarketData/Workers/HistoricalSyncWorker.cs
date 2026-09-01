using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Worker.MarketData.Config;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Worker.MarketData.Workers
{
    public class HistoricalSyncWorker : BackgroundService
    {
        private readonly ILogger<HistoricalSyncWorker> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly MarketDataWorkerSettings _settings;

        public HistoricalSyncWorker(
            ILogger<HistoricalSyncWorker> logger,
            IServiceProvider serviceProvider,
            IOptions<MarketDataWorkerSettings> settings)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _settings = settings.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HistoricalSyncWorker started at: {Time}", DateTimeOffset.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();

                    var sessionStore = scope.ServiceProvider.GetRequiredService<IBrokerSessionStore>();
                    var useCase = scope.ServiceProvider.GetRequiredService<SyncHistoryUseCase>();

                    var session = await sessionStore.GetCurrentAsync(stoppingToken);

                    if (session is null || !session.IsAuthenticated)
                    {
                        _logger.LogWarning("No active FYERS broker session found. Worker will retry later.");
                    }
                    else
                    {
                        foreach (var symbol in _settings.Symbols)
                        {
                            //var fromDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(_settings.LookbackDays));
                            //var toDate = DateOnly.FromDateTime(DateTime.UtcNow);

                            int lookbackDays = Math.Abs(_settings.LookbackDays);

                            var toDate = DateOnly.FromDateTime(DateTime.UtcNow);
                            var fromDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-lookbackDays));

                            if (fromDate > toDate)
                            {
                                _logger.LogWarning(
                                    "Invalid worker date range detcted. fromDate {FromDate} was greater than toDate {ToDate}. Adjusting values.",
                                    fromDate,
                                    toDate);
                            }

                            var request = new SyncHistoryRequest
                            {
                                Symbol = symbol,
                                Resolution = _settings.Resolution,
                                DateFormat = 1,
                                FromDate = fromDate,
                                ToDate = toDate,
                                ContFlag = 1
                            };

                            _logger.LogInformation(
                                "Syncing candles for {Symbol} from {FromDate} tp {ToDate}",
                                symbol,
                                fromDate,
                                toDate);

                            var result = await useCase.ExecuteAsync(request, stoppingToken);

                            _logger.LogInformation(
                                "Fetched {Count} candles from FYERS for {Symbol}",
                                result.Count,
                                symbol);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while syncing historical market data");
                }

                await Task.Delay(TimeSpan.FromMinutes(_settings.IntervalMinutes), stoppingToken);
            }
        }
    }
}
