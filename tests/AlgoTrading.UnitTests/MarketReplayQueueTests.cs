using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.MarketReplayTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The replay queue: recorded days played one after another with the AI Trader, each from the open, never in a
/// trading day's session and never so late that a day would run into the next trading morning.
/// </summary>
public class MarketReplayQueueTests
{
    private static readonly DateOnly Sep29 = new(2026, 9, 29);
    private static readonly DateOnly Sep30 = new(2026, 9, 30);

    private static DateTime Ist(int year, int month, int day, int hour, int minute) => IstTime.FromIst(new DateTime(year, month, day, hour, minute, 0));

    [Fact]
    public async Task Queued_days_play_oldest_first_from_the_open_with_the_ai_trader_and_a_gap_between_them()
    {
        var (replay, _, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);   // a Saturday

        var queued = await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30", "2026-09-29"], 2), "admin", default);

        Assert.Equal(202, queued.StatusCode);
        Assert.Equal(new[] { "2026-09-29", "2026-09-30" }, queued.Session!.Dates);
        Assert.Equal(new[] { "--date", "2026-09-29", "--speed", "2", "--from", "09:15" }, player.Args!.Take(6));
        var first = (await replay.LoadAsync(default))!;
        Assert.Equal((true, 0), (first.AiTrader, first.RunIds.Count));
        Assert.False((await replay.StatusAsync(default)).CanStart);

        // The first day plays out; the next waits for the gap, then starts.
        channel.Status = new ReplayPlayerStatus(first.Id, MarketReplayService.StateFinished, null, 0, 1, null, DateTime.UtcNow);
        await replay.TickAsync(default);
        await replay.TickAsync(default);
        Assert.Equal(MarketReplayService.StateFinished, (await replay.LoadAsync(default))!.State);

        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        var second = (await replay.LoadAsync(default))!;
        Assert.Equal(("2026-09-30", MarketReplayService.StateStarting), (second.Date, second.State));

        channel.Status = new ReplayPlayerStatus(second.Id, MarketReplayService.StateFinished, null, 0, 1, null, DateTime.UtcNow);
        await replay.TickAsync(default);
        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);

        var done = (await replay.LoadQueueAsync(default))!;
        Assert.Equal((2, 2, "Every day was played."), (done.Next, done.Sessions.Count, done.Note));
        Assert.NotNull(done.EndedUtc);
        Assert.True((await replay.StatusAsync(default)).CanStart);
    }

    [Fact]
    public async Task A_day_with_nothing_recorded_is_skipped_with_its_reason()
    {
        var (replay, _, player, _, _) = Kit(Ist(2026, 10, 3, 12, 0), Sep30);

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);
        Assert.Null(player.Args);
        await replay.TickAsync(default);

        var queue = (await replay.LoadQueueAsync(default))!;
        Assert.StartsWith("2026-09-29: The desk recorded nothing", queue.Skipped.Single());
        Assert.Equal("2026-09-30", (await replay.LoadAsync(default))!.Date);
    }

    [Fact]
    public async Task It_waits_out_a_trading_days_session_and_never_runs_into_the_next_morning()
    {
        // Monday 10:00: the session is the live desk's.
        var (replay, _, player, _, clock) = Kit(Ist(2026, 10, 5, 10, 0), Sep30);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 2), "admin", default);
        Assert.Null(player.Args);

        // Tuesday 06:30: a day at 2x (about 3 h 20 min) would run past 08:45.
        clock.Set(Ist(2026, 10, 6, 6, 30));
        await replay.TickAsync(default);
        Assert.Null(player.Args);

        // Monday 16:00 would have been fine.
        clock.Set(Ist(2026, 10, 5, 16, 0));
        await replay.TickAsync(default);
        Assert.Equal("2026-09-30", (await replay.LoadAsync(default))!.Date);
    }

    [Fact]
    public async Task At_ten_times_a_day_fits_in_an_early_morning()
    {
        var (replay, _, player, _, _) = Kit(Ist(2026, 10, 6, 6, 30), Sep30);

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 10), "admin", default);

        Assert.NotNull(player.Args);
    }

    [Fact]
    public async Task A_cancelled_queue_starts_no_further_day_and_the_day_playing_plays_on()
    {
        var (replay, _, _, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);

        var cancelled = await replay.CancelQueueAsync("upendra", default);
        Assert.Equal("Cancelled by upendra.", cancelled!.Note);
        Assert.True(MarketReplayService.IsActive((await replay.LoadAsync(default))!.State));

        channel.Status = new ReplayPlayerStatus(1, MarketReplayService.StateFinished, null, 0, 1, null, DateTime.UtcNow);
        await replay.TickAsync(default);
        clock.Advance(TimeSpan.FromMinutes(5));
        await replay.TickAsync(default);
        Assert.Equal("2026-09-29", (await replay.LoadAsync(default))!.Date);
    }

    [Fact]
    public async Task A_second_queue_or_a_bad_request_is_refused()
    {
        var (replay, _, _, _, _) = Kit(Ist(2026, 10, 3, 12, 0), Sep30);

        Assert.Equal(400, (await replay.QueueAsync(new ReplayQueueRequest([], 2), "admin", default)).StatusCode);
        Assert.Equal(400, (await replay.QueueAsync(new ReplayQueueRequest(["2026-10-03"], 2), "admin", default)).StatusCode);
        Assert.Equal(400, (await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 3), "admin", default)).StatusCode);
        Assert.Equal(202, (await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 2), "admin", default)).StatusCode);
        Assert.Equal(409, (await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 2), "admin", default)).StatusCode);
    }

    private static (MarketReplayService Replay, TradingDbContext Db, FakePlayer Player, FakeChannel Channel, FakeClock Clock) Kit(DateTime nowUtc, params DateOnly[] recorded)
    {
        var clock = new FakeClock();
        clock.Set(nowUtc);
        var db = NewDb();
        foreach (var day in recorded)
        {
            db.LiveBars.Add(new LiveBar { Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m", BarStartUtc = IstTime.FromIst(day.ToDateTime(new TimeOnly(9, 15))),
                Open = 1, High = 1, Low = 1, Close = 1, UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        }

        db.SaveChanges();
        var player = new FakePlayer();
        var channel = new FakeChannel();
        var book = new MarketReplayBook();
        var lots = new PositionGreeksTests.FixedLots(65);
        var replay = new MarketReplayService(db, player, channel, new FakeStopper(), book, new MarketSessionService(new OpenCalendar()),
            new RunPnl(db, lots, new RunCharges(db, lots), book), NullLogger<MarketReplayService>.Instance, clock);
        return (replay, db, player, channel, clock);
    }
}
