using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Intraday vs carry forward, one tick per position.
/// </summary>
/// <remarks>
/// The owner, 27 Sep: "Put a carry-forward system in strategies and in manual
/// orders: if I want to carry forward, there should be a tick there and
/// ticking it is enough. In strategies, even a single leg — I should be able to
/// do it." Built like a broker's MIS (intraday) and NRML (carry forward):
/// unticked manual positions are squared off at their exchange's close; a
/// strategy's ticked legs move to the owner's manual book when — and only
/// when — the market close stops the run. These pin who may change the tick,
/// what the close does with it, what every other stop does with it, and that
/// the run, the book, their charges and the history still add up afterwards.
/// </remarks>
public class CarryForwardTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string Call = "NSE:NIFTY2692924500CE";
    private const string Put = "NSE:NIFTY2692924500PE";
    private const string CrudeFuture = "MCX:CRUDEOIL26OCTFUT";
    private const int LotSize = 75;

    private static DateTime Ist(int d, int h, int m) => IstTime.FromIst(new DateTime(2026, 9, d, h, m, 0));

    // Monday 28 Sep 2026, a trading session.
    private static readonly DateTime MondayMorning = Ist(28, 10, 0);
    private static readonly DateTime MondayClose = Ist(28, 15, 30);

    // ======================================================== the tick itself

    [Fact]
    public async Task A_position_is_intraday_until_someone_ticks_it()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        await desk.InScope(sp => sp.GetRequiredService<IPaperTradingService>().CreateSignalAsync(new CreateSimulationSignalRequest
        {
            SimulationRunId = run,
            StrategyName = "Ghost",
            SignalType = "OPEN_GROUP",
            TimestampUtc = DateTime.UtcNow,
            GroupId = "G1",
            Legs = new List<SimulationSignalLegRequest> { new() { Symbol = Call, Side = "SELL", Quantity = 2, Price = 100m } }
        }));

        var position = desk.Positions(run).Single();
        Assert.False(position.CarryForward);
        Assert.Null(position.CarryForwardChangedUtc);
        Assert.Null(position.CarriedFromPositionId);
    }

    [Fact]
    public async Task The_owner_and_an_admin_may_tick_a_leg_and_another_trader_gets_403()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long leg = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning);

        var stranger = await desk.PutCarry(run, leg, true, Desk.OtherTrader, "mallory");
        Assert.IsType<ForbidResult>(stranger);
        Assert.False(desk.Position(leg).CarryForward);

        var owner = await desk.PutCarry(run, leg, true, Desk.Owner, "trader");
        Assert.IsType<OkObjectResult>(owner);
        Assert.True(desk.Position(leg).CarryForward);

        var admin = await desk.PutCarry(run, leg, false, Desk.AdminId, "admin", admin: true);
        Assert.IsType<OkObjectResult>(admin);
        Assert.False(desk.Position(leg).CarryForward);
    }

    [Fact]
    public async Task A_change_is_recorded_with_who_and_when_and_leaves_the_marks_age_alone()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long leg = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning);
        var markAge = desk.Position(leg).UpdatedUtc;

        var before = DateTime.UtcNow;
        var ok = Assert.IsType<OkObjectResult>(await desk.PutCarry(run, leg, true, Desk.Owner, "trader"));

        var position = desk.Position(leg);
        Assert.True(position.CarryForwardChangedUtc >= before);
        // The views read UpdatedUtc as the age of the last price.
        Assert.Equal(markAge, position.UpdatedUtc);

        var signal = desk.Signals(run).Single(x => x.SignalType == PaperTradingService.CarryForwardSignalType);
        Assert.Equal("G1", signal.GroupId);
        Assert.True(signal.TimestampUtc >= before);
        Assert.Equal("trader", Read(signal.MetadataJson, "by"));
        Assert.Equal(
            "Carry forward ticked by trader: NIFTY 24500 CE · 29 Sep — moves to the manual book at the close instead of being squared off",
            Read(signal.MetadataJson, "reason"));
        Assert.Contains("moves to the manual book", JsonSerializer.Serialize(ok.Value));

        // Asking again for what is already so writes nothing.
        var again = Assert.IsType<OkObjectResult>(await desk.PutCarry(run, leg, true, Desk.Owner, "trader"));
        Assert.Contains("\"changed\":false", JsonSerializer.Serialize(again.Value));
        Assert.Single(desk.Signals(run), x => x.SignalType == PaperTradingService.CarryForwardSignalType);
    }

    [Fact]
    public async Task Only_an_open_position_of_a_running_live_run_can_be_ticked()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long closed = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, status: "Closed");
        Assert.IsType<ConflictObjectResult>(await desk.PutCarry(run, closed, true, Desk.Owner, "trader"));

        long carried = desk.Fill(run, "Ghost", "G2", Put, "SELL", 2, 80m, MondayMorning, status: PaperTradingService.CarriedStatus);
        Assert.IsType<ConflictObjectResult>(await desk.PutCarry(run, carried, true, Desk.Owner, "trader"));

        long stopped = desk.NewRun("Ghost", Desk.Owner, status: "Stopped");
        long left = desk.Fill(stopped, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning);
        Assert.IsType<ConflictObjectResult>(await desk.PutCarry(stopped, left, true, Desk.Owner, "trader"));

        // A recap replays an earlier session: its fills must never reach the real book.
        long recap = desk.NewRun("Ghost", Desk.Owner, parameters: "{\"session\":\"recap\",\"recap_date\":\"2026-09-11\"}");
        long replayed = desk.Fill(recap, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning);
        Assert.IsType<ConflictObjectResult>(await desk.PutCarry(recap, replayed, true, Desk.Owner, "trader"));

        // Someone else's position id, asked for through this run.
        long other = desk.NewRun("Ghost", Desk.Owner);
        long foreign = desk.Fill(other, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning);
        Assert.IsType<ConflictObjectResult>(await desk.PutCarry(run, foreign, true, Desk.Owner, "trader"));

        Assert.All(new[] { closed, carried, left, replayed, foreign }, id => Assert.False(desk.Position(id).CarryForward));
        Assert.DoesNotContain(desk.AllSignals(), x => x.SignalType == PaperTradingService.CarryForwardSignalType);
    }

    [Fact]
    public void The_migration_keeps_carrying_what_the_manual_books_hold_when_it_runs()
    {
        // Every manual position was carried until the tick existed. Unticked
        // now means intraday, so without this the new API's first minute would
        // square off everything held in a book at the deploy.
        var up = new AlgoTrading.Infrastructure.Persistence.Migrations.PositionCarryForward().UpOperations;

        var column = up.OfType<AddColumnOperation>().Single(x => x.Name == nameof(PaperPosition.CarryForward));
        Assert.False(column.IsNullable);
        Assert.Equal(false, column.DefaultValue);

        string sql = Assert.Single(up.OfType<SqlOperation>()).Sql;
        Assert.Contains("SET \"CarryForward\" = TRUE", sql);
        Assert.Contains($"r.\"StrategyName\" = '{ManualOrdersController.BookStrategyName}'", sql);
        Assert.Contains("p.\"Status\" = 'Open'", sql);
    }

    [Fact]
    public void The_live_view_offers_the_tick_only_where_it_can_mean_something()
    {
        var live = new SimulationRun { Status = "Running", ParametersJson = "{}" };
        Assert.True(PositionCarryForward.IsChangeable(live, active: true));
        // A row still Running with no runner behind it (lost across a restart).
        Assert.False(PositionCarryForward.IsChangeable(live, active: false));
        Assert.False(PositionCarryForward.IsChangeable(new SimulationRun { Status = "Stopping", ParametersJson = "{}" }, true));
        Assert.False(PositionCarryForward.IsChangeable(
            new SimulationRun { Status = "Running", ParametersJson = "{\"session\":\"recap\"}" }, true));
    }

    // =================================================== the manual ticket

    [Fact]
    public async Task The_ticket_opens_a_position_with_the_tick_it_was_given()
    {
        using var desk = new Desk();
        desk.Equity("NSE:SBIN-EQ", bid: 702.9m, ask: 703m);

        await desk.PlaceManual("NSE:SBIN-EQ", "BUY", carryForward: true);
        await desk.PlaceManual("NSE:SBIN-EQ", "BUY", carryForward: false);
        await desk.PlaceManual("NSE:SBIN-EQ", "BUY");   // an older console sends no tick

        long book = desk.BookOf(Desk.Owner)!.Value;
        var positions = desk.Positions(book).OrderBy(x => x.Id).ToList();

        // Each ticket order is a position of its own, with its own tick: an
        // unticked order never turns a carried position intraday.
        Assert.Equal(3, positions.Count);
        Assert.Equal(3, positions.Select(x => x.GroupId).Distinct().Count());
        Assert.Equal(new[] { true, false, false }, positions.Select(x => x.CarryForward));
    }

    // ========================================= the manual book at the close

    [Fact]
    public async Task At_15_30_an_unticked_NSE_position_is_squared_off_and_a_ticked_one_is_kept()
    {
        using var desk = new Desk();
        long book = desk.NewBook(Desk.Owner);
        long intraday = desk.Fill(book, "Manual", "MANUAL-1", Call, "BUY", 1, 120m, MondayMorning);
        long carried = desk.Fill(book, "Manual", "MANUAL-2", Put, "BUY", 1, 90m, MondayMorning, carry: true);
        desk.Quote(Call, 131m);

        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 15, 29)));
        Assert.Equal("Open", desk.Position(intraday).Status);

        Assert.Equal(1, await desk.SquareOffIntraday(Ist(28, 15, 31)));

        var closed = desk.Position(intraday);
        Assert.Equal("Closed", closed.Status);
        Assert.Equal(131m, closed.LastMarkPrice);
        Assert.Equal((131m - 120m) * LotSize, closed.RealizedPnl);
        var signal = desk.Signals(book).Single(x => x.GroupId == "MANUAL-1");
        Assert.Equal("CLOSE_GROUP", signal.SignalType);
        Assert.Equal("Intraday — squared off at the close (15:30 IST)", Read(signal.MetadataJson, "reason"));
        Assert.Equal(ManualIntradaySquareOff.By, Read(signal.MetadataJson, "by"));

        Assert.Equal("Open", desk.Position(carried).Status);
    }

    [Fact]
    public async Task An_MCX_position_trades_the_evening_and_goes_at_the_MCX_close()
    {
        using var desk = new Desk();
        long book = desk.NewBook(Desk.Owner);
        long crude = desk.Fill(book, "Manual", "MANUAL-1", CrudeFuture, "BUY", 1, 6500m, MondayMorning);
        desk.Quote(CrudeFuture, 6540m);

        // The NSE close is not crude's.
        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 15, 31)));
        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 23, 29)));
        Assert.Equal("Open", desk.Position(crude).Status);

        // 23:30 IST while New York is on summer time.
        Assert.Equal(1, await desk.SquareOffIntraday(Ist(28, 23, 31)));
        Assert.Equal("Intraday — squared off at the MCX close (23:30 IST)",
            Read(desk.Signals(book).Single().MetadataJson, "reason"));
    }

    [Fact]
    public async Task A_restart_after_the_close_still_squares_off_the_days_intraday_positions_once()
    {
        using var desk = new Desk();
        long book = desk.NewBook(Desk.Owner);
        long intraday = desk.Fill(book, "Manual", "MANUAL-1", Call, "BUY", 1, 120m, MondayMorning);

        // The API was down from 15:00 and its first minute back is 20:05. The
        // rule keeps no "done today" memory, so nothing about the restart
        // matters.
        Assert.Equal(1, await desk.SquareOffIntraday(Ist(28, 20, 5)));
        Assert.Equal("Closed", desk.Position(intraday).Status);
        Assert.Equal("Intraday — squared off at the close (15:30 IST)", Read(desk.Signals(book).Single().MetadataJson, "reason"));

        // And the next minute changes nothing.
        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 20, 6)));
        Assert.Single(desk.Signals(book));
    }

    [Fact]
    public async Task A_position_unticked_in_the_morning_is_intraday_for_that_day_not_squared_off_at_once()
    {
        using var desk = new Desk();
        long book = desk.NewBook(Desk.Owner);
        // Carried since Friday.
        long held = desk.Fill(book, "Manual", "MANUAL-1", Call, "BUY", 1, 120m, Ist(25, 11, 0), carry: true);

        await desk.InScope(sp => sp.GetRequiredService<IPaperTradingService>()
            .SetCarryForwardAsync(book, held, false, "{}", Ist(28, 9, 30)));

        // Friday's close lies after its opening, but not after it became intraday.
        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 9, 31)));
        Assert.Equal("Open", desk.Position(held).Status);

        Assert.Equal(1, await desk.SquareOffIntraday(Ist(28, 15, 31)));
        Assert.Equal("Closed", desk.Position(held).Status);
    }

    [Fact]
    public async Task A_position_opened_after_the_close_waits_for_the_next_close()
    {
        using var desk = new Desk();
        long book = desk.NewBook(Desk.Owner);
        long evening = desk.Fill(book, "Manual", "MANUAL-1", Call, "BUY", 1, 120m, Ist(28, 16, 10));

        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 16, 11)));
        Assert.Equal(0, await desk.SquareOffIntraday(Ist(29, 15, 29)));
        Assert.Equal(1, await desk.SquareOffIntraday(Ist(29, 15, 31)));
        Assert.Equal("Closed", desk.Position(evening).Status);
    }

    [Fact]
    public async Task A_tick_that_lands_while_the_sweep_is_on_its_way_is_honoured()
    {
        using var desk = new Desk();
        long book = desk.NewBook(Desk.Owner);
        long position = desk.Fill(book, "Manual", "MANUAL-1", Call, "BUY", 1, 120m, MondayMorning);

        // The sweep read it as intraday; the owner ticked it before the close ran.
        await desk.InScope(sp => sp.GetRequiredService<IPaperTradingService>()
            .SetCarryForwardAsync(book, position, true, "{}", Ist(28, 15, 30)));
        int closed = await desk.InScope(sp => sp.GetRequiredService<IPaperTradingService>()
            .CloseIntradayPositionsAsync(book, new[] { position }, "Intraday — squared off at the close (15:30 IST)", "market-hours"));

        Assert.Equal(0, closed);
        Assert.Equal("Open", desk.Position(position).Status);
    }

    [Fact]
    public async Task The_manual_square_off_never_touches_a_strategy_run()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long leg = desk.Fill(run, "Ghost", "G1", Call, "SELL", 1, 100m, MondayMorning);

        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 15, 31)));
        Assert.Equal("Open", desk.Position(leg).Status);
    }

    [Fact]
    public void The_intraday_rule_asks_each_exchange_for_its_own_close()
    {
        var sessions = PositionGreeksTests.Sessions();
        var due = ManualIntradaySquareOff.DuePositions(sessions, Ist(28, 15, 31), new[]
        {
            new ManualIntradaySquareOff.Candidate(1, 9, Call, MondayMorning, null),
            new ManualIntradaySquareOff.Candidate(2, 9, "BSE:SENSEX2610185000CE", MondayMorning, null),
            new ManualIntradaySquareOff.Candidate(3, 9, CrudeFuture, MondayMorning, null),
        });

        Assert.Equal(new long[] { 1, 2 }, due.Select(x => x.PositionId));
        Assert.Equal(new[] { "NSE", "BSE" }, due.Select(x => x.Exchange));
    }

    // ============================================ a strategy run at the close

    [Fact]
    public async Task The_close_moves_only_the_ticked_leg_to_the_owners_book_and_flattens_the_rest()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long call = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        long put = desk.Fill(run, "Ghost", "G1", Put, "SELL", 2, 80m, MondayMorning);
        desk.Quote(Call, 90m);
        desk.Quote(Put, 70m);
        desk.Register(run, "Ghost", Desk.Owner);
        Assert.Null(desk.BookOf(Desk.Owner));

        var result = await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));

        Assert.True(result.WasRunning);
        Assert.Equal(1, result.Carried);
        Assert.Equal(1, result.Flattened);

        // The unticked leg: squared off at the close, as always.
        var putRow = desk.Position(put);
        Assert.Equal("Closed", putRow.Status);
        Assert.Equal(70m, putRow.LastMarkPrice);

        // The ticked leg: left the run, whole, without a fill.
        var gone = desk.Position(call);
        Assert.Equal(PaperTradingService.CarriedStatus, gone.Status);
        Assert.Equal(2, gone.Quantity);
        Assert.Equal(0m, gone.RealizedPnl);
        Assert.Equal(0m, gone.UnrealizedPnl);
        Assert.DoesNotContain(desk.Orders(run), o => o.Symbol == Call && o.Side == "BUY");

        // …and arrived in a book the close opened for its owner.
        long book = desk.BookOf(Desk.Owner)!.Value;
        var arrived = desk.Positions(book).Single();
        Assert.Equal("Open", arrived.Status);
        Assert.Equal(Call, arrived.Symbol);
        Assert.Equal("SHORT", arrived.Direction);
        Assert.Equal(2, arrived.Quantity);
        Assert.Equal(100m, arrived.AveragePrice);
        Assert.Equal(MondayMorning, arrived.OpenedUtc);
        Assert.True(arrived.CarryForward);
        Assert.Equal(call, arrived.CarriedFromPositionId);
        Assert.Equal(ManualOrdersController.BookStrategyName, arrived.StrategyName);
        Assert.Equal($"CARRY-{run}-G1", arrived.GroupId);
        Assert.Empty(desk.Orders(book));

        var outSignal = desk.Signals(run).Single(x => x.SignalType == PaperTradingService.CarryOutSignalType);
        Assert.Equal("Carried forward to the manual book at the close (15:30 IST): NIFTY 24500 CE · 29 Sep — SELL 2 lots at 100.00",
            Read(outSignal.MetadataJson, "reason"));
        var inSignal = desk.Signals(book).Single(x => x.SignalType == PaperTradingService.CarryInSignalType);
        Assert.Equal($"Carried forward from run #{run} (Ghost) at the close (15:30 IST): NIFTY 24500 CE · 29 Sep — SELL 2 lots at 100.00",
            Read(inSignal.MetadataJson, "reason"));

        // The run ended the way every close ends it.
        Assert.Equal("Stopped", desk.Run(run).Status);
        Assert.Equal("Market closed (15:30 IST)",
            Read(desk.Signals(run).Single(x => x.SignalType == StrategyRunControl.RunStoppedSignalType).MetadataJson, "reason"));
        Assert.Null(desk.Registry.Get(run));

        // Carried overnight, the leg is not the manual book's intraday square-off's to take.
        Assert.Equal(0, await desk.SquareOffIntraday(Ist(28, 15, 32)));
    }

    [Fact]
    public async Task A_carried_leg_joins_the_owners_existing_book()
    {
        using var desk = new Desk();
        long book = desk.NewBook(Desk.Owner);
        desk.Fill(book, "Manual", "MANUAL-1", CrudeFuture, "BUY", 1, 6500m, MondayMorning, carry: true);
        long run = desk.NewRun("Ghost", Desk.Owner);
        desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        desk.Register(run, "Ghost", Desk.Owner);

        await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));

        Assert.Equal(book, desk.BookOf(Desk.Owner));
        Assert.Single(desk.Books());
        Assert.Equal(2, desk.Positions(book).Count(x => x.Status == "Open"));
    }

    [Fact]
    public async Task The_stop_button_squares_off_a_ticked_leg_like_any_other()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long ticked = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        desk.Quote(Call, 90m);
        desk.Register(run, "Ghost", Desk.Owner);

        var result = await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAsync(run, "Stopped by trader", flatten: true, by: "trader"));

        Assert.Equal(1, result.Flattened);
        Assert.Equal(0, result.Carried);
        AssertSquaredOffNotCarried(desk, run, ticked);
    }

    [Fact]
    public async Task A_risk_rule_trip_squares_off_a_ticked_leg_too()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long ticked = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        // −₹7,500 against an overall stop of ₹1,000.
        desk.Quote(Call, 150m);
        desk.Register(run, "Ghost", Desk.Owner, new RiskRulesDto { Overall = new OverallRiskDto { StopLoss = 1_000m } });

        var guard = desk.Guard();
        await guard.StartAsync(CancellationToken.None);
        try
        {
            await desk.WaitUntilStopped(run);
        }
        finally
        {
            await guard.StopAsync(CancellationToken.None);
        }

        Assert.StartsWith("Stop loss hit",
            Read(desk.Signals(run).Single(x => x.SignalType == StrategyRunControl.RunStoppedSignalType).MetadataJson, "reason"));
        AssertSquaredOffNotCarried(desk, run, ticked);
    }

    [Fact]
    public async Task A_runner_that_dies_leaves_no_ticked_leg_behind()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long ticked = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        desk.Quote(Call, 90m);
        var entry = desk.Register(run, "Ghost", Desk.Owner);

        entry.Process.Kill();
        await desk.WaitUntilStopped(run);

        Assert.StartsWith("Runner exited",
            Read(desk.Signals(run).Single(x => x.SignalType == StrategyRunControl.RunStoppedSignalType).MetadataJson, "reason"));
        AssertSquaredOffNotCarried(desk, run, ticked);
    }

    [Fact]
    public async Task A_recap_run_is_squared_off_at_the_close_even_with_a_tick()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner, parameters: "{\"session\":\"recap\",\"recap_date\":\"2026-09-11\"}");
        long ticked = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        desk.Quote(Call, 90m);
        desk.Register(run, "Ghost", Desk.Owner);

        var result = await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));

        Assert.Equal(0, result.Carried);
        AssertSquaredOffNotCarried(desk, run, ticked);
    }

    private static void AssertSquaredOffNotCarried(Desk desk, long run, long ticked)
    {
        var row = desk.Position(ticked);
        Assert.Equal("Closed", row.Status);
        Assert.Equal(0, row.Quantity);
        Assert.Contains(desk.Orders(run), o => o.Symbol == row.Symbol && o.Side == "BUY");
        Assert.DoesNotContain(desk.Signals(run), x => x.SignalType == PaperTradingService.CarryOutSignalType);
        Assert.Null(desk.BookOf(Desk.Owner));
        Assert.Equal("Stopped", desk.Run(run).Status);
    }

    // ================================= P&L, charges and history after a move

    [Fact]
    public async Task After_a_move_the_run_keeps_its_entry_charges_and_the_book_the_trade()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        long call = desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        desk.Fill(run, "Ghost", "G1", Put, "SELL", 2, 80m, MondayMorning);
        desk.Quote(Call, 90m);
        desk.Quote(Put, 70m);
        desk.Register(run, "Ghost", Desk.Owner);

        await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));

        // Tuesday: the owner buys the carried call back at 60.
        long book = desk.BookOf(Desk.Owner)!.Value;
        long arrived = desk.Positions(book).Single().Id;
        desk.Quote(Call, 60m);
        await desk.InScope(sp => sp.GetRequiredService<IPaperTradingService>()
            .ClosePositionsAsync(book, new[] { arrived }, "Squared off by trader", "trader"));

        const int units = 2 * LotSize;
        var schedule = ChargeSchedule.ForSymbol(Call);

        // The run: the put's round trip and the call's ENTRY; its P&L is the put's only.
        decimal runCharges = await desk.Charges(run);
        Assert.Equal(OptionCharges.For(70m * units, (100m + 80m) * units, orders: 3, schedule).Total, runCharges);
        Assert.Equal((80m - 70m) * units, desk.Positions(run).Sum(x => x.RealizedPnl));

        // The book: the call's whole trade, entry to exit, and its exit fill's charges.
        decimal bookCharges = await desk.Charges(book);
        Assert.Equal(OptionCharges.For(60m * units, 0m, orders: 1, schedule).Total, bookCharges);
        Assert.Equal((100m - 60m) * units, desk.Positions(book).Sum(x => x.RealizedPnl));

        // Between them, charged once: what the same four fills cost in one run
        // (to the paisa, each figure being rounded on its own).
        decimal together = OptionCharges.For((70m + 60m) * units, (100m + 80m) * units, orders: 4, schedule).Total;
        Assert.InRange(runCharges + bookCharges - together, -0.05m, 0.05m);

        // The run history reads the same numbers.
        var history = await desk.History();
        var runRow = history.Single(x => x.RunId == run);
        Assert.Equal(1_500m, runRow.RealizedPnl);
        Assert.Equal(runCharges, runRow.Charges);
        Assert.Equal(1_500m - runCharges, runRow.NetPnl);
        Assert.Equal(1, runRow.Trades);            // the put; the carried call is not a trade of the run
        Assert.Equal(0, runRow.OpenPositions);
        var bookRow = history.Single(x => x.RunId == book);
        Assert.Equal(6_000m, bookRow.RealizedPnl);
        Assert.Equal(bookCharges, bookRow.Charges);
        Assert.Equal(1, bookRow.Trades);

        // The run's rows: the carried leg reads as carried, not as a ₹0 trade.
        var runView = await desk.View(run);
        var carriedRow = runView.Positions.Single(x => x.Id == call);
        Assert.Equal(PaperTradingService.CarriedStatus, carriedRow.Status);
        Assert.Equal(2, carriedRow.Lots);
        Assert.Equal(units, carriedRow.Quantity);
        Assert.Null(carriedRow.ExitPrice);
        Assert.Null(carriedRow.PnlPoints);
        Assert.Equal(0m, carriedRow.Pnl);
        Assert.Equal(0m, runView.CapitalUsed);

        // The book's row, closed: its size comes from the leg that arrived.
        var bookView = await desk.View(book);
        var closedRow = bookView.Positions.Single();
        Assert.Equal("Closed", closedRow.Status);
        Assert.Equal(2, closedRow.Lots);
        Assert.Equal(100m, closedRow.EntryPrice);
        Assert.Equal(60m, closedRow.ExitPrice);
        Assert.Equal(6_000m, closedRow.Pnl);
        Assert.Equal(run, closedRow.CarriedFromRunId);
        Assert.Equal("Ghost", closedRow.CarriedFromStrategy);
    }

    [Fact]
    public async Task A_leg_rolled_in_the_same_second_and_then_carried_leaves_the_earlier_rows_size_alone()
    {
        using var desk = new Desk();
        long run = desk.NewRun("Ghost", Desk.Owner);
        var opened = Ist(28, 11, 0);
        var rolled = Ist(28, 14, 0);
        long first = 0, second = 0;
        desk.Seed(db =>
        {
            // Sold 2, bought back and sold 2 again within the same second: a
            // roll. The re-entry is the leg the owner ticks.
            db.PaperOrders.AddRange(
                Order(run, "SELL", 2, 100m, opened),
                Order(run, "BUY", 2, 90m, rolled),
                Order(run, "SELL", 2, 91m, rolled.AddMilliseconds(400)));
            var closedRow = new PaperPosition
            {
                SimulationRunId = run, StrategyName = "Ghost", GroupId = "G1", Symbol = Call, Direction = "SHORT",
                Quantity = 0, AveragePrice = 100m, LastMarkPrice = 90m, RealizedPnl = 10m * 2 * LotSize,
                Status = "Closed", OpenedUtc = opened, ClosedUtc = rolled, UpdatedUtc = rolled
            };
            var reentry = new PaperPosition
            {
                SimulationRunId = run, StrategyName = "Ghost", GroupId = "G1", Symbol = Call, Direction = "SHORT",
                Quantity = 2, AveragePrice = 91m, LastMarkPrice = 91m, Status = "Open", CarryForward = true,
                OpenedUtc = rolled.AddMilliseconds(400), UpdatedUtc = rolled.AddMilliseconds(400)
            };
            db.PaperPositions.AddRange(closedRow, reentry);
            db.SaveChanges();
            first = closedRow.Id;
            second = reentry.Id;
        });
        desk.Register(run, "Ghost", Desk.Owner);

        await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));

        var view = await desk.View(run);
        // To the run's own fills the carried row never closed. Read as closed,
        // the rows stopped matching the fills and the earlier row fell back to
        // counting every sell in its window: 4 lots, twice what it traded.
        Assert.Equal(2, view.Positions.Single(x => x.Id == first).Lots);
        Assert.Equal(2, view.Positions.Single(x => x.Id == second).Lots);
        Assert.Equal(PaperTradingService.CarriedStatus, view.Positions.Single(x => x.Id == second).Status);

        static PaperOrder Order(long runId, string side, int lots, decimal price, DateTime at) => new()
        {
            SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = Call, Side = side, Quantity = lots,
            OrderType = "MARKET_SIM", Status = "Filled", RequestedPrice = price, FillPrice = price, CreatedUtc = at, FilledUtc = at
        };
    }

    // ================================================ held overnight in the book

    [Fact]
    public async Task A_carried_leg_is_settled_at_expiry_like_any_book_position()
    {
        using var desk = new Desk();
        desk.Contract(Call, "CE", 24500m, new DateOnly(2026, 9, 29));
        long run = desk.NewRun("Ghost", Desk.Owner);
        desk.Fill(run, "Ghost", "G1", Call, "BUY", 2, 120m, MondayMorning, carry: true);
        desk.Register(run, "Ghost", Desk.Owner);
        await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));
        long book = desk.BookOf(Desk.Owner)!.Value;

        // Tuesday is expiry; NIFTY closes at 24,650.35.
        desk.Bar(Nifty, Ist(29, 15, 29), 24_650.35m);
        var swept = await desk.InScope(sp => sp.GetRequiredService<ExpirySettler>()
            .SettleDueAsync(Ist(29, 15, 36), CancellationToken.None));

        Assert.Equal(1, swept.Settled);
        var settled = desk.Positions(book).Single();
        Assert.Equal("Closed", settled.Status);
        Assert.Equal(150.35m, settled.LastMarkPrice);
        // From the entry the run paid, in the book that now holds it.
        Assert.Equal((150.35m - 120m) * 2 * LotSize, settled.RealizedPnl);
        Assert.Equal(0m, desk.Positions(run).Sum(x => x.RealizedPnl));
    }

    [Fact]
    public async Task A_carried_leg_is_kept_on_the_feed_overnight()
    {
        using var desk = new Desk();
        desk.Contract(Call, "CE", 24500m, new DateOnly(2026, 9, 29));
        long run = desk.NewRun("Ghost", Desk.Owner);
        desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        desk.Register(run, "Ghost", Desk.Owner);
        await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));

        var missing = await desk.InScope(sp => sp.GetRequiredService<ExpirySettler>()
            .CarriedSymbolsOffTheFeedAsync(Ist(28, 20, 0), CancellationToken.None));

        Assert.Equal(new[] { Call }, missing);
    }

    [Fact]
    public async Task A_carried_leg_shows_its_greeks_in_the_book()
    {
        using var desk = new Desk();
        desk.Contract(Call, "CE", 24500m, new DateOnly(2026, 9, 29));
        long run = desk.NewRun("Ghost", Desk.Owner);
        desk.Fill(run, "Ghost", "G1", Call, "SELL", 2, 100m, MondayMorning, carry: true);
        desk.Register(run, "Ghost", Desk.Owner);
        await desk.InScope(sp => sp.GetRequiredService<StrategyRunControl>()
            .StopAtMarketCloseAsync(run, "Market closed (15:30 IST)", MondayClose));
        long book = desk.BookOf(Desk.Owner)!.Value;
        desk.Quote(Call, 95m, theta: -12.5m, delta: 0.52m, vega: 8.2m, iv: 13.6m);

        var view = await desk.View(book);

        var row = view.Positions.Single();
        Assert.Equal("Open", row.Status);
        Assert.True(row.CarryForward);
        Assert.NotNull(row.Greeks);
        Assert.Equal(PositionGreeks.SourceFeed, row.Greeks!.Source);
        // A written option collects its theta: +12.5 × 150 a day.
        Assert.Equal(1_875m, row.Greeks.ThetaRupeesPerDay);
    }

    // ================================================================ helpers

    private static string? Read(string metadataJson, string property) => SignalMetadata.ReadString(metadataJson, property);

    /// <summary>
    /// The pieces of the desk these rules run in: a shared in-memory database,
    /// the paper engine, the registry of runners and the stop pipeline, each
    /// resolved per scope as the API does. Runners are stand-in processes that
    /// sleep, so a stop has something real to terminate. Given a
    /// <c>replayBook</c>, a market replay's book is on the desk too, as the API
    /// registers it, and <c>more</c> adds what else a test needs there
    /// (LiveRunsBesideReplayTests).
    /// </summary>
    internal sealed class Desk : IDisposable
    {
        public const long Owner = 7;
        public const long OtherTrader = 8;
        public const long AdminId = 1;

        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"carry-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;
        private readonly List<Process> _processes = new();
        private readonly string _emptyEngine = Directory.CreateTempSubdirectory("carry-engine-").FullName;

        public Desk(IMarketReplayBook? replayBook = null, Action<IServiceCollection>? more = null)
        {
            var services = new ServiceCollection();
            if (replayBook is not null) services.AddSingleton(replayBook);
            more?.Invoke(services);
            services.AddLogging();
            services.AddDbContext<TradingDbContext>(Configure);
            services.AddSingleton<IRiskManagementService>(RecapClockTests.Inert<IRiskManagementService>.Create());
            services.AddSingleton<IProcessSettingsStore>(RecapClockTests.Inert<IProcessSettingsStore>.Create());
            services.AddSingleton<ILotSizeResolver>(new PositionGreeksTests.FixedLots(LotSize));
            services.AddSingleton<IMarketSessionService>(PositionGreeksTests.Sessions());
            services.AddScoped<IPaperTradingService, PaperTradingService>();
            // These tests are about who holds a leg and what it is charged, in
            // round numbers: fills land on the quote exactly. The spread has
            // tests of its own (PaperTradingServiceTests).
            services.Configure<PaperFillOptions>(o => o.HalfSpreadFraction = 0m);
            services.AddSingleton<StrategyProcessRegistry>();
            services.AddScoped<PositionCarryForward>();
            services.AddScoped<StrategyRunControl>();
            services.AddScoped<ManualIntradaySquareOff>();
            services.AddScoped<RunCharges>();
            services.AddScoped<RunPnl>();
            services.AddScoped<PositionGreeksBuilder>();
            services.AddScoped<PositionViewBuilder>();
            services.AddScoped<ExpirySettler>();
            services.Configure<StrategyRunnerOptions>(o =>
            {
                o.RiskGuardIntervalSeconds = 1;
                o.EngineDirectory = _emptyEngine;
            });
            services.AddSingleton<IWebHostEnvironment>(RecapClockTests.Inert<IWebHostEnvironment>.Create());
            services.AddSingleton<PythonEngineLocator>();
            services.AddSingleton<IProcessProbe, SystemProcessProbe>();
            services.AddSingleton<ISystemNotifier>(RecapClockTests.Inert<ISystemNotifier>.Create());
            _provider = services.BuildServiceProvider();
            Registry = _provider.GetRequiredService<StrategyProcessRegistry>();
        }

        public StrategyProcessRegistry Registry { get; }

        /// <summary>The desk's scopes, as a background service of the API gets them.</summary>
        public IServiceScopeFactory Scopes => _provider.GetRequiredService<IServiceScopeFactory>();

        private void Configure(DbContextOptionsBuilder options) => options
            .UseInMemoryDatabase(_name, _root)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

        private TradingDbContext Db()
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>();
            Configure(options);
            return new TradingDbContext(options.Options);
        }

        public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = _provider.CreateScope();
            return await work(scope.ServiceProvider);
        }

        // ------------------------------------------------------ arranging --

        public long NewRun(string strategy, long userId, string status = "Running", string parameters = "{}")
        {
            using var db = Db();
            var run = new SimulationRun
            {
                UserId = userId, Mode = PaperTradingService.LivePaperMode, Status = status, Symbol = Nifty,
                StrategyName = strategy, ParametersJson = parameters, StartedUtc = MondayMorning.AddMinutes(-30),
                CreatedUtc = MondayMorning.AddMinutes(-30)
            };
            db.SimulationRuns.Add(run);
            db.SaveChanges();
            return run.Id;
        }

        public long NewBook(long userId)
        {
            using var db = Db();
            var book = ManualBook.NewBook(userId, MondayMorning.AddDays(-7));
            db.SimulationRuns.Add(book);
            db.SaveChanges();
            return book.Id;
        }

        /// <summary>An opening fill and the position it opened, as the engine books them.</summary>
        public long Fill(long runId, string strategy, string group, string symbol, string side, int lots, decimal price,
            DateTime atUtc, bool carry = false, string status = "Open")
        {
            using var db = Db();
            db.PaperOrders.Add(new PaperOrder
            {
                SimulationRunId = runId, StrategyName = strategy, GroupId = group, Symbol = symbol, Side = side,
                Quantity = lots, OrderType = "MARKET_SIM", Status = "Filled", RequestedPrice = price, FillPrice = price,
                CreatedUtc = atUtc, FilledUtc = atUtc
            });
            var position = new PaperPosition
            {
                SimulationRunId = runId, StrategyName = strategy, GroupId = group, Symbol = symbol,
                Direction = side == "BUY" ? "LONG" : "SHORT", Quantity = status == "Closed" ? 0 : lots,
                AveragePrice = price, LastMarkPrice = price, Status = status, OpenedUtc = atUtc, UpdatedUtc = atUtc,
                CarryForward = carry
            };
            db.PaperPositions.Add(position);
            db.SaveChanges();
            return position.Id;
        }

        public void Seed(Action<TradingDbContext> seed)
        {
            using var db = Db();
            seed(db);
        }

        public void Quote(string symbol, decimal ltp, decimal? theta = null, decimal? delta = null, decimal? vega = null, decimal? iv = null)
        {
            using var db = Db();
            var row = db.LiveQuotesLatest.SingleOrDefault(x => x.Symbol == symbol);
            if (row is null)
            {
                row = new LiveQuoteLatest { Symbol = symbol, RawPayload = "{}", SourceKey = "dhan" };
                db.LiveQuotesLatest.Add(row);
            }
            row.LastTradedPrice = ltp;
            row.UpdatedUtc = DateTime.UtcNow.AddSeconds(-2);
            row.Theta = theta;
            row.Delta = delta;
            row.Vega = vega;
            row.Gamma = delta is null ? null : 0.0008m;
            row.ImpliedVolatility = iv;
            db.SaveChanges();
        }

        public void Equity(string symbol, decimal bid, decimal ask)
        {
            using var db = Db();
            db.Instruments.Add(new Instrument
            {
                Symbol = symbol, Exchange = "NSE", Segment = "CM", InstrumentType = "EQ", Underlying = "SBIN",
                IsEnabled = true, TickSize = 0.05m
            });
            db.LiveQuotesLatest.Add(new LiveQuoteLatest
            {
                Symbol = symbol, LastTradedPrice = ask, BidPrice = bid, AskPrice = ask,
                UpdatedUtc = DateTime.UtcNow, RawPayload = "{}"
            });
            db.SaveChanges();
        }

        public void Contract(string symbol, string type, decimal strike, DateOnly expiry)
        {
            using var db = Db();
            db.Instruments.Add(new Instrument
            {
                Symbol = symbol, Exchange = "NSE", Segment = "FO", InstrumentType = type, OptionType = type,
                Underlying = "NIFTY", StrikePrice = strike, ExpiryDate = expiry, IsEnabled = true
            });
            db.SaveChanges();
        }

        public void Bar(string symbol, DateTime startUtc, decimal close)
        {
            using var db = Db();
            db.LiveBars.Add(new LiveBar
            {
                Symbol = symbol, Resolution = "1m", BarStartUtc = startUtc,
                Open = close, High = close, Low = close, Close = close, UpdatedUtc = startUtc.AddMinutes(1)
            });
            db.SaveChanges();
        }

        /// <summary>A runner on the desk: a registry entry around a live stand-in process.</summary>
        public RunningStrategy Register(long runId, string name, long userId, RiskRulesDto? risk = null)
        {
            var entry = new RunningStrategy(
                StrategyCatalogService.StableId(name), name, Sleeper(), StartedBy: "admin", UserId: userId,
                StartedUtc: MondayMorning.AddMinutes(-30), RunId: runId, Underlying: "NIFTY", SpotSymbol: Nifty,
                Lots: 2, Risk: risk ?? new RiskRulesDto());
            Assert.True(Registry.TryAdd(entry));
            return entry;
        }

        private Process Sleeper()
        {
            var info = TestSleeper.StartInfo();
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            var process = Process.Start(info)!;
            _processes.Add(process);
            return process;
        }

        public StrategyRiskGuardService Guard() => new(
            Registry,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IOptionsMonitor<StrategyRunnerOptions>>(),
            NullLogger<StrategyRiskGuardService>.Instance);

        public async Task WaitUntilStopped(long runId)
        {
            var until = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < until)
            {
                if (Registry.Get(runId) is null && Run(runId).Status == "Stopped") return;
                await Task.Delay(50);
            }
            Assert.Fail($"Run {runId} was not stopped within 20 s.");
        }

        // -------------------------------------------------------- acting --

        public Task<int> SquareOffIntraday(DateTime nowUtc) =>
            InScope(sp => sp.GetRequiredService<ManualIntradaySquareOff>().SquareOffDueAsync(nowUtc, CancellationToken.None));

        public Task<IActionResult> PutCarry(long runId, long positionId, bool carry, long userId, string userName, bool admin = false) =>
            InScope(async sp =>
            {
                var controller = new StrategyController(
                    sp.GetRequiredService<TradingDbContext>(),
                    null!,                      // catalog
                    Registry,
                    null!,                      // run control
                    null!,                      // engine locator
                    null!,                      // paper trading
                    null!,                      // lot sizes
                    null!,                      // position views
                    null!,                      // history
                    null!,                      // charges
                    null!,                      // orders
                    null!,                      // watchlist
                    null!,                      // notifier
                    null!,                      // strategy access
                    Options.Create(new StrategyRunnerOptions()),
                    NullLogger<StrategyController>.Instance)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(userId, userName, admin) } }
                };
                return await controller.SetCarryForward(runId, positionId, new StrategyController.CarryForwardRequest(carry),
                    sp.GetRequiredService<PositionCarryForward>(), CancellationToken.None);
            });

        public Task PlaceManual(string symbol, string side, bool? carryForward = null) =>
            InScope(async sp =>
            {
                var controller = new ManualOrdersController(
                    sp.GetRequiredService<TradingDbContext>(),
                    sp.GetRequiredService<IPaperTradingService>(),
                    sp.GetRequiredService<ILotSizeResolver>(),
                    null!,                      // watchlist: a quoted symbol is never subscribed
                    NullLogger<ManualOrdersController>.Instance)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = User(Owner, "trader", false) } }
                };
                var request = carryForward is { } carry
                    ? new ManualOrdersController.PlaceManualOrderRequest(symbol, side, 1, null, CarryForward: carry)
                    : new ManualOrdersController.PlaceManualOrderRequest(symbol, side, 1, null);
                var result = await controller.Place(request, CancellationToken.None);
                Assert.IsType<OkObjectResult>(result);
                return 0;
            });

        private static ClaimsPrincipal User(long id, string name, bool admin)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, name) };
            if (admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        // ------------------------------------------------------ reading --

        public PaperPosition Position(long id)
        {
            using var db = Db();
            return db.PaperPositions.AsNoTracking().Single(x => x.Id == id);
        }

        public List<PaperPosition> Positions(long runId)
        {
            using var db = Db();
            return db.PaperPositions.AsNoTracking().Where(x => x.SimulationRunId == runId).ToList();
        }

        public List<PaperOrder> Orders(long runId)
        {
            using var db = Db();
            return db.PaperOrders.AsNoTracking().Where(x => x.SimulationRunId == runId).ToList();
        }

        public List<SimulationSignal> Signals(long runId)
        {
            using var db = Db();
            return db.SimulationSignals.AsNoTracking().Where(x => x.SimulationRunId == runId).OrderBy(x => x.Id).ToList();
        }

        public List<SimulationSignal> AllSignals()
        {
            using var db = Db();
            return db.SimulationSignals.AsNoTracking().ToList();
        }

        public SimulationRun Run(long id)
        {
            using var db = Db();
            return db.SimulationRuns.AsNoTracking().Single(x => x.Id == id);
        }

        public List<SimulationRun> Books()
        {
            using var db = Db();
            return db.SimulationRuns.AsNoTracking()
                .Where(x => x.StrategyName == ManualOrdersController.BookStrategyName).ToList();
        }

        public long? BookOf(long userId) =>
            Books().Where(x => x.UserId == userId && x.Status == "Running").Select(x => (long?)x.Id).SingleOrDefault();

        public Task<decimal> Charges(long runId) =>
            InScope(sp => sp.GetRequiredService<RunCharges>().ForRunAsync(runId, CancellationToken.None));

        /// <summary>The run as the live run page builds it: the engine's rows through the view builder.</summary>
        public Task<(List<LivePositionResponse> Positions, decimal CapitalUsed)> View(long runId) =>
            InScope(async sp =>
            {
                var positions = await sp.GetRequiredService<IPaperTradingService>().GetPaperPositionsAsync(runId);
                var built = await sp.GetRequiredService<PositionViewBuilder>()
                    .BuildAsync<LivePositionResponse>(positions, useLiveQuotes: true, Nifty, CancellationToken.None);
                return (built.Positions, built.CapitalUsed);
            });

        /// <summary>The owner's run history, as GET /api/Strategy/runs builds it.</summary>
        public Task<List<LiveRunSummaryResponse>> History() =>
            InScope(async sp =>
            {
                var options = sp.GetRequiredService<IOptions<StrategyRunnerOptions>>();
                var catalog = new StrategyCatalogService(
                    new PythonEngineLocator(options, RecapClockTests.Inert<IWebHostEnvironment>.Create()),
                    NullLogger<StrategyCatalogService>.Instance);
                var history = new LiveRunHistoryBuilder(
                    sp.GetRequiredService<TradingDbContext>(), Registry, catalog,
                    sp.GetRequiredService<ILotSizeResolver>(), sp.GetRequiredService<RunCharges>(),
                    sp.GetRequiredService<RunPnl>());
                return await history.ListAsync(
                    new LiveRunHistoryFilter(Owner, null, null, null, null, null, 50, 0), CancellationToken.None);
            });

        public void Dispose()
        {
            foreach (var process in _processes)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
                process.Dispose();
            }
            _provider.Dispose();
            try { Directory.Delete(_emptyEngine, recursive: true); } catch (IOException) { }
        }
    }
}
