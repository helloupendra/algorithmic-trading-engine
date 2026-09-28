using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The feed has sent its backlog and the ticks it shed in every heartbeat since
/// 2026-09-07; the heartbeat DTO had no such properties, so binding dropped them
/// and nothing could see a feed shedding ticks. They now reach /metrics.
/// </summary>
public class FeedHeartbeatMetricsTests
{
    [Fact]
    public void The_counts_the_feed_sends_bind_to_the_heartbeat()
    {
        // Exactly the keys core/live/feed_runner.py send_heartbeat posts.
        const string body = """
            {"sourceName": "python-dhan-feed", "status": "Running", "feedKey": "dhan",
             "queueDepth": 12, "ticksDropped": 3, "ticksNotStored": 150, "ticksRejected": 2,
             "greeksUnavailable": "No module named 'vollib'"}
            """;

        var request = JsonSerializer.Deserialize<UpsertHeartbeatRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(12, request.QueueDepth);
        Assert.Equal(3, request.TicksDropped);
        Assert.Equal(150, request.TicksNotStored);
        Assert.Equal(2, request.TicksRejected);
        Assert.Equal("No module named 'vollib'", request.GreeksUnavailable);
    }

    [Fact]
    public async Task A_heartbeat_puts_the_feeds_counts_on_the_metrics_page()
    {
        var source = $"test-feed-{Guid.NewGuid():N}";
        var service = Service();

        await service.UpsertHeartbeatAsync(new UpsertHeartbeatRequest
        {
            SourceName = "python-test-feed",
            FeedKey = source,
            QueueDepth = 40,
            TicksDropped = 7,
            TicksNotStored = 150,
            TicksRejected = 1,
            GreeksUnavailable = "No module named 'vollib'",
        });

        Assert.Equal(40, FeedHeartbeatMetrics.QueueDepth.WithLabels(source).Value);
        Assert.Equal(7, FeedHeartbeatMetrics.TicksDropped.WithLabels(source).Value);
        Assert.Equal(150, FeedHeartbeatMetrics.TicksNotStored.WithLabels(source).Value);
        Assert.Equal(1, FeedHeartbeatMetrics.TicksRejected.WithLabels(source).Value);
        Assert.Equal(0, FeedHeartbeatMetrics.GreeksAvailable.WithLabels(source).Value);

        await service.UpsertHeartbeatAsync(new UpsertHeartbeatRequest
        {
            SourceName = "python-test-feed", FeedKey = source, QueueDepth = 0, GreeksUnavailable = "",
        });

        Assert.Equal(0, FeedHeartbeatMetrics.QueueDepth.WithLabels(source).Value);
        Assert.Equal(1, FeedHeartbeatMetrics.GreeksAvailable.WithLabels(source).Value);
        // Not sent this time: the last figure stands rather than reading as zero.
        Assert.Equal(150, FeedHeartbeatMetrics.TicksNotStored.WithLabels(source).Value);
    }

    private static LiveDataService Service()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"feed-heartbeat-{Guid.NewGuid():N}")
            .Options;
        return new LiveDataService(new TradingDbContext(options), new NoCatalog(), new MarketSessionService(new OpenCalendar()));
    }

    private sealed class NoCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => Array.Empty<ProviderDescriptor>();
        public ProviderDescriptor? Find(string providerKey) => null;
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public bool IsLoaded => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
