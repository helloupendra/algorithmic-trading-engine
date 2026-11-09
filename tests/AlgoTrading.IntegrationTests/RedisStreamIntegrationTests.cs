using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Consumers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AlgoTrading.IntegrationTests;

public sealed class RedisStreamIntegrationTests
{
    [RedisFact]
    public async Task Restart_reclaims_failed_delivery_and_poison_messages_reach_dead_letter_stream()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("OPENFNO_TEST_REDIS")!);
        var prefix = $"test:{Guid.NewGuid():N}";
        var options = new RedisStreamOptions { StreamName = prefix, ConsumerGroup = prefix, ConsumerName = "first",
            DeadLetterStreamName = prefix + ":dead", ClaimMinIdleMs = 0, MaxDeliveries = 3 };
        var db = redis.GetDatabase();
        try
        {
            RedisTickStream Stream() => new(redis, Options.Create(options), NullLogger<RedisTickStream>.Instance);
            var first = Stream();
            await first.EnsureGroupAsync();
            await db.StreamAddAsync(prefix, "payload", "{\"symbol\":\"NSE:TEST-EQ\",\"lastTradedPrice\":100}");
            var failed = new TickStreamDrain(first, (_, _, _) => throw new IOException("Database unavailable"), options, NullLogger.Instance);
            await failed.RunOnceAsync(default);
            Assert.True(failed.LastPassFailed);
            Assert.Equal(1, (await db.StreamPendingAsync(prefix, prefix)).PendingMessageCount);
            options.ConsumerName = "replacement";
            var second = Stream();
            await second.EnsureGroupAsync(); // Existing group must retain its pending entries.
            var recovered = new List<string>();
            var drain = new TickStreamDrain(second, (ticks, redelivered, _) => {
                Assert.True(redelivered);
                recovered.AddRange(ticks.Select(t => t.Symbol));
                return Task.CompletedTask;
            }, options, NullLogger.Instance);
            await drain.RunOnceAsync(default);
            Assert.Equal(new[] { "NSE:TEST-EQ" }, recovered);
            Assert.Equal(0, (await db.StreamPendingAsync(prefix, prefix)).PendingMessageCount);
            await drain.RunOnceAsync(default);
            Assert.Single(recovered);
            var poison = await db.StreamAddAsync(prefix, "payload", "not-json");
            await drain.RunOnceAsync(default);
            var dead = Assert.Single(await db.StreamRangeAsync(options.DeadLetterStreamName));
            Assert.Contains(dead.Values, v => v.Name == "sourceId" && v.Value == poison);
            Assert.Contains(dead.Values, v => v.Name == "payload" && v.Value == "not-json");
            Assert.Equal(0, (await db.StreamPendingAsync(prefix, prefix)).PendingMessageCount);
        }
        finally { await db.KeyDeleteAsync(new RedisKey[] { prefix, options.DeadLetterStreamName }); }
    }
}
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENFNO_TEST_REDIS")))
            Skip = "Set OPENFNO_TEST_REDIS to a disposable Redis server.";
    }
}
