using System.Diagnostics;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.MarketReplayTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A market replay playing never reaches the live desk. A live run's fills, marks, P&amp;L, square-off at the
/// close, risk guard, clock and its runner's quote and bar calls read the live prices exactly as before,
/// whatever the replay's book holds for the same contract; and the close still stops every live run.
/// </summary>
/// <remarks>
/// One replay (a queue of days) plays on the server through the weekend, and the monitor that ends it at
/// 08:45 is the only thing between it and Monday's open. These hold the live side if it plays on: every live
/// figure below is taken while the book prices the same contract at another day's price.
/// </remarks>
public class LiveRunsBesideReplayTests
{
    private static readonly DateOnly ReplayedDay = new(2026, 9, 25);
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string Call = "NSE:NIFTY2692924500CE";
    private const string Put = "NSE:NIFTY2692924500PE";
    private const string LiveParameters = """{"underlying":"NIFTY","lots":2}""";

    /// <summary>TrueData's evening recap of today: a recap, but not of the replayed day, so it reads the live table.</summary>
    private const string VendorRecapParameters = """{"session":"recap","recap_date":"2026-09-28","underlying":"NIFTY"}""";

    private static DateTime Ist(int month, int day, int h, int m, int s = 0) => IstTime.FromIst(new DateTime(2026, month, day, h, m, s));

    /// <summary>A replay of Friday 25 Sep at 10:07, pricing the call at 160 and the put at 40.</summary>
    private static MarketReplayBook ReplayPlaying()
    {
        var book = new MarketReplayBook();
        book.Begin(ReplayedDay);
        book.Apply([
            new UpsertLiveTickRequest { Symbol = Nifty, LastTradedPrice = 24000m, ExchangeTimestampUtc = Ist(9, 25, 10, 7, 30), IsReplay = true },
            new UpsertLiveTickRequest { Symbol = Call, LastTradedPrice = 160m, BidPrice = 159.5m, AskPrice = 160.5m, ExchangeTimestampUtc = Ist(9, 25, 10, 7, 31), IsReplay = true },
            new UpsertLiveTickRequest { Symbol = Put, LastTradedPrice = 40m, BidPrice = 39.5m, AskPrice = 40.5m, ExchangeTimestampUtc = Ist(9, 25, 10, 7, 32), IsReplay = true },
        ]);
        Assert.Equal(160m, book.Quote(Call)!.LastTradedPrice);
        return book;
    }

    // ================================================================ the engine

    [Fact]
    public async Task A_live_run_is_marked_valued_and_refreshed_at_the_live_price_while_a_replay_prices_the_same_contract()
    {
        var book = ReplayPlaying();
        var db = NewDb();
        LiveQuote(db, Call, 90m);
        long live = NewRun(db, LiveParameters);
        long vendorRecap = NewRun(db, VendorRecapParameters);
        long liveLeg = OpenLeg(db, live, Call, "SELL", 100m);
        long vendorLeg = OpenLeg(db, vendorRecap, Call, "SELL", 100m);
        var service = Engine(db, book);

        var marked = (await service.GetPaperPositionsAsync(live)).Single(x => x.Id == liveLeg);
        Assert.Equal(90m, marked.LastMarkPrice);
        Assert.Equal(10m * 2 * 65, marked.UnrealizedPnl);                  // SELL 2 lots of 65 at 100, now 90
        Assert.Equal(90m, (await service.GetPaperPositionsAsync(vendorRecap)).Single(x => x.Id == vendorLeg).LastMarkPrice);

        Assert.Equal(10m * 2 * 65, (await service.GetPortfolioSummaryAsync(live)).UnrealizedPnl);
        Assert.Equal(10m * 2 * 65, (await service.RefreshPortfolioMarkToMarketAsync(live)).UnrealizedPnl);
        Assert.Equal(90m, db.PaperPositions.AsNoTracking().Single(x => x.Id == liveLeg).LastMarkPrice);
    }

    [Fact]
    public async Task A_live_run_is_squared_off_at_the_live_bid_and_ask_on_the_wall_clock_while_a_replay_plays()
    {
        var book = ReplayPlaying();
        var db = NewDb();
        LiveQuote(db, Call, 90m);
        LiveQuote(db, Put, 70m);
        long live = NewRun(db, LiveParameters);
        OpenLeg(db, live, Call, "SELL", 100m);
        OpenLeg(db, live, Put, "BUY", 60m);
        long manual = NewRun(db, "{}", strategy: ManualOrdersController.BookStrategyName);
        long manualLeg = OpenLeg(db, manual, Put, "BUY", 60m);
        var service = Engine(db, book);
        var opening = db.PaperOrders.AsNoTracking().Select(o => o.Id).ToHashSet();
        var before = DateTime.UtcNow;

        Assert.Equal(2, await service.FlattenRunAsync(live, "Market closed (15:30 IST)"));
        Assert.Equal(1, await service.CloseIntradayPositionsAsync(manual, [manualLeg], "Intraday — squared off at the close (15:30 IST)", "market-hours"));

        var closes = db.PaperOrders.AsNoTracking().ToList().Where(o => !opening.Contains(o.Id)).ToList();
        Assert.Equal(3, closes.Count);
        Assert.Equal(90.5m, closes.Single(o => o.SimulationRunId == live && o.Symbol == Call).FillPrice);      // bought back at the live ask, not 160.5
        Assert.Equal(69.5m, closes.Single(o => o.SimulationRunId == live && o.Symbol == Put).FillPrice);       // sold at the live bid, not 39.5
        Assert.Equal(69.5m, closes.Single(o => o.SimulationRunId == manual).FillPrice);
        // Stamped now, as a live close always is: never on the replay's 25 Sep clock.
        Assert.All(closes, o => Assert.True(o.FilledUtc >= before, $"{o.Symbol} filled at {o.FilledUtc:O}"));
        Assert.All(db.SimulationSignals.AsNoTracking().ToList(), s => Assert.True(s.TimestampUtc >= before));
    }

    [Fact]
    public async Task A_live_run_opens_at_the_live_ask_while_a_replay_plays()
    {
        var book = ReplayPlaying();
        var db = NewDb();
        LiveQuote(db, Call, 90m);
        long live = NewRun(db, LiveParameters);
        var service = Engine(db, book);

        await service.CreateSignalAsync(new CreateSimulationSignalRequest
        {
            SimulationRunId = live, StrategyName = "Ghost", SignalType = "OPEN_GROUP", TimestampUtc = DateTime.UtcNow,
            GroupId = "G1", MetadataJson = "{}",
            Legs = [new SimulationSignalLegRequest { Symbol = Call, Side = "BUY", Quantity = 1 }],
        });

        Assert.Equal(90.5m, db.PaperOrders.AsNoTracking().Single().FillPrice);
    }

    [Fact]
    public async Task A_live_run_keeps_the_wall_clock_while_a_replay_plays()
    {
        var book = ReplayPlaying();
        var db = NewDb();
        // TrueData's evening recap of 28 Sep has reached 11:00 in the live table.
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = Nifty, LastTradedPrice = 25000m, UpdatedUtc = DateTime.UtcNow,
            ExchangeTimestampUtc = Ist(9, 28, 11, 0), SourceKey = "truedata", DataType = "symbolUpdate", RawPayload = "{}" });
        db.SaveChanges();
        var live = db.SimulationRuns.Find(NewRun(db, LiveParameters))!;
        var vendorRecap = db.SimulationRuns.Find(NewRun(db, VendorRecapParameters))!;

        Assert.Null(await RecapClock.NowAsync(db, live, CancellationToken.None, book));
        // The vendor's recap reads its clock off the live table, never the desk replay's 25 Sep.
        Assert.Equal(Ist(9, 28, 11, 0), await RecapClock.NowAsync(db, vendorRecap, CancellationToken.None, book));
    }

    // ============================================================= the desk

    [Fact]
    public async Task The_close_squares_off_a_live_run_at_the_live_price_while_a_replay_plays()
    {
        using var desk = new CarryForwardTests.Desk(ReplayPlaying());
        long run = desk.NewRun("Ghost", CarryForwardTests.Desk.Owner, parameters: LiveParameters);
        long call = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, Ist(9, 28, 10, 0));
        long put = desk.Fill(run, "Ghost", "G1", Put, "SELL", 2, 80m, Ist(9, 28, 10, 0));
        desk.Quote(Call, 90m);
        desk.Quote(Put, 70m);
        desk.Register(run, "Ghost", CarryForwardTests.Desk.Owner);

        var result = await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", Ist(9, 28, 15, 30)));

        Assert.True(result.WasRunning);
        Assert.Equal(2, result.Flattened);
        Assert.Equal(("Closed", 90m), (desk.Position(call).Status, desk.Position(call).LastMarkPrice));
        Assert.Equal(("Closed", 70m), (desk.Position(put).Status, desk.Position(put).LastMarkPrice));
        Assert.Equal("Stopped", desk.Run(run).Status);
    }

    [Fact]
    public async Task The_manual_books_intraday_square_off_is_at_the_live_price_while_a_replay_plays()
    {
        using var desk = new CarryForwardTests.Desk(ReplayPlaying());
        long book = desk.NewBook(CarryForwardTests.Desk.Owner);
        long leg = desk.Fill(book, ManualOrdersController.BookStrategyName, "MANUAL-1", Call, "BUY", 1, 100m, Ist(9, 28, 10, 0));
        desk.Quote(Call, 90m);

        Assert.Equal(1, await desk.InScope(sp => sp.GetRequiredService<ManualIntradaySquareOff>()
            .SquareOffDueAsync(Ist(9, 28, 15, 31), CancellationToken.None)));

        Assert.Equal(("Closed", 90m), (desk.Position(leg).Status, desk.Position(leg).LastMarkPrice));
    }

    [Fact]
    public async Task The_risk_guard_judges_a_live_leg_on_the_live_price_never_the_replays()
    {
        using var desk = new CarryForwardTests.Desk(ReplayPlaying());
        long run = desk.NewRun("Ghost", CarryForwardTests.Desk.Owner, parameters: LiveParameters);
        long leg = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, Ist(9, 28, 10, 0));
        desk.Register(run, "Ghost", CarryForwardTests.Desk.Owner, new RiskRulesDto { Leg = new LegRiskDto { StopLossPoints = 20m } });
        var guard = desk.Guard();

        // The replay's 160 is 60 points against the leg; the live 101 is one.
        desk.Quote(Call, 101m);
        await guard.SweepOnceAsync(CancellationToken.None);
        Assert.Equal("Open", desk.Position(leg).Status);

        // The live price goes through the stop: closed there, not at the replay's price.
        desk.Quote(Call, 125m);
        await guard.SweepOnceAsync(CancellationToken.None);
        Assert.Equal("Closed", desk.Position(leg).Status);
        Assert.Equal(125m, desk.Orders(run).Single(o => o.Side == "BUY").FillPrice);
    }

    [Fact]
    public async Task A_live_runs_card_and_page_show_the_live_price_while_a_replay_plays()
    {
        var replay = ReplayPlaying();
        using var desk = new CarryForwardTests.Desk(replay);
        long run = desk.NewRun("Ghost", CarryForwardTests.Desk.Owner, parameters: LiveParameters);
        desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, Ist(9, 28, 10, 0));
        desk.Quote(Call, 90m);
        desk.Quote(Nifty, 25000m);

        var marks = await desk.InScope(sp => sp.GetRequiredService<RunPnl>().MarkOpenLegsAsync([run], CancellationToken.None));
        Assert.Equal(10m * 2 * 75, marks[run].Unrealized);

        var figures = await desk.InScope(sp => sp.GetRequiredService<RunPnl>()
            .FiguresAsync([run], new HashSet<long> { run }, CancellationToken.None));
        Assert.Equal(10m * 2 * 75, figures[run].Unrealized);

        // The run page, as StrategyController builds it: priced from the replay only when the book says so.
        var parameters = desk.Run(run).ParametersJson;
        Assert.False(replay.Prices(parameters));
        var built = await desk.InScope(async sp =>
        {
            var positions = await sp.GetRequiredService<IPaperTradingService>().GetPaperPositionsAsync(run);
            return await sp.GetRequiredService<PositionViewBuilder>().BuildAsync<LivePositionResponse>(
                positions, useLiveQuotes: true, Nifty, CancellationToken.None, replayPriced: replay.Prices(parameters));
        });
        Assert.Equal(25000m, built.SpotLtp);
        Assert.Equal(90m, built.Positions.Single().Ltp);
    }

    // ============================================================ the close sweep

    [Fact]
    public void The_close_sweep_stops_every_live_run_and_leaves_only_a_playing_replays_runs_to_it()
    {
        using var p = new Process();
        var startedMorning = Ist(10, 5, 9, 20);
        var liveNifty = Entry(p, 101, "NIFTY", Nifty, startedMorning);
        var liveSensex = Entry(p, 102, "SENSEX", "BSE:SENSEX-INDEX", startedMorning);
        var liveCrude = Entry(p, 103, "CRUDEOIL", "MCX:CRUDEOIL26OCTFUT", startedMorning);
        var replayRun = Entry(p, 104, "NIFTY", Nifty, startedMorning);
        var claimed = Entry(p, 105, "NIFTY", Nifty, startedMorning);
        Assert.True(claimed.TryClaimStop());
        var sessions = PositionGreeksTests.Sessions();

        // Monday 5 Oct, 15:31 IST.
        var due = MarketHoursService.RunsDueAtClose(sessions, Ist(10, 5, 15, 31),
            [liveNifty, liveSensex, liveCrude, replayRun, claimed], new HashSet<long> { 104 });

        Assert.Equal(new long[] { 101, 102 }, due.Select(x => x.RunId));
        Assert.All(due, x => Assert.Equal(Ist(10, 5, 15, 30), x.ClosedAtUtc));
        Assert.Equal(["Market closed (15:30 IST)", "Market closed (15:30 IST)"], due.Select(x => x.Reason));

        // MCX, on New York's daylight time: 23:30.
        var night = MarketHoursService.RunsDueAtClose(sessions, Ist(10, 5, 23, 31), [liveCrude], new HashSet<long>());
        Assert.Equal(("MCX closed (23:30 IST)", Ist(10, 5, 23, 30)), (night.Single().Reason, night.Single().ClosedAtUtc));

        // No replay: the same runs, and the replay's would be judged like any other.
        var plain = MarketHoursService.RunsDueAtClose(sessions, Ist(10, 5, 15, 31), [liveNifty, replayRun], new HashSet<long>());
        Assert.Equal(new long[] { 101, 104 }, plain.Select(x => x.RunId));
    }

    [Fact]
    public async Task Only_a_replay_in_progress_holds_its_runs_back_from_the_close()
    {
        var db = NewDb();
        await using var provider = ReplayServices(db);

        Assert.Empty(await MarketHoursService.ReplayRunIdsAsync(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, default));

        Store(db, MarketReplayService.StatePlaying, [41, 42]);
        Assert.Equal(new long[] { 41, 42 },
            (await MarketHoursService.ReplayRunIdsAsync(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, default)).Order());

        Store(db, MarketReplayService.StateFinished, [41, 42]);
        Assert.Empty(await MarketHoursService.ReplayRunIdsAsync(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, default));
    }

    [Fact]
    public async Task A_replay_that_cannot_be_read_keeps_no_live_run_from_its_close()
    {
        // The replay's state could not be had (its service would not build, the read failed): the sweep goes
        // ahead as it did before replays existed, every run judged by its own close. Thrown, it took the whole
        // sweep down with it, and every live run stayed open past 15:30 while the failure lasted.
        var services = new ServiceCollection();
        services.AddScoped<MarketReplayService>(_ => throw new InvalidOperationException("Redis did not answer."));
        await using var provider = services.BuildServiceProvider();

        var held = await MarketHoursService.ReplayRunIdsAsync(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, default);

        Assert.Empty(held);
        using var p = new Process();
        var due = MarketHoursService.RunsDueAtClose(PositionGreeksTests.Sessions(), Ist(10, 5, 15, 31),
            [Entry(p, 101, "NIFTY", Nifty, Ist(10, 5, 9, 20))], held);
        Assert.Equal(101, due.Single().RunId);
    }

    [Fact]
    public async Task The_close_sweep_still_stops_and_squares_off_the_live_runs_when_the_replay_lookup_throws()
    {
        // Every minute from 15:30 the sweep asks the replay which runs are its own first. That read failing
        // (the database blinking, the replay's service not building) must not keep the live runs open.
        using var desk = new CarryForwardTests.Desk(more: services =>
            services.AddScoped<MarketReplayService>(_ => throw new InvalidOperationException("The replay's state could not be read.")));
        long run = desk.NewRun("Ghost", CarryForwardTests.Desk.Owner, parameters: LiveParameters);
        long leg = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, Ist(9, 28, 10, 0));
        desk.Quote(Call, 90m);
        desk.Register(run, "Ghost", CarryForwardTests.Desk.Owner);

        int stopped = await MarketHoursService.StopRunsPastTheirCloseAsync(desk.Scopes, PositionGreeksTests.Sessions(), desk.Registry,
            NullLogger.Instance, Ist(9, 28, 15, 31), CancellationToken.None);

        Assert.Equal(1, stopped);
        Assert.Equal("Stopped", desk.Run(run).Status);
        Assert.Equal(("Closed", 90m), (desk.Position(leg).Status, desk.Position(leg).LastMarkPrice));
        Assert.Null(desk.Registry.Get(run));
    }

    [Fact]
    public async Task The_close_sweep_stops_the_live_runs_and_leaves_a_playing_replays_run_to_the_replay()
    {
        var replay = ReplayPlaying();
        using var desk = new CarryForwardTests.Desk(replay, services => services.AddScoped(sp =>
        {
            var db = sp.GetRequiredService<TradingDbContext>();
            var lots = new PositionGreeksTests.FixedLots(75);
            return new MarketReplayService(db, new FakePlayer(), new FakeChannel(), new FakeStopper(), replay, PositionGreeksTests.Sessions(),
                new RunPnl(db, lots, new RunCharges(db, lots), replay), new FakeRecapFeeds(), NullLogger<MarketReplayService>.Instance);
        }));
        long live = desk.NewRun("Ghost", CarryForwardTests.Desk.Owner, parameters: LiveParameters);
        long recap = desk.NewRun("Ghost", CarryForwardTests.Desk.Owner, parameters: """{"session":"recap","recap_date":"2026-09-25","underlying":"NIFTY"}""");
        desk.Fill(live, "Ghost", "G1", Call, "SELL", 2, 100m, Ist(9, 28, 10, 0));
        desk.Fill(recap, "Ghost", "G1", Call, "SELL", 2, 100m, Ist(9, 28, 10, 0));
        desk.Quote(Call, 90m);
        desk.Register(live, "Ghost", CarryForwardTests.Desk.Owner);
        desk.Register(recap, "Ghost", CarryForwardTests.Desk.Owner);
        desk.Seed(db => Store(db, MarketReplayService.StatePlaying, [recap]));

        int stopped = await MarketHoursService.StopRunsPastTheirCloseAsync(desk.Scopes, PositionGreeksTests.Sessions(), desk.Registry,
            NullLogger.Instance, Ist(9, 28, 15, 31), CancellationToken.None);

        Assert.Equal(1, stopped);
        Assert.Equal("Stopped", desk.Run(live).Status);
        Assert.Equal("Running", desk.Run(recap).Status);
        Assert.NotNull(desk.Registry.Get(recap));
    }

    // ========================================================= the runner's reads

    [Fact]
    public async Task A_live_runners_quotes_and_bars_are_the_live_tables_while_a_replay_plays()
    {
        var book = ReplayPlaying();
        book.Apply([new UpsertLiveTickRequest { Symbol = Nifty, LastTradedPrice = 24010m, ExchangeTimestampUtc = Ist(9, 25, 10, 8, 5), IsReplay = true }]);
        Assert.NotNull(book.CurrentMinute(Nifty));
        var db = NewDb();
        LiveQuote(db, Nifty, 25000m);
        var today = Ist(10, 5, 10, 0);
        for (int minute = 0; minute < 3; minute++)
        {
            db.LiveBars.Add(new LiveBar { Symbol = Nifty, Resolution = "1m", BarStartUtc = today.AddMinutes(minute), Open = 25000 + minute,
                High = 25001 + minute, Low = 24999 + minute, Close = 25000.5m + minute, TickCount = 10, UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        }

        db.SaveChanges();
        var service = new LiveDataService(db, new NoCatalog(), new MarketSessionService(new OpenCalendar()));
        var controller = new LiveDataController(null!, null!, null!, new GetLatestQuoteUseCase(service), new GetAllLatestQuotesUseCase(service),
            null!, null!, null!, null!, null!, null!, null!, null!, new GetRecentBarsUseCase(service), null!);

        // A live runner asks with no replay flag and no untilUtc, as it always has.
        var quote = Assert.IsType<OkObjectResult>(await controller.GetLatest(Nifty, default, replay: false, book, recapDate: null));
        Assert.Equal(25000m, Assert.IsType<LiveQuoteResponse>(quote.Value).LastTradedPrice);
        var all = Assert.IsType<OkObjectResult>(await controller.GetAllLatest(default, replay: false, book, recapDate: null));
        Assert.Equal(25000m, Assert.IsAssignableFrom<IEnumerable<LiveQuoteResponse>>(all.Value).Single().LastTradedPrice);

        var bars = Assert.IsType<OkObjectResult>(await controller.GetRecentBars(Nifty, "1m", 5, default, untilUtc: null, book, service));
        var rows = Assert.IsAssignableFrom<IReadOnlyList<LiveBarResponse>>(bars.Value);
        Assert.Equal(new[] { today.AddMinutes(2), today.AddMinutes(1), today }, rows.Select(b => b.BarStartUtc));
        Assert.Equal(25002.5m, rows[0].Close);
    }

    // ---------------------------------------------------------------- helpers

    private static PaperTradingService Engine(TradingDbContext db, IMarketReplayBook book) =>
        new(db, RecapClockTests.Inert<IRiskManagementService>.Create(), new PositionGreeksTests.FixedLots(65), replayBook: book);

    private static void LiveQuote(TradingDbContext db, string symbol, decimal ltp)
    {
        db.LiveQuotesLatest.Add(new LiveQuoteLatest
        {
            Symbol = symbol, LastTradedPrice = ltp, BidPrice = ltp - 0.5m, AskPrice = ltp + 0.5m, UpdatedUtc = DateTime.UtcNow,
            ExchangeTimestampUtc = DateTime.UtcNow, SourceKey = "dhan", DataType = "symbolUpdate", RawPayload = "{}",
        });
        db.SaveChanges();
    }

    private static long NewRun(TradingDbContext db, string parameters, string strategy = "Ghost")
    {
        var run = new SimulationRun
        {
            Mode = PaperTradingService.LivePaperMode, Symbol = Nifty, Status = "Running", StrategyName = strategy,
            ParametersJson = parameters, UserId = 7, InitialCapital = 500_000m, CreatedUtc = DateTime.UtcNow.AddHours(-1),
            StartedUtc = DateTime.UtcNow.AddHours(-1),
        };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private static long OpenLeg(TradingDbContext db, long runId, string symbol, string side, decimal price)
    {
        var at = DateTime.UtcNow.AddMinutes(-30);
        db.PaperOrders.Add(new PaperOrder
        {
            SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = symbol, Side = side, Quantity = 2,
            OrderType = "MARKET_SIM", Status = "Filled", RequestedPrice = price, FillPrice = price, CreatedUtc = at, FilledUtc = at,
        });
        var position = new PaperPosition
        {
            SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = symbol, Direction = side == "BUY" ? "LONG" : "SHORT",
            Quantity = 2, AveragePrice = price, LastMarkPrice = price, Status = "Open", OpenedUtc = at, UpdatedUtc = at,
        };
        db.PaperPositions.Add(position);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return position.Id;
    }

    private static RunningStrategy Entry(Process process, long runId, string underlying, string spot, DateTime startedUtc) => new(
        StrategyCatalogService.StableId("Ghost"), "Ghost", process, StartedBy: "admin", UserId: 7, StartedUtc: startedUtc,
        RunId: runId, Underlying: underlying, SpotSymbol: spot, Lots: 1, Risk: new RiskRulesDto());

    /// <summary>The replay service as the API registers it, over <paramref name="db"/>, with the player and its channel played.</summary>
    private static ServiceProvider ReplayServices(TradingDbContext db)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var lots = new PositionGreeksTests.FixedLots(65);
            var book = new MarketReplayBook();
            return new MarketReplayService(db, new FakePlayer(), new FakeChannel(), new FakeStopper(), book,
                PositionGreeksTests.Sessions(), new RunPnl(db, lots, new RunCharges(db, lots), book), new FakeRecapFeeds(),
                NullLogger<MarketReplayService>.Instance);
        });
        return services.BuildServiceProvider();
    }

    private static void Store(TradingDbContext db, string state, IReadOnlyList<long> runIds)
    {
        var session = new ReplaySessionState(7, "2026-09-25", 2, "09:15", state, runIds, DateTime.UtcNow, null, null, "admin");
        var row = db.SystemSettings.SingleOrDefault(s => s.Key == SystemSettingKeys.ReplaySession);
        if (row is null)
        {
            row = new SystemSetting { Key = SystemSettingKeys.ReplaySession, CreatedUtc = DateTime.UtcNow };
            db.SystemSettings.Add(row);
        }

        row.Value = System.Text.Json.JsonSerializer.Serialize(session, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        row.UpdatedUtc = DateTime.UtcNow;
        db.SaveChanges();
    }
}
