// src/AlgoTrading.Worker.MarketData/Consumers/RedisTickConsumerService.cs
using System.Text.Json;
using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Models;
using AlgoTrading.Worker.MarketData.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AlgoTrading.Worker.MarketData.Consumers;

public class RedisTickConsumerService : BackgroundService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisStreamOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RedisTickConsumerService> _logger;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public RedisTickConsumerService(
        IConnectionMultiplexer redis,
        IOptions<RedisStreamOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<RedisTickConsumerService> logger)
    {
        _redis = redis;
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _redis.GetDatabase();

        await EnsureConsumerGroupAsync(db);

        _logger.LogInformation(
            "Redis tick consumer started. Stream={Stream}, Group={Group}, Consumer={Consumer}",
            _options.StreamName,
            _options.ConsumerGroup,
            _options.ConsumerName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entries = await db.StreamReadGroupAsync(
                    _options.StreamName,
                    _options.ConsumerGroup,
                    _options.ConsumerName,
                    ">",
                    count: _options.ReadBatchSize);

                if (entries.Length == 0)
                {
                    await Task.Delay(_options.PollDelayMs, stoppingToken);
                    continue;
                }

                var parsed = new List<(RedisValue Id, MarketTickStreamMessage Message)>();

                foreach (var entry in entries)
                {
                    var payload = entry.Values.FirstOrDefault(x => x.Name == "payload").Value;

                    if (payload.IsNullOrEmpty)
                    {
                        _logger.LogWarning("Skipping Redis tick entry {Id} because payload is empty", entry.Id);
                        // Ack poison entry so it does not block the stream forever
                        await db.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entry.Id);
                        continue;
                    }

                    try
                    {
                        var message = JsonSerializer.Deserialize<MarketTickStreamMessage>((string)payload!, _jsonOptions);
                        if (message is null)
                        {
                            _logger.LogWarning("Skipping Redis tick entry {Id} because payload deserialized to null", entry.Id);
                            await db.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entry.Id);
                            continue;
                        }

                        parsed.Add((entry.Id, message));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to deserialize Redis tick payload for entry {Id}", entry.Id);
                        // Ack poison entry to avoid infinite retry on bad JSON
                        await db.StreamAcknowledgeAsync(_options.StreamName, _options.ConsumerGroup, entry.Id);
                    }
                }

                if (parsed.Count == 0)
                    continue;

                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<ITickBatchProcessor>();

                await processor.ProcessAsync(parsed.Select(x => x.Message).ToList(), stoppingToken);

                // Ack only after successful DB processing
                await db.StreamAcknowledgeAsync(
                    _options.StreamName,
                    _options.ConsumerGroup,
                    parsed.Select(x => x.Id).ToArray());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis tick consumer loop failed");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("Redis tick consumer stopped.");
    }

    private async Task EnsureConsumerGroupAsync(IDatabase db)
    {
        try
        {
            await db.StreamCreateConsumerGroupAsync(
                _options.StreamName,
                _options.ConsumerGroup,
                "$",
                createStream: true);

            _logger.LogInformation(
                "Created Redis consumer group. Stream={Stream}, Group={Group}",
                _options.StreamName,
                _options.ConsumerGroup);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Redis consumer group already exists. Stream={Stream}, Group={Group}",
                _options.StreamName,
                _options.ConsumerGroup);
        }
    }
}