using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The market replay: a past day's prices kept apart from the live ones, read only by the recap runs of
/// that day, bars bounded by the replay's clock, and a replay that owns its runs from start to end.
/// </summary>
public class MarketReplayTests
{
    private static readonly DateOnly Day = new(2026, 9, 30);
    private const string Spot = "NSE:NIFTY50-INDEX";
    private const string Option = "NSE:NIFTY2610625000CE";
    private const string RecapOfDay = """{"session":"recap","recap_date":"2026-09-30","underlying":"NIFTY"}""";

    private static DateTime Ist(int h, int m, int s = 0) => IstTime.FromIst(Day.ToDateTime(new TimeOnly(h, m, s)));

    private static UpsertLiveTickRequest Tick(string symbol, decimal ltp, DateTime stamp, long? volume = null) => new()
    {
        Symbol = symbol, LastTradedPrice = ltp, BidPrice = ltp - 0.5m, AskPrice = ltp + 0.5m, ExchangeTimestampUtc = stamp,
        Volume = volume, IsReplay = true, SourceKey = "desk-replay",
    };

    // ---------- the book ----------

    [Fact]
    public void The_book_keeps_each_symbols_latest_replayed_quote_and_the_minute_in_progress()
    {
        var book = new MarketReplayBook();
        book.Begin(Day);

        int taken = book.Apply([
            Tick(Spot, 25000m, Ist(10, 5, 1), volume: 0),
            Tick(Spot, 25010m, Ist(10, 5, 20), volume: 0),
            Tick(Spot, 24990m, Ist(10, 5, 40), volume: 0),
            Tick(Option, 120m, Ist(10, 5, 41), volume: 1000),
            Tick(Option, 121m, Ist(10, 5, 50), volume: 1300),
        ]);

        Assert.Equal(5, taken);
        Assert.Equal((24990m, 24989.5m, 24990.5m), (book.Quote(Spot)!.LastTradedPrice, book.Quote(Spot)!.BidPrice, book.Quote(Spot)!.AskPrice));
        Assert.Equal(Ist(10, 5, 50), book.ClockUtc);
        var minute = book.CurrentMinute(Spot)!;
        Assert.Equal((Ist(10, 5), 25000m, 25010m, 24990m, 24990m, 3), (minute.BarStartUtc, minute.Open, minute.High, minute.Low, minute.Close, minute.TickCount));
        Assert.Equal(300, book.CurrentMinute(Option)!.VolumeDelta);
    }

    [Fact]
    public void A_contract_not_traded_yet_that_day_is_priced_but_moves_neither_clock_nor_bar_and_tomorrow_is_refused()
    {
        var book = new MarketReplayBook();
        book.Begin(Day);
        book.Apply([Tick(Spot, 25000m, Ist(10, 0))]);

        int taken = book.Apply([Tick(Option, 95m, Ist(15, 20).AddDays(-1)), Tick(Spot, 1m, Ist(10, 0).AddDays(1))]);

        Assert.Equal(1, taken);
        Assert.Equal(95m, book.Quote(Option)!.LastTradedPrice);
        Assert.Null(book.CurrentMinute(Option));
        Assert.Equal(Ist(10, 0), book.ClockUtc);
        Assert.Equal(25000m, book.Quote(Spot)!.LastTradedPrice);
    }

    [Fact]
    public void Only_a_recap_run_of_the_replayed_day_is_priced_from_the_book_and_nothing_after_it_ends()
    {
        var book = new MarketReplayBook();
        Assert.False(book.Prices(RecapOfDay));
        book.Begin(Day);

        Assert.True(book.Prices(RecapOfDay));
        Assert.False(book.Prices("""{"session":"recap","recap_date":"2026-09-29"}"""));
        Assert.False(book.Prices("""{"underlying":"NIFTY"}"""));
        Assert.Equal(0, new MarketReplayBook().Apply([Tick(Spot, 1m, Ist(10, 0))]));

        book.End();
        Assert.False(book.Prices(RecapOfDay));
        Assert.Null(book.Quote(Spot));
    }

    // ---------- fills ----------

    [Fact]
    public async Task A_recap_run_of_the_replayed_day_fills_at_the_replays_price_and_a_live_run_at_the_live_one()
    {
        var book = new MarketReplayBook();
        book.Begin(Day);
        book.Apply([Tick(Spot, 25000m, Ist(10, 7, 30)), Tick(Option, 120m, Ist(10, 7, 38))]);
        var db = NewDb();
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = Option, LastTradedPrice = 300m, BidPrice = 299.5m, AskPrice = 300.5m,
            UpdatedUtc = DateTime.UtcNow, ExchangeTimestampUtc = DateTime.UtcNow, SourceKey = "dhan", DataType = "symbolUpdate", RawPayload = "{}" });
        long recap = Run(db, RecapOfDay);
        long live = Run(db, """{"underlying":"NIFTY"}""");
        var service = new PaperTradingService(db, RecapClockTests.Inert<IRiskManagementService>.Create(), new PositionGreeksTests.FixedLots(65),
            replayBook: book);

        await service.CreateSignalAsync(Open(recap, Ist(10, 7, 38)));
        await service.CreateSignalAsync(Open(live, DateTime.UtcNow));

        var fills = db.PaperOrders.AsNoTracking().ToDictionary(o => o.SimulationRunId, o => o.FillPrice);
        Assert.Equal(120.5m, fills[recap]);   // the replay's ask
        Assert.Equal(300.5m, fills[live]);    // the live ask, untouched by the replay
    }

    // ---------- bars ----------

    [Fact]
    public async Task Bars_until_a_moment_end_at_the_last_recorded_minute_then_the_replays_own()
    {
        var db = NewDb();
        for (int minute = 0; minute < 10; minute++)
        {
            db.LiveBars.Add(new LiveBar { Symbol = Spot, Resolution = "1m", BarStartUtc = Ist(10, minute), Open = 100 + minute, High = 101 + minute,
                Low = 99 + minute, Close = 100.5m + minute, TickCount = 10, UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        }

        // A later day's bars, newer than the replay: the newest-first read used to return only these.
        db.LiveBars.Add(new LiveBar { Symbol = Spot, Resolution = "1m", BarStartUtc = Ist(10, 0).AddDays(1), Open = 1, High = 1, Low = 1, Close = 1,
            UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        db.SaveChanges();
        var service = new LiveDataService(db, new NoCatalog(), new MarketSessionService(new OpenCalendar()));
        var current = new LiveBarResponse { Symbol = Spot, Resolution = "1m", BarStartUtc = Ist(10, 7), Open = 107, High = 107.2m, Low = 106.9m, Close = 107.1m, TickCount = 3 };

        var bars = await service.GetBarsUntilAsync(Spot, "1m", 5, Ist(10, 7, 30), current);

        Assert.Equal(new[] { Ist(10, 7), Ist(10, 6), Ist(10, 5), Ist(10, 4), Ist(10, 3) }, bars.Select(b => b.BarStartUtc));
        Assert.Equal(107.1m, bars[0].Close);   // the replay's minute so far, not the recorded 107.5 close

        var fives = await service.GetBarsUntilAsync(Spot, "5m", 2, Ist(10, 7, 30), current);
        Assert.Equal(new[] { Ist(10, 5), Ist(10, 0) }, fives.Select(b => b.BarStartUtc));
        Assert.Equal((105m, 107.2m, 107.1m), (fives[0].Open, fives[0].High, fives[0].Close));
    }

    // ---------- the replay ----------

    [Fact]
    public async Task A_replay_starts_its_player_for_running_recap_runs_of_the_day_only()
    {
        var replay = Replay(out var db, out var player, out _, out _, out var book);
        long runId = Run(db, RecapOfDay, status: "Running");
        SeedBars(db);

        var refused = await replay.StartAsync(new ReplayStartRequest("2026-09-30", 1, "09:15", [runId, 999]), "admin", default);
        Assert.Equal(409, refused.StatusCode);
        Assert.Contains("run 999 does not exist", refused.Error);

        var started = await replay.StartAsync(new ReplayStartRequest("2026-09-30", 2, "10:00", [runId]), "admin", default);

        Assert.Equal(202, started.StatusCode);
        Assert.Equal(MarketReplayService.StateStarting, started.Session!.State);
        Assert.Equal(Day, book.Day);
        Assert.Equal(["--date", "2026-09-30", "--speed", "2", "--from", "10:00", "--session", "1", "--runs", runId.ToString()], player.Args);
        var again = await replay.StartAsync(new ReplayStartRequest("2026-09-29", 1, "09:15", [runId]), "admin", default);
        Assert.Equal(409, again.StatusCode);
    }

    [Theory]
    [InlineData("2026-10-03", 1, "09:15", "Only a day that is over")]
    [InlineData("2026-09-30", 3, "09:15", "speed is one of")]
    [InlineData("2026-09-30", 1, "15:10", "from is HH:mm")]
    [InlineData("30-09-2026", 1, "09:15", "date is yyyy-MM-dd")]
    public async Task A_replay_asked_for_wrongly_is_refused_with_why(string date, int speed, string from, string why)
    {
        var replay = Replay(out var db, out _, out _, out _, out _);
        long runId = Run(db, RecapOfDay, status: "Running");

        var result = await replay.StartAsync(new ReplayStartRequest(date, speed, from, [runId]), "admin", default);

        Assert.Null(result.Session);
        Assert.Contains(why, result.Error);
    }

    [Fact]
    public async Task No_replay_starts_while_NSE_trades_or_in_the_morning_before_it()
    {
        // 1 Oct 2026, a trading day, 08:50 IST.
        var replay = Replay(out var db, out _, out _, out _, out _, nowUtc: IstTime.FromIst(new DateTime(2026, 10, 1, 8, 50, 0)));
        long runId = Run(db, RecapOfDay, status: "Running");

        var result = await replay.StartAsync(new ReplayStartRequest("2026-09-30", 1, "09:15", [runId]), "admin", default);

        Assert.Equal(409, result.StatusCode);
        Assert.Contains("NSE trades today", result.Error);
    }

    [Fact]
    public async Task A_replay_played_out_stops_its_runs_at_the_replays_prices_then_forgets_them()
    {
        var replay = Replay(out var db, out _, out var channel, out var stopper, out var book);
        long runId = Run(db, RecapOfDay, status: "Running");
        SeedBars(db);
        await replay.StartAsync(new ReplayStartRequest("2026-09-30", 1, "09:15", [runId]), "admin", default);
        book.Apply([Tick(Spot, 25000m, Ist(15, 31))]);

        channel.Status = new ReplayPlayerStatus(1, MarketReplayService.StatePlaying, Ist(12, 0), 1000, 0.5, null, DateTime.UtcNow);
        await replay.TickAsync(default);
        Assert.Equal(MarketReplayService.StatePlaying, (await replay.LoadAsync(default))!.State);
        Assert.Empty(stopper.Stopped);

        channel.Status = channel.Status with { State = MarketReplayService.StateFinished, ClockUtc = Ist(15, 32) };
        stopper.BookOnStop = book;
        await replay.TickAsync(default);

        Assert.Equal([runId], stopper.Stopped);
        Assert.True(stopper.BookWasOn);              // the runs were stopped while the replay's prices were there
        Assert.Null(book.Day);
        var ended = (await replay.LoadAsync(default))!;
        Assert.Equal(MarketReplayService.StateFinished, ended.State);
        Assert.NotNull(ended.EndedUtc);
    }

    [Fact]
    public async Task A_player_that_dies_ends_the_replay_as_failed_and_its_runs_are_stopped()
    {
        var replay = Replay(out var db, out var player, out _, out var stopper, out _);
        long runId = Run(db, RecapOfDay, status: "Running");
        SeedBars(db);
        await replay.StartAsync(new ReplayStartRequest("2026-09-30", 1, "09:15", [runId]), "admin", default);
        player.Running = false;
        Clock.Advance(TimeSpan.FromMinutes(1));

        await replay.TickAsync(default);

        Assert.Equal([runId], stopper.Stopped);
        var ended = (await replay.LoadAsync(default))!;
        Assert.Equal((MarketReplayService.StateFailed, "The replay player stopped before the day was played out."), (ended.State, ended.Error));
    }

    [Fact]
    public async Task A_replay_still_playing_at_0845_on_a_trading_day_is_stopped()
    {
        // Started on Thursday 1 Oct 2026 after the close, still playing on Friday morning.
        var replay = Replay(out var db, out var player, out _, out var stopper, out _, nowUtc: IstTime.FromIst(new DateTime(2026, 10, 1, 20, 0, 0)));
        long runId = Run(db, RecapOfDay, status: "Running");
        SeedBars(db);
        await replay.StartAsync(new ReplayStartRequest("2026-09-30", 1, "09:15", [runId]), "admin", default);
        Clock.Set(IstTime.FromIst(new DateTime(2026, 10, 2, 8, 45, 0)));

        await replay.TickAsync(default);

        Assert.True(player.StoppedFor?.Contains("market opens"));
        Assert.Equal(MarketReplayService.StateStopped, (await replay.LoadAsync(default))!.State);
        Assert.Equal([runId], stopper.Stopped);
    }

    [Fact]
    public async Task An_api_restart_mid_replay_opens_the_book_again_for_the_players_next_ticks()
    {
        var replay = Replay(out var db, out _, out var channel, out _, out var book);
        long runId = Run(db, RecapOfDay, status: "Running");
        SeedBars(db);
        await replay.StartAsync(new ReplayStartRequest("2026-09-30", 1, "09:15", [runId]), "admin", default);
        book.End();   // what a restart leaves
        channel.Status = new ReplayPlayerStatus(1, MarketReplayService.StatePlaying, Ist(11, 0), 10, 0.3, null, DateTime.UtcNow);

        await replay.TickAsync(default);

        Assert.Equal(Day, book.Day);
    }

    [Fact]
    public void Progress_runs_from_the_open_to_the_close_and_is_whole_once_finished()
    {
        Assert.Equal(0, MarketReplayService.ProgressOf(null, Day, MarketReplayService.StatePlaying));
        Assert.Equal(0.5, MarketReplayService.ProgressOf(Ist(12, 22, 30), Day, MarketReplayService.StatePlaying), 3);
        Assert.Equal(1, MarketReplayService.ProgressOf(null, Day, MarketReplayService.StateFinished));
    }

    // ---------- helpers ----------

    private static readonly FakeClock Clock = new();

    private static MarketReplayService Replay(out TradingDbContext db, out FakePlayer player, out FakeChannel channel,
        out FakeStopper stopper, out MarketReplayBook book, DateTime? nowUtc = null)
    {
        // Saturday 3 Oct 2026, noon IST: no session.
        Clock.Set(nowUtc ?? IstTime.FromIst(new DateTime(2026, 10, 3, 12, 0, 0)));
        db = NewDb();
        player = new FakePlayer();
        channel = new FakeChannel();
        stopper = new FakeStopper();
        book = new MarketReplayBook();
        var lots = new PositionGreeksTests.FixedLots(65);
        var charges = new RunCharges(db, lots);
        return new MarketReplayService(db, player, channel, stopper, book,
            new MarketSessionService(new OpenCalendar()), new RunPnl(db, lots, charges, book),
            NullLogger<MarketReplayService>.Instance, Clock);
    }

    private static TradingDbContext NewDb() => new(new DbContextOptionsBuilder<TradingDbContext>()
        .UseInMemoryDatabase($"replay-{Guid.NewGuid():N}")
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
        .Options);

    private static long Run(TradingDbContext db, string parameters, string status = "Running")
    {
        var run = new SimulationRun { Mode = PaperTradingService.LivePaperMode, Symbol = Spot, Status = status, StrategyName = "GhostTangentCrossings",
            ParametersJson = parameters, UserId = 1, CreatedUtc = DateTime.UtcNow, StartedUtc = DateTime.UtcNow };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private static void SeedBars(TradingDbContext db)
    {
        db.LiveBars.Add(new LiveBar { Symbol = Spot, Resolution = "1m", BarStartUtc = Ist(9, 15), Open = 1, High = 1, Low = 1, Close = 1,
            UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        db.SaveChanges();
    }

    private static CreateSimulationSignalRequest Open(long runId, DateTime at) => new()
    {
        SimulationRunId = runId, StrategyName = "GhostTangentCrossings", SignalType = "OPEN_GROUP", TimestampUtc = at,
        GroupId = $"G{runId}", MetadataJson = "{}",
        Legs = [new SimulationSignalLegRequest { Symbol = Option, Side = "BUY", Quantity = 1 }],
    };

    private sealed class FakeClock : TimeProvider
    {
        private DateTime _now;

        public void Set(DateTime utc) => _now = DateTime.SpecifyKind(utc, DateTimeKind.Utc);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => new(_now);
    }

    private sealed class FakePlayer : IReplayPlayer
    {
        public bool Running { get; set; } = true;

        public IReadOnlyList<string>? Args { get; private set; }

        public string? StoppedFor { get; private set; }

        public Task<bool> IsRunningAsync(CancellationToken cancellationToken) => Task.FromResult(Running);

        public Task<(bool Started, string Message)> StartAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Args = args;
            return Task.FromResult((true, "started"));
        }

        public Task StopAsync(string reason, CancellationToken cancellationToken)
        {
            StoppedFor = reason;
            Running = false;
            return Task.CompletedTask;
        }

        public IReadOnlyList<string> Logs(int take) => [];
    }

    private sealed class FakeChannel : IReplayChannel
    {
        public ReplayPlayerStatus? Status { get; set; }

        public Task<ReplayPlayerStatus?> ReadStatusAsync(CancellationToken cancellationToken) => Task.FromResult(Status);

        public Task SendAsync(string command, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            Status = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStopper : IReplayRunStopper
    {
        public List<long> Stopped { get; } = [];

        public MarketReplayBook? BookOnStop { get; set; }

        public bool BookWasOn { get; private set; }

        public Task StopAsync(long runId, string reason, CancellationToken cancellationToken)
        {
            Stopped.Add(runId);
            BookWasOn = BookOnStop?.Day is not null;
            return Task.CompletedTask;
        }
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
