using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
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
        PlayedOut(channel, player, first.Id);
        await replay.TickAsync(default);
        await replay.TickAsync(default);
        Assert.Equal(MarketReplayService.StateFinished, (await replay.LoadAsync(default))!.State);

        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        var second = (await replay.LoadAsync(default))!;
        Assert.Equal(("2026-09-30", MarketReplayService.StateStarting), (second.Date, second.State));

        PlayedOut(channel, player, second.Id);
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

        // Monday 23:31, MCX closed too, was fine.
        clock.Set(Ist(2026, 10, 5, 23, 31));
        await replay.TickAsync(default);
        Assert.Equal("2026-09-30", (await replay.LoadAsync(default))!.Date);
    }

    [Fact]
    public async Task A_queued_day_waits_while_MCX_trades_and_starts_once_it_has_closed()
    {
        // Monday 5 Oct 2026, 16:00: NSE has closed, but the live crude runs trade MCX until 23:30 (New York on
        // daylight time), reading the tick stream a replay plays into.
        var (replay, _, player, _, clock) = Kit(Ist(2026, 10, 5, 16, 0), Sep30);

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 2), "admin", default);
        Assert.Empty(player.Starts);
        clock.Set(Ist(2026, 10, 5, 23, 29));
        await replay.TickAsync(default);
        Assert.Empty(player.Starts);

        clock.Set(Ist(2026, 10, 5, 23, 31));
        await replay.TickAsync(default);
        Assert.Single(player.Starts);
    }

    [Fact]
    public async Task In_November_MCX_closes_at_2355_and_a_day_started_then_ends_before_the_morning()
    {
        // Monday 2 Nov 2026: New York is on standard time again, so MCX trades until 23:55.
        var (replay, _, player, _, clock) = Kit(Ist(2026, 11, 2, 23, 50), Sep30);

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 1), "admin", default);
        Assert.Empty(player.Starts);

        clock.Set(Ist(2026, 11, 2, 23, 56));
        await replay.TickAsync(default);
        Assert.Single(player.Starts);          // at 1x it ends about 06:31, before Tuesday's 08:45
    }

    [Fact]
    public async Task A_day_that_would_run_into_MCXs_open_on_an_NSE_holiday_waits_for_the_evening()
    {
        // Wednesday 21 Oct 2026 as an NSE holiday on which MCX trades from 09:00. At 06:00 there is no 08:45 guard,
        // but a day at 2x (about 3 h 20 min) would still be playing when MCX opens.
        var holiday = new TestCalendar();
        holiday.Holidays[new DateOnly(2026, 10, 21)] = "Holiday";
        var (replay, _, player, _, clock) = Kit(Ist(2026, 10, 21, 6, 0), holiday, Sep30);

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 2), "admin", default);
        Assert.Empty(player.Starts);
        clock.Set(Ist(2026, 10, 21, 12, 0));      // MCX in session
        await replay.TickAsync(default);
        Assert.Empty(player.Starts);

        clock.Set(Ist(2026, 10, 21, 23, 31));
        await replay.TickAsync(default);
        Assert.Single(player.Starts);
    }

    [Fact]
    public async Task On_a_saturday_with_every_exchange_shut_a_queued_day_starts_at_once()
    {
        var (replay, _, player, _, _) = Kit(Ist(2026, 10, 3, 16, 0), Sep30);

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-30"], 2), "admin", default);

        Assert.Single(player.Starts);
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
        var (replay, _, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);

        var cancelled = await replay.CancelQueueAsync("upendra", default);
        Assert.Equal("Cancelled by upendra.", cancelled!.Note);
        Assert.True(MarketReplayService.IsActive((await replay.LoadAsync(default))!.State));

        PlayedOut(channel, player, 1);
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

    [Fact]
    public async Task A_queues_first_day_starts_once_though_the_monitor_looks_while_the_queue_is_starting_it()
    {
        // The queue's POST and the monitor's five-second look, in two scopes of one API, over one database.
        var shared = new Shared(Ist(2026, 10, 3, 12, 0));
        var post = shared.Service(Sep29);
        var monitor = shared.Service();
        var resume = new TaskCompletionSource();
        shared.Channel.NextResetWaitsFor = resume.Task;      // the POST's start pauses at its first Redis call

        var queued = post.QueueAsync(new ReplayQueueRequest(["2026-09-29"], 2), "admin", default);
        var look = monitor.TickAsync(default);
        await Task.WhenAny(look, Task.Delay(TimeSpan.FromMilliseconds(300)));
        resume.SetResult();
        await queued.WaitAsync(TimeSpan.FromSeconds(10));
        await look.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(shared.Player.Starts);
        var queue = (await monitor.LoadQueueAsync(default))!;
        Assert.Equal((1, 1), (queue.Next, queue.Sessions.Count));
        Assert.True(MarketReplayService.IsActive((await monitor.LoadAsync(default))!.State));
    }

    [Fact]
    public async Task A_manual_start_is_refused_while_a_queue_plays_even_in_the_gap_between_its_days()
    {
        var (replay, db, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);
        PlayedOut(channel, player, 1);
        await replay.TickAsync(default);
        db.SimulationRuns.Add(new SimulationRun { Mode = PaperTradingService.LivePaperMode, Symbol = "NSE:NIFTY50-INDEX", Status = "Running",
            StrategyName = "GhostTangentCrossings", ParametersJson = """{"session":"recap","recap_date":"2026-09-30"}""", UserId = 1,
            CreatedUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow });
        db.SaveChanges();
        long runId = db.SimulationRuns.Single().Id;

        var manual = await replay.StartAsync(new ReplayStartRequest("2026-09-30", 1, "09:15", [runId]), "upendra", default);

        Assert.Equal(409, manual.StatusCode);
        Assert.Contains("queue", manual.Error);
        Assert.Single(player.Starts);
    }

    [Fact]
    public async Task A_day_is_not_played_again_when_redis_blinks_just_after_its_player_started()
    {
        var (replay, _, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        channel.FailingReads = 1;

        try
        {
            await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);
        }
        catch (InvalidOperationException)
        {
            // The POST may fail; the queue must still know its first day is playing.
        }

        channel.FailingReads = 0;    // the blink is over
        PlayedOut(channel, player, 1);
        await replay.TickAsync(default);
        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);

        Assert.Equal(new[] { "2026-09-29", "2026-09-30" }, player.Starts.Select(a => a[1]));
        Assert.Equal(new long[] { 1, 2 }, (await replay.LoadQueueAsync(default))!.Sessions);
    }

    [Fact]
    public async Task A_day_started_but_not_written_into_the_queue_before_a_restart_counts_as_played()
    {
        // The API stopped between the player's start and the queue's write: the day's session is stored, the
        // queue still names it next.
        var (replay, db, player, _, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        var created = clock.GetUtcNow().UtcDateTime;
        Store(db, SystemSettingKeys.ReplayQueue, new ReplayQueueState(4, ["2026-09-29", "2026-09-30"], 2, "09:15", 0, [], [], "admin", created, null, null));
        Store(db, SystemSettingKeys.ReplaySession, new ReplaySessionState(7, "2026-09-29", 2, "09:15", MarketReplayService.StateFinished, [],
            created.AddSeconds(1), created.AddHours(3), null, "admin", AiTrader: true));
        clock.Advance(TimeSpan.FromHours(3) + MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));

        await replay.TickAsync(default);

        Assert.Equal("2026-09-30", player.Starts.Single()[1]);
        Assert.Equal(new long[] { 7, 8 }, (await replay.LoadQueueAsync(default))!.Sessions);
    }

    [Fact]
    public async Task The_next_day_waits_for_the_last_days_player_to_exit_rather_than_being_skipped()
    {
        var (replay, _, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        player.RefusesWhileRunning = true;
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);

        // Played out, but its process has not exited yet.
        channel.Status = new ReplayPlayerStatus(1, MarketReplayService.StateFinished, null, 0, 1, null, DateTime.UtcNow);
        await replay.TickAsync(default);
        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);

        Assert.Empty((await replay.LoadQueueAsync(default))!.Skipped);
        Assert.Single(player.Starts);

        player.Running = false;
        await replay.TickAsync(default);
        Assert.Equal("2026-09-30", player.Starts[^1][1]);
    }

    [Fact]
    public async Task A_queued_day_waits_while_a_vendors_recap_feed_runs_and_is_never_skipped_for_it()
    {
        var shared = new Shared(Ist(2026, 10, 3, 12, 0));
        shared.RecapFeeds.Running = "python-truedata-recap";
        var replay = shared.Service(Sep29, Sep30);

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);
        shared.Clock.Advance(TimeSpan.FromMinutes(30));
        await replay.TickAsync(default);

        Assert.Empty(shared.Player.Starts);
        var waiting = (await replay.LoadQueueAsync(default))!;
        Assert.Equal((0, 0), (waiting.Next, waiting.Skipped.Count));

        shared.RecapFeeds.Running = null;     // the vendor's recap has ended
        await replay.TickAsync(default);
        Assert.Equal("2026-09-29", shared.Player.Starts.Single()[1]);
    }

    [Fact]
    public async Task The_queues_stored_state_fits_its_setting_however_its_days_ended()
    {
        // Twenty days with nothing recorded, queued and cancelled by an account whose name is as long as a name can be
        // and not ASCII: the JSON escaped each such character as six.
        var (replay, db, _, _, _) = Kit(Ist(2026, 10, 3, 12, 0));
        string by = string.Concat(Enumerable.Repeat("उपेन्द्र ", 12))[..100];
        var days = Enumerable.Range(0, MarketReplayService.MaxQueuedDays).Select(i => new DateOnly(2026, 9, 1).AddDays(i).ToString("yyyy-MM-dd")).ToList();

        await replay.QueueAsync(new ReplayQueueRequest(days, 10), by, default);
        for (int i = 1; i < days.Count; i++) await replay.TickAsync(default);
        await replay.CancelQueueAsync(by, default);

        var queue = (await replay.LoadQueueAsync(default))!;
        Assert.Equal(days.Count, queue.Skipped.Count);
        Assert.InRange(db.SystemSettings.AsNoTracking().Single(s => s.Key == SystemSettingKeys.ReplayQueue).Value.Length, 1, SettingLength(db));
    }

    // ---------- days that did not play are tried again ----------

    [Fact]
    public async Task A_day_whose_player_does_not_start_is_tried_twice_more_after_a_wait_then_skipped()
    {
        // The database was down: every start in that window failed at once. Each used to be skipped on the spot.
        var (replay, _, player, _, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        player.RefuseStarts = 3;

        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);
        var waiting = (await replay.LoadQueueAsync(default))!;
        Assert.Equal((1, 0, 0), (player.Refused, waiting.Next, waiting.Skipped.Count));

        // Five minutes before the second try, fifteen before the third.
        clock.Advance(TimeSpan.FromMinutes(4));
        await replay.TickAsync(default);
        Assert.Equal(1, player.Refused);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        Assert.Equal(2, player.Refused);
        clock.Advance(TimeSpan.FromMinutes(14));
        await replay.TickAsync(default);
        Assert.Equal(2, player.Refused);
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        Assert.Equal(3, player.Refused);

        // Three tries: skipped with why, and the queue moves on.
        var gaveUp = (await replay.LoadQueueAsync(default))!;
        Assert.Equal(1, gaveUp.Next);
        Assert.StartsWith("2026-09-29: 3 tries; The player did not start", gaveUp.Skipped.Single());
        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        Assert.Equal("2026-09-30", player.Starts.Single()[1]);
        Assert.Single((await replay.LoadQueueAsync(default))!.Sessions);
    }

    [Fact]
    public async Task A_day_whose_replay_fails_soon_after_it_started_is_played_again_not_counted()
    {
        var (replay, _, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);
        var first = (await replay.LoadAsync(default))!;

        // Redis went away: the player died 30 s in, having played nothing.
        clock.Advance(TimeSpan.FromSeconds(30));
        player.Running = false;
        await replay.TickAsync(default);
        Assert.Equal(MarketReplayService.StateFailed, (await replay.LoadAsync(default))!.State);
        await replay.TickAsync(default);

        var queue = (await replay.LoadQueueAsync(default))!;
        Assert.Equal((0, 0, 0), (queue.Next, queue.Sessions.Count, queue.Skipped.Count));    // the day is next again

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        var second = (await replay.LoadAsync(default))!;
        Assert.Equal(("2026-09-29", MarketReplayService.StateStarting), (second.Date, second.State));
        Assert.True(second.Id > first.Id);

        PlayedOut(channel, player, second.Id);
        await replay.TickAsync(default);
        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        var third = (await replay.LoadAsync(default))!;
        Assert.Equal("2026-09-30", third.Date);
        Assert.Equal(new[] { second.Id, third.Id }, (await replay.LoadQueueAsync(default))!.Sessions);
    }

    [Fact]
    public async Task A_day_whose_replay_fails_late_counts_as_played()
    {
        var (replay, _, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);
        var first = (await replay.LoadAsync(default))!;

        clock.Advance(TimeSpan.FromHours(2));
        channel.Status = new ReplayPlayerStatus(first.Id, MarketReplayService.StateFailed, Ist(2026, 9, 29, 13, 0), 400_000, 0.6,
            "OperationalError: server closed the connection unexpectedly", DateTime.UtcNow);
        player.Running = false;
        await replay.TickAsync(default);
        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);

        Assert.Equal(new[] { "2026-09-29", "2026-09-30" }, player.Starts.Select(a => a[1]));
        Assert.Equal(2, (await replay.LoadQueueAsync(default))!.Sessions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_day_stopped_at_0845_is_played_again_after_the_close_not_counted(bool playerWillNotStop)
    {
        // Thursday 1 Oct, 23:31, MCX closed: a day at 1x fits before Friday's 08:45, but it was paused overnight.
        var (replay, _, player, channel, clock) = Kit(Ist(2026, 10, 1, 23, 31), Sep29);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29"], 1), "admin", default);
        var first = (await replay.LoadAsync(default))!;
        channel.Status = new ReplayPlayerStatus(first.Id, MarketReplayService.StatePaused, Ist(2026, 9, 29, 11, 0), 1000, 0.3, null, DateTime.UtcNow);
        player.StopThrows = playerWillNotStop;

        clock.Set(Ist(2026, 10, 2, 8, 45));
        await replay.TickAsync(default);
        Assert.Equal(MarketReplayService.StateStopped, (await replay.LoadAsync(default))!.State);
        player.StopThrows = false;
        player.Running = false;                               // it has exited, told to stop or refused its next post
        clock.Set(Ist(2026, 10, 2, 10, 0));
        await replay.TickAsync(default);

        var queue = (await replay.LoadQueueAsync(default))!;
        Assert.Equal((0, 0, 0), (queue.Next, queue.Sessions.Count, queue.Skipped.Count));
        Assert.Null(queue.EndedUtc);
        Assert.Single(player.Starts);                         // not during Friday's session

        clock.Set(Ist(2026, 10, 2, 16, 0));                   // nor while MCX trades
        await replay.TickAsync(default);
        Assert.Single(player.Starts);

        clock.Set(Ist(2026, 10, 3, 10, 0));
        await replay.TickAsync(default);
        Assert.Equal(new[] { "2026-09-29", "2026-09-29" }, player.Starts.Select(a => a[1]));
    }

    [Fact]
    public async Task A_day_the_owner_stopped_counts_as_played()
    {
        var (replay, _, player, _, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        await replay.QueueAsync(new ReplayQueueRequest(["2026-09-29", "2026-09-30"], 2), "admin", default);

        clock.Advance(TimeSpan.FromSeconds(30));
        await replay.StopAsync("upendra", default);
        clock.Advance(MarketReplayService.QueueGap + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);

        Assert.Equal(new[] { "2026-09-29", "2026-09-30" }, player.Starts.Select(a => a[1]));
        var queue = (await replay.LoadQueueAsync(default))!;
        Assert.Equal((2, 2, 0), (queue.Next, queue.Sessions.Count, queue.Skipped.Count));
    }

    [Fact]
    public async Task A_queue_stored_by_the_version_before_retries_carries_on_and_retries_its_day()
    {
        // As the deployed API wrote them: the queue has no record of tries, and its second day is playing.
        var (replay, db, player, channel, clock) = Kit(Ist(2026, 10, 3, 12, 0), Sep29, Sep30);
        var created = clock.GetUtcNow().UtcDateTime.AddHours(-4);
        StoreRaw(db, SystemSettingKeys.ReplayQueue,
            $$"""{"id":3,"dates":["2026-09-28","2026-09-29","2026-09-30"],"speed":2,"fromIst":"09:15","next":2,"sessions":[6,7],"skipped":[],"by":"admin","createdUtc":"{{created:O}}","endedUtc":null,"note":null}""");
        StoreRaw(db, SystemSettingKeys.ReplaySession,
            $$"""{"id":7,"date":"2026-09-29","speed":2,"fromIst":"09:15","state":"playing","runIds":[],"startedUtc":"{{clock.GetUtcNow().UtcDateTime.AddMinutes(-2):O}}","endedUtc":null,"error":null,"startedBy":"admin","aiTrader":true,"clockUtc":null,"ticksSent":0}""");

        player.Running = false;      // its player has died, two minutes in
        await replay.TickAsync(default);
        await replay.TickAsync(default);
        var queue = (await replay.LoadQueueAsync(default))!;
        Assert.Equal(1, queue.Next);
        Assert.Equal(new long[] { 6 }, queue.Sessions);

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await replay.TickAsync(default);
        var again = (await replay.LoadAsync(default))!;
        Assert.Equal(("2026-09-29", 8L), (again.Date, again.Id));
    }

    [Fact]
    public async Task The_queues_stored_state_fits_its_setting_with_every_day_tried_three_times()
    {
        // Twenty recorded days, and a player that never starts: each day is tried three times and skipped with why.
        // The clock has a fraction of a second, as a real one does, and the session ids are long.
        var recorded = Enumerable.Range(0, MarketReplayService.MaxQueuedDays).Select(i => new DateOnly(2026, 9, 1).AddDays(i)).ToArray();
        var (replay, db, player, _, clock) = Kit(Ist(2026, 10, 3, 12, 0).AddTicks(1_234_567), recorded);
        db.AiTraderDecisions.Add(new AiTraderDecision { Day = recorded[0], ClockUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow, ReplaySessionId = 999_900 });
        db.SaveChanges();
        string by = string.Concat(Enumerable.Repeat("उपेन्द्र ", 12))[..100];
        var days = recorded.Select(d => d.ToString("yyyy-MM-dd")).ToList();
        player.RefuseStarts = int.MaxValue;

        await replay.QueueAsync(new ReplayQueueRequest(days, 10), by, default);
        for (int i = 0; i < 200 && (await replay.LoadQueueAsync(default))!.Skipped.Count < days.Count - 1; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(16));
            await replay.TickAsync(default);
        }

        // The last day is between its second and third tries when the queue is cancelled.
        for (int i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(16));
            await replay.TickAsync(default);
        }

        await replay.CancelQueueAsync(by, default);

        var queue = (await replay.LoadQueueAsync(default))!;
        Assert.Equal(days.Count - 1, queue.Skipped.Count);
        Assert.All(queue.Skipped, s => Assert.Contains(": 3 tries; The player did not start", s));
        Assert.Equal(new string('3', days.Count - 1) + "2", queue.Tries);
        Assert.NotNull(queue.RetryUtc);
        Assert.InRange(db.SystemSettings.AsNoTracking().Single(s => s.Key == SystemSettingKeys.ReplayQueue).Value.Length, 1, SettingLength(db));
    }

    private static void StoreRaw(TradingDbContext db, string key, string json)
    {
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = json, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
        db.SaveChanges();
    }

    /// <summary>The longest value system_settings takes (varchar on PostgreSQL; the in-memory store takes any).</summary>
    internal static int SettingLength(TradingDbContext db) =>
        db.Model.FindEntityType(typeof(SystemSetting))!.FindProperty(nameof(SystemSetting.Value))!.GetMaxLength()!.Value;

    /// <summary>The day played out and its player exited, as the player does.</summary>
    private static void PlayedOut(FakeChannel channel, FakePlayer player, long session)
    {
        channel.Status = new ReplayPlayerStatus(session, MarketReplayService.StateFinished, null, 0, 1, null, DateTime.UtcNow);
        player.Running = false;
    }

    private static void Store<T>(TradingDbContext db, string key, T state)
    {
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow });
        db.SaveChanges();
    }

    private static (MarketReplayService Replay, TradingDbContext Db, FakePlayer Player, FakeChannel Channel, FakeClock Clock) Kit(DateTime nowUtc, params DateOnly[] recorded) =>
        Kit(nowUtc, new OpenCalendar(), recorded);

    private static (MarketReplayService Replay, TradingDbContext Db, FakePlayer Player, FakeChannel Channel, FakeClock Clock) Kit(DateTime nowUtc, IMarketCalendar calendar,
        params DateOnly[] recorded)
    {
        var shared = new Shared(nowUtc, calendar);
        var replay = shared.Service(recorded);
        return (replay, shared.LastDb!, shared.Player, shared.Channel, shared.Clock);
    }

    /// <summary>One API's singletons (player, channel, book, clock) and one database, for as many scopes as a test needs.</summary>
    private sealed class Shared
    {
        private readonly string _database = $"replay-queue-{Guid.NewGuid():N}";
        private readonly IMarketCalendar _calendar;

        public Shared(DateTime nowUtc, IMarketCalendar? calendar = null)
        {
            Clock.Set(nowUtc);
            _calendar = calendar ?? new OpenCalendar();
        }

        public FakeClock Clock { get; } = new();
        public FakePlayer Player { get; } = new();
        public FakeChannel Channel { get; } = new();
        public MarketReplayBook Book { get; } = new();
        public FakeRecapFeeds RecapFeeds { get; } = new();
        public TradingDbContext? LastDb { get; private set; }

        /// <summary>A scope: its own DbContext over the shared database, and the shared singletons.</summary>
        public MarketReplayService Service(params DateOnly[] recorded)
        {
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase(_database)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            foreach (var day in recorded)
            {
                db.LiveBars.Add(new LiveBar { Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m", BarStartUtc = IstTime.FromIst(day.ToDateTime(new TimeOnly(9, 15))),
                    Open = 1, High = 1, Low = 1, Close = 1, UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
            }

            db.SaveChanges();
            LastDb = db;
            var lots = new PositionGreeksTests.FixedLots(65);
            return new MarketReplayService(db, Player, Channel, new FakeStopper(), Book, new MarketSessionService(_calendar),
                new RunPnl(db, lots, new RunCharges(db, lots), Book), RecapFeeds, NullLogger<MarketReplayService>.Instance, Clock);
        }
    }
}
