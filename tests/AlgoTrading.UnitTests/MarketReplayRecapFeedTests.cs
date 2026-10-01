using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Xunit;
using static AlgoTrading.UnitTests.MarketReplayTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The desk replay and a vendor's recap feed (TrueData's evening replay of a session) are never on together:
/// how the API knows a recap feed is running.
/// </summary>
public class MarketReplayRecapFeedTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_recap_feed_heartbeating_now_is_running()
    {
        var (feeds, locks) = Feeds(Beat("python-dhan-feed", 3), Beat("python-truedata-recap", 10));

        Assert.Equal("python-truedata-recap", await feeds.RunningAsync(default));
        Assert.Empty(locks);
    }

    [Fact]
    public async Task A_recap_heartbeat_gone_quiet_counts_only_while_the_feed_still_holds_its_lock()
    {
        // The API was down: the feed's heartbeats were refused, but it is alive and still refreshes its Redis lock.
        var (quiet, asked) = Feeds(Beat("python-truedata-recap", 300));
        Assert.Null(await quiet.RunningAsync(default));
        Assert.Equal(["feed:truedata:lock"], asked);

        var (held, _) = Feeds(new[] { "feed:truedata:lock" }, Beat("python-truedata-recap", 300));
        Assert.Equal("python-truedata-recap", await held.RunningAsync(default));
    }

    [Fact]
    public async Task A_vendor_back_on_its_live_host_is_not_a_recap_and_live_feeds_never_are()
    {
        var (back, _) = Feeds(new[] { "feed:truedata:lock" }, Beat("python-truedata-recap", 40), Beat("python-truedata-feed", 5));
        Assert.Null(await back.RunningAsync(default));

        var (live, asked) = Feeds(Beat("python-dhan-feed", 2), Beat("python-live-ingestor", 4), Beat("python-truedata-feed", 9));
        Assert.Null(await live.RunningAsync(default));
        Assert.Empty(asked);
    }

    private static LiveIngestorStatus Beat(string source, int secondsAgo) => new()
    {
        SourceName = source, Status = "Running", LastHeartbeatUtc = Now.AddSeconds(-secondsAgo), UpdatedUtc = Now.AddSeconds(-secondsAgo),
    };

    private static (HeartbeatRecapFeeds Feeds, List<string> LocksAsked) Feeds(params LiveIngestorStatus[] beats) => Feeds([], beats);

    private static (HeartbeatRecapFeeds Feeds, List<string> LocksAsked) Feeds(string[] heldLocks, params LiveIngestorStatus[] beats)
    {
        TradingDbContext db = NewDb();
        db.LiveIngestorStatuses.AddRange(beats);
        db.SaveChanges();
        var clock = new FakeClock();
        clock.Set(Now);
        var asked = new List<string>();
        var feeds = new HeartbeatRecapFeeds(db, key =>
        {
            asked.Add(key);
            return Task.FromResult(heldLocks.Contains(key));
        }, clock);
        return (feeds, asked);
    }
}
