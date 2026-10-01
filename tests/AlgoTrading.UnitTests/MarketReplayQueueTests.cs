using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.Replay;
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

    private static (MarketReplayService Replay, TradingDbContext Db, FakePlayer Player, FakeChannel Channel, FakeClock Clock) Kit(DateTime nowUtc, params DateOnly[] recorded)
    {
        var shared = new Shared(nowUtc);
        var replay = shared.Service(recorded);
        return (replay, shared.LastDb!, shared.Player, shared.Channel, shared.Clock);
    }

    /// <summary>One API's singletons (player, channel, book, clock) and one database, for as many scopes as a test needs.</summary>
    private sealed class Shared
    {
        private readonly string _database = $"replay-queue-{Guid.NewGuid():N}";

        public Shared(DateTime nowUtc) => Clock.Set(nowUtc);

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
            return new MarketReplayService(db, Player, Channel, new FakeStopper(), Book, new MarketSessionService(new OpenCalendar()),
                new RunPnl(db, lots, new RunCharges(db, lots), Book), RecapFeeds, NullLogger<MarketReplayService>.Instance, Clock);
        }
    }
}
