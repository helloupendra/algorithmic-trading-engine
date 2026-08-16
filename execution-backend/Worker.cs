using ExecutionEngine.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Threading;
using System.Threading.Tasks;

namespace ExecutionEngine
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly FyersWebSocketClient _fyersClient;
        private readonly ISubscriber _redisSubscriber;

        // Redis is now cleanly injected!
        public Worker(ILogger<Worker> logger, FyersWebSocketClient fyersClient, IConnectionMultiplexer redis)
        {
            _logger = logger;
            _fyersClient = fyersClient;
            _redisSubscriber = redis.GetSubscriber();
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Booting up Execution Engine...");
            
            string dummyAppId = "YOUR_APP_ID";
            string dummyToken = "YOUR_ACCESS_TOKEN";
            await _fyersClient.ConnectAsync(dummyAppId, dummyToken, cancellationToken);
            
            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _redisSubscriber.SubscribeAsync("trade_signals", (channel, message) =>
            {
                _logger.LogInformation("Received trade signal from Python: {Message}", message);
            });

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await _fyersClient.DisconnectAsync(cancellationToken);
            await base.StopAsync(cancellationToken);
        }
    }
}