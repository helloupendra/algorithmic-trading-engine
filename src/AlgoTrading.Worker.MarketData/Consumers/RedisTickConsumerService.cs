// src/AlgoTrading.Worker.MarketData/Consumers/RedisTickConsumerService.cs
using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Worker.MarketData.Consumers;

/// <summary>
/// Runs <see cref="TickStreamDrain"/> for as long as the worker lives: retries
/// and dead-letters live there, this only keeps the loop going.
/// </summary>
public class RedisTickConsumerService : BackgroundService
{
    private readonly ITickStream _stream;
    private readonly RedisStreamOptions _options;
    private readonly ILogger<RedisTickConsumerService> _logger;
    private readonly TickStreamDrain _drain;

    public RedisTickConsumerService(
        ITickStream stream,
        IOptions<RedisStreamOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<RedisTickConsumerService> logger)
    {
        _stream = stream;
        _options = options.Value;
        _logger = logger;

        _drain = new TickStreamDrain(
            stream,
            async (messages, redelivered, cancellationToken) =>
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ITickBatchProcessor>();
                await processor.ProcessAsync(messages, cancellationToken, redelivered);
            },
            _options,
            logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _stream.EnsureGroupAsync();

        _logger.LogInformation(
            "Redis tick consumer started. Stream={Stream}, Group={Group}, Consumer={Consumer}, " +
            "retry after {IdleMs} ms, dead-letter after {Max} deliveries to {DeadLetter}, latest quotes {Projection}",
            _options.StreamName,
            _options.ConsumerGroup,
            _options.ConsumerName,
            _options.ClaimMinIdleMs,
            _options.MaxDeliveries,
            _options.DeadLetterStreamName,
            _options.ProjectLatestQuotes ? "written here" : "left to the API");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var handled = await _drain.RunOnceAsync(stoppingToken);

                if (_drain.LastPassFailed)
                {
                    await Task.Delay(1000, stoppingToken);
                }
                else if (handled == 0)
                {
                    await Task.Delay(_options.PollDelayMs, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Redis itself failed (a read, a claim or an ack). Nothing is
                // lost by that: unacknowledged entries stay pending and the
                // next pass claims them back.
                _logger.LogError(ex, "Redis tick consumer loop failed");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation(
            "Redis tick consumer stopped. Stored {Stored}, dead-lettered {DeadLettered}, lost {Lost} since start.",
            _drain.Stored, _drain.DeadLettered, _drain.Lost);
    }
}
