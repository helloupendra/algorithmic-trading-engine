using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Which runner feed-stall reports reach Telegram. On 25 Sep 2026, 11 feed
/// blips made 26 stall and 26 recovery alerts each — 572, of which Telegram
/// refused 350 — and most described a blip the feed's own 120 s reconnect was
/// already healing. Every report stays on the record; the channel gets one
/// stall per underlying per ten minutes, after 180 s, and its recovery.
/// </summary>
public class FeedStallAlertTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_stall_under_180s_is_recorded_not_sent()
    {
        var gate = new FeedStallAlertGate(new Clock(Start));
        Assert.False(gate.ShouldSendStall("NIFTY", 95));
        Assert.False(gate.ShouldSendStall("NIFTY", 179));
        Assert.True(gate.ShouldSendStall("NIFTY", 180));
    }

    [Fact]
    public void One_stall_per_underlying_in_ten_minutes()
    {
        var clock = new Clock(Start);
        var gate = new FeedStallAlertGate(clock);

        Assert.True(gate.ShouldSendStall("NIFTY", 185));
        Assert.False(gate.ShouldSendStall("NIFTY", 190));   // the next run on NIFTY
        Assert.False(gate.ShouldSendStall(" nifty ", 190));

        // 24 Sep, 15:18: SENSEX alone went quiet — its own message.
        Assert.True(gate.ShouldSendStall("SENSEX", 185));

        clock.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        Assert.False(gate.ShouldSendStall("NIFTY", 785));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(gate.ShouldSendStall("NIFTY", 786));
    }

    [Fact]
    public void A_recovery_is_sent_only_for_a_stall_that_was_sent_and_only_once()
    {
        var gate = new FeedStallAlertGate(new Clock(Start));

        // A 95 s blip: stall recorded, so its recovery is recorded too.
        Assert.False(gate.ShouldSendStall("BANKNIFTY", 95));
        Assert.False(gate.ShouldSendRecovery("BANKNIFTY"));

        Assert.True(gate.ShouldSendStall("BANKNIFTY", 185));
        Assert.True(gate.ShouldSendRecovery("BANKNIFTY"));   // the first run back speaks for the rest
        Assert.False(gate.ShouldSendRecovery("BANKNIFTY"));
    }

    [Fact]
    public void Twenty_six_runs_over_three_underlyings_make_three_stalls_and_three_recoveries()
    {
        var clock = new Clock(Start);
        var gate = new FeedStallAlertGate(clock);
        var underlyings = new[] { "NIFTY", "BANKNIFTY", "SENSEX" };
        var runs = Enumerable.Range(0, 26).Select(i => underlyings[i % 3]).ToList();
        int sent = 0;

        // Each run: stalled at ~95 s, again at ~185 s (core/feed_watchdog.py), then recovered.
        foreach (var u in runs) sent += gate.ShouldSendStall(u, 95) ? 1 : 0;
        clock.Advance(TimeSpan.FromSeconds(90));
        foreach (var u in runs) sent += gate.ShouldSendStall(u, 185) ? 1 : 0;
        clock.Advance(TimeSpan.FromSeconds(30));
        foreach (var u in runs) sent += gate.ShouldSendRecovery(u) ? 1 : 0;

        Assert.Equal(6, sent);
    }

    [Fact]
    public async Task ReportFeedHealth_sends_one_stall_and_one_recovery_per_underlying_and_records_every_report()
    {
        // 25 Sep, one blip: 26 runs on three underlyings, each reporting a stall
        // at 95 s, again at 185 s, and a recovery.
        var clock = new Clock(Start);
        var gate = new FeedStallAlertGate(clock);
        var notifier = new Recorder();
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"feed-stall-{Guid.NewGuid():N}").Options);
        var underlyings = new[] { "NIFTY", "BANKNIFTY", "SENSEX" };
        for (long id = 1; id <= 26; id++)
        {
            db.SimulationRuns.Add(new SimulationRun
            {
                Id = id, UserId = 1, Mode = "LivePaper", Status = "Running",
                StrategyName = "Ghost", Symbol = underlyings[id % 3],
            });
        }
        await db.SaveChangesAsync();
        var controller = Controller(db, notifier);

        async Task Report(bool stalled, int seconds)
        {
            for (long id = 1; id <= 26; id++)
            {
                var result = await controller.ReportFeedHealth(id,
                    new RunnerFeedHealthRequest { IsStalled = stalled, SilentSeconds = seconds, Underlying = underlyings[id % 3] },
                    gate, CancellationToken.None);
                Assert.IsType<OkObjectResult>(result);
            }
        }

        await Report(stalled: true, 95);
        clock.Advance(TimeSpan.FromSeconds(90));
        await Report(stalled: true, 185);
        clock.Advance(TimeSpan.FromSeconds(30));
        await Report(stalled: false, 3);

        Assert.Equal(78, notifier.Recorded.Count + notifier.Sent.Count);   // every report kept
        Assert.Equal(6, notifier.Sent.Count);
        Assert.Equal(3, notifier.Sent.Count(t => t.StartsWith("Feed stalled")));
        Assert.Equal(3, notifier.Sent.Count(t => t.StartsWith("Feed recovered")));
        Assert.Equal(underlyings.OrderBy(u => u), notifier.Sent.Where(t => t.StartsWith("Feed stalled")).Select(t => t.Split(" on ")[1]).OrderBy(u => u));
    }

    private static StrategyController Controller(TradingDbContext db, ISystemNotifier notifier)
    {
        // Only what ReportFeedHealth touches: the runs, the log registry and the notifier.
        var registry = new StrategyProcessRegistry(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StrategyProcessRegistry>.Instance);
        return new StrategyController(db, null!, registry, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            notifier, null!, Microsoft.Extensions.Options.Options.Create(new AlgoTrading.Api.Configuration.StrategyRunnerOptions()),
            NullLogger<StrategyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private sealed class Recorder : ISystemNotifier
    {
        public List<string> Sent { get; } = new();
        public List<string> Recorded { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(title);
            return Task.CompletedTask;
        }

        public Task RecordAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Recorded.Add(title);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void A_record_only_event_carries_its_flag_to_the_subscriber()
    {
        var json = RedisSystemNotifier.Payload(NotificationCategory.StrategyRun, NotificationSeverity.Warning,
            "[admin] Feed stalled — Ghost on NIFTY", "Run #7 has had no ticks for 95s.", "NIFTY", "NSE:NIFTY50-INDEX", 7, recordOnly: true);

        var payload = JsonSerializer.Deserialize<AlertEventPayload>(json)!;
        Assert.True(AlertSubscriberService.IsRecordOnly(payload));
        Assert.Equal("[admin] Feed stalled — Ghost on NIFTY", payload.Title);
        Assert.Equal(7, payload.SimulationRunId);
    }

    [Fact]
    public void An_ordinary_event_is_unchanged_and_goes_to_telegram()
    {
        var json = RedisSystemNotifier.Payload(NotificationCategory.Process, NotificationSeverity.Info,
            "Dhan feed started", "pid 4242", null, null, null, recordOnly: false);

        Assert.DoesNotContain("RecordOnly", json);
        Assert.StartsWith("""{"Title":"Dhan feed started","Message":"pid 4242","Source":"process",""", json);
        Assert.False(AlertSubscriberService.IsRecordOnly(JsonSerializer.Deserialize<AlertEventPayload>(json)!));
    }

    [Fact]
    public async Task A_notifier_that_cannot_hold_a_message_back_sends_it()
    {
        // The default RecordAsync: a duplicate is noise, a lost alert is the failure.
        ISystemNotifier notifier = new SendOnly();
        await notifier.RecordAsync(NotificationCategory.StrategyRun, NotificationSeverity.Warning, "t", "m");
        Assert.Equal(1, ((SendOnly)notifier).Sent);
    }

    private sealed class SendOnly : ISystemNotifier
    {
        public int Sent { get; private set; }
        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent++;
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan span) => Now += span;
    }
}
