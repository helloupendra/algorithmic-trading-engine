using System.Security.Claims;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What the console is told, and when: each booked order and fill, each
/// closed, settled or carried position, to the run's owner and the admins,
/// only once the write has committed.
/// </summary>
/// <remarks>
/// The pages polled every few seconds to find out a fill had happened. A
/// desk event is what lets them stop: so it must come for every write a page
/// shows, name the run and its owner, and never come for a write that did not
/// happen — a refused signal, a replay nobody watches, a retry already booked.
/// </remarks>
public class DeskEventTests
{
    private const string Call = "NSE:NIFTY2692925000CE";
    private const string Put = "NSE:NIFTY2692925000PE";
    private const long Owner = 7;

    private static readonly DateTime Morning = IstTime.FromIst(new DateTime(2026, 9, 28, 10, 0, 0));

    // ----------------------------------------------------- orders and fills --

    [Fact]
    public async Task A_booked_signal_tells_its_owner_the_order_then_each_fill()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);
        desk.Quote(Put, 90m, bid: 89.5m, ask: 90.5m);

        await desk.Book("OPEN_GROUP", Leg(Call, "SELL"), Leg(Put, "SELL"));

        var events = desk.Events.All;
        Assert.Equal(new[] { "order", "fill", "fill" }, events.Select(x => x.Kind));
        Assert.All(events, x =>
        {
            Assert.Equal(desk.RunId, x.RunId);
            Assert.Equal(Owner, x.UserId);
            Assert.Equal(DateTimeKind.Utc, x.AtUtc.Kind);
        });
        Assert.Null(events[0].Symbol);                                   // two contracts, so no one symbol
        Assert.Equal("OPEN_GROUP: 2 legs filled", events[0].Detail);
        Assert.Equal(new[] { Call, Put }, events.Skip(1).Select(x => x.Symbol));
        Assert.Equal("SELL 2 at 99.50", events[1].Detail);
    }

    [Fact]
    public async Task A_close_that_takes_a_leg_to_zero_says_the_position_closed()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);
        await desk.Book("OPEN_GROUP", Leg(Call, "SELL"));
        desk.Events.Clear();

        await desk.Book("CLOSE_GROUP", Leg(Call, "BUY"));

        Assert.Equal(new[] { "order", "fill", "position" }, desk.Events.All.Select(x => x.Kind));
        Assert.All(desk.Events.All, x => Assert.Equal(Call, x.Symbol));
        Assert.Equal("Position closed", desk.Events.Of(DeskEventKinds.Position).Single().Detail);
    }

    [Fact]
    public async Task A_leg_closed_by_hand_carries_the_reason()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);
        await desk.Book("OPEN_GROUP", Leg(Call, "SELL"));
        desk.Events.Clear();

        int closed = await desk.Service.ClosePositionsAsync(desk.RunId, desk.OpenPositionIds(), "Squared off by admin", "admin");

        Assert.Equal(1, closed);
        Assert.Equal(new[] { "order", "fill", "position" }, desk.Events.All.Select(x => x.Kind));
        Assert.Equal("CLOSE_GROUP: 1 leg filled — Squared off by admin", desk.Events.Of(DeskEventKinds.Order).Single().Detail);
        Assert.Equal("Position closed — Squared off by admin", desk.Events.Of(DeskEventKinds.Position).Single().Detail);
    }

    [Fact]
    public void A_typed_reason_is_cut_to_a_line()
    {
        var deskEvent = new DeskEvent(DeskEventKinds.Position, 1, Owner, Call, DateTime.UtcNow, "Position closed — " + new string('x', 500));

        Assert.Equal(DeskEvent.MaxDetailLength, deskEvent.Detail!.Length);
        Assert.EndsWith("x…", deskEvent.Detail);
        Assert.Null((deskEvent with { Detail = null }).Detail);
    }

    [Fact]
    public async Task A_refused_signal_tells_nobody()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);
        desk.SetRunStatus("Stopping");

        await Assert.ThrowsAsync<InvalidOperationException>(() => desk.Book("OPEN_GROUP", Leg(Call, "SELL")));

        Assert.Empty(desk.Events.All);
    }

    [Fact]
    public async Task A_retry_of_a_booked_signal_is_not_announced_again()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);

        await desk.Book("OPEN_GROUP", clientSignalId: "b1f0", Leg(Call, "SELL"));
        await desk.Book("OPEN_GROUP", clientSignalId: "b1f0", Leg(Call, "SELL"));

        Assert.Single(desk.Events.Of(DeskEventKinds.Order));
        Assert.Single(desk.Events.Of(DeskEventKinds.Fill));
    }

    [Fact]
    public async Task A_replay_books_its_fills_and_tells_nobody()
    {
        using var desk = new PaperDesk(mode: PaperTradingService.OfflineReplayMode);

        await desk.Book("OPEN_GROUP", Leg(Call, "SELL", price: 100m));
        await desk.Service.FlattenRunAsync(desk.RunId, "Backtest finished");

        Assert.Equal(2, desk.Orders());
        Assert.Empty(desk.Events.All);
    }

    // --------------------------------------------- positions, carry, risk --

    [Fact]
    public async Task A_carry_tick_is_announced_once_per_change()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);
        await desk.Book("OPEN_GROUP", Leg(Call, "SELL"));
        long position = desk.OpenPositionIds().Single();
        desk.Events.Clear();

        Assert.Equal(CarryForwardUpdate.Changed, await desk.Service.SetCarryForwardAsync(desk.RunId, position, true, "{}", Morning));
        Assert.Equal(CarryForwardUpdate.Unchanged, await desk.Service.SetCarryForwardAsync(desk.RunId, position, true, "{}", Morning));

        var carry = Assert.Single(desk.Events.All);
        Assert.Equal((DeskEventKinds.Carry, desk.RunId, (long?)Owner, Call), (carry.Kind, carry.RunId, carry.UserId, carry.Symbol));
        Assert.Equal("Carry forward ticked: held past the close", carry.Detail);
    }

    [Fact]
    public async Task An_expiry_settlement_is_announced_to_the_owner()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);
        await desk.Book("OPEN_GROUP", Leg(Call, "SELL"));
        desk.Events.Clear();

        Assert.True(await desk.Service.SettleExpiredPositionAsync(desk.RunId, desk.OpenPositionIds().Single(), 150.35m, "{}", Morning));

        var settled = Assert.Single(desk.Events.All);
        Assert.Equal((DeskEventKinds.Position, desk.RunId, (long?)Owner, Call), (settled.Kind, settled.RunId, settled.UserId, settled.Symbol));
        Assert.Equal("Settled at expiry at 150.35", settled.Detail);
    }

    [Fact]
    public async Task The_kill_switch_tells_each_owner_their_run_was_flattened()
    {
        using var desk = new PaperDesk();
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);
        await desk.Book("OPEN_GROUP", Leg(Call, "SELL"));
        desk.Events.Clear();

        await desk.Service.FlattenAllPositionsAsync();

        var risk = Assert.Single(desk.Events.Of(DeskEventKinds.Risk));
        Assert.Equal((desk.RunId, (long?)Owner), (risk.RunId, risk.UserId));
        Assert.Equal("Kill switch: 1 position squared off", risk.Detail);
        Assert.Single(desk.Events.Of(DeskEventKinds.Fill));
    }

    [Fact]
    public async Task A_manual_order_tells_its_owner_the_fill_then_its_levels_and_carry()
    {
        using var desk = new PaperDesk();
        desk.Instrument(Call);
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m, updatedUtc: DateTime.UtcNow);

        var controller = desk.ManualTicket(Owner);
        var result = await controller.Place(
            new ManualOrdersController.PlaceManualOrderRequest(Call, "BUY", 1, null, StopLossPrice: 90m, TargetPrice: 120m, CarryForward: true),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        long book = desk.BookOf(Owner);
        Assert.NotEqual(desk.RunId, book);
        Assert.Equal(new[] { "order", "fill", "position", "carry" }, desk.Events.All.Select(x => x.Kind));
        Assert.All(desk.Events.All, x =>
        {
            Assert.Equal(book, x.RunId);
            Assert.Equal(Owner, x.UserId);
            Assert.Equal(Call, x.Symbol);
        });
        Assert.Equal("BUY 1 at 100.50", desk.Events.Of(DeskEventKinds.Fill).Single().Detail);
        Assert.Equal("Own levels set: stop-loss 90.00, target 120.00", desk.Events.Of(DeskEventKinds.Position).Single().Detail);
    }

    [Fact]
    public async Task A_publisher_that_throws_never_fails_the_fill()
    {
        using var desk = new PaperDesk(events: new ThrowingDeskEvents());
        desk.Quote(Call, 100m, bid: 99.5m, ask: 100.5m);

        await desk.Book("OPEN_GROUP", Leg(Call, "SELL"));

        Assert.Single(desk.OpenPositionIds());
    }

    // ------------------------------------------------------------ delivery --

    [Fact]
    public async Task An_event_goes_to_the_admins_and_to_the_owner_without_the_admins_twice()
    {
        var subs = new LiveFeedSubscriptions();
        subs.Connect("admin-tab", 1, isAdmin: true);
        subs.Connect("owner-tab", Owner, isAdmin: false);
        var hub = new RecordingHubContext();
        var publisher = new SignalRDeskEventPublisher(hub, subs, NullLogger<SignalRDeskEventPublisher>.Instance);

        await publisher.SendAsync(new DeskEvent(DeskEventKinds.Fill, 42, Owner, Call,
            new DateTime(2026, 9, 28, 4, 30, 0, DateTimeKind.Unspecified), "SELL 2 at 99.50"));

        Assert.Equal(new[] { "group:role:admin", "group:user:7 except admin-tab" }, hub.All.Select(x => x.Target));
        Assert.All(hub.All, x => Assert.Equal("DeskEvent", x.Method));

        var payload = JsonSerializer.SerializeToElement(hub.All[0].Args[0], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "kind", "runId", "userId", "symbol", "atUtc", "detail" }, payload.EnumerateObject().Select(x => x.Name));
        Assert.Equal("fill", payload.GetProperty("kind").GetString());
        Assert.Equal(42, payload.GetProperty("runId").GetInt64());
        Assert.Equal("2026-09-28T04:30:00Z", payload.GetProperty("atUtc").GetString());
    }

    [Fact]
    public async Task The_owner_is_sent_the_event_while_an_admin_connection_is_still_being_written()
    {
        // A group send completes when every member's write has. The owner's
        // used to wait for the admins': one admin phone on a bad network held
        // every trader's fills until its socket drained or timed out.
        var subs = new LiveFeedSubscriptions();
        subs.Connect("admin-phone", 1, isAdmin: true);
        subs.Connect("owner-tab", Owner, isAdmin: false);
        var hub = new RecordingHubContext();
        var stalled = new TaskCompletionSource();
        hub.OnSend = target => target == "group:role:admin" ? stalled.Task : Task.CompletedTask;
        var publisher = new SignalRDeskEventPublisher(hub, subs, NullLogger<SignalRDeskEventPublisher>.Instance);

        var sending = publisher.SendAsync(new DeskEvent(DeskEventKinds.Fill, 42, Owner, Call, DateTime.UtcNow, "SELL 2 at 99.50"));

        Assert.Equal(new[] { "group:user:7 except admin-phone" }, hub.All.Select(x => x.Target));
        Assert.False(sending.IsCompleted);

        stalled.SetResult();
        await sending;
        Assert.Equal(2, hub.All.Count);
    }

    [Fact]
    public async Task An_event_without_an_owner_goes_to_the_admins_only_and_a_failed_send_is_swallowed()
    {
        var subs = new LiveFeedSubscriptions();
        var hub = new RecordingHubContext();
        var publisher = new SignalRDeskEventPublisher(hub, subs, NullLogger<SignalRDeskEventPublisher>.Instance);

        await publisher.SendAsync(new DeskEvent(DeskEventKinds.Risk, null, null, null, DateTime.UtcNow, "Kill switch activated by admin"));
        Assert.Equal(new[] { "group:role:admin" }, hub.All.Select(x => x.Target));

        hub.OnSend = _ => throw new IOException("connection reset");
        publisher.Publish(new DeskEvent(DeskEventKinds.Run, 42, Owner, null, DateTime.UtcNow, "Stopped"));
        await publisher.SendAsync(new DeskEvent(DeskEventKinds.Run, 42, Owner, null, DateTime.UtcNow, "Stopped"));
    }

    // ------------------------------------------------------------- helpers --

    private static SimulationSignalLegRequest Leg(string symbol, string side, decimal? price = null)
        => new() { Symbol = symbol, Side = side, Quantity = 2, Price = price };

    private sealed class ThrowingDeskEvents : IDeskEventPublisher
    {
        public void Publish(DeskEvent deskEvent) => throw new InvalidOperationException("the hub is down");
    }

    /// <summary>One run of user 7 over an in-memory database, with a publisher that keeps what it is told.</summary>
    private sealed class PaperDesk : IDisposable
    {
        private readonly TradingDbContext _db;
        private readonly PaperTradingPublisherClock _clock = new(Morning);

        public PaperDesk(string mode = PaperTradingService.LivePaperMode, IDeskEventPublisher? events = null)
        {
            _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase($"desk-events-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            var run = new SimulationRun
            {
                UserId = Owner, Mode = mode, Symbol = "NSE:NIFTY50-INDEX", Status = "Running",
                StrategyName = "Ghost", ParametersJson = "{}"
            };
            _db.SimulationRuns.Add(run);
            _db.SaveChanges();
            RunId = run.Id;

            Publisher = events ?? Events;
            Service = new PaperTradingService(
                _db,
                RecapClockTests.Inert<IRiskManagementService>.Create(),
                new PositionGreeksTests.FixedLots(65),
                PositionGreeksTests.Sessions(),
                Options.Create(new PaperFillOptions()),
                _clock,
                Publisher);
        }

        public long RunId { get; }

        public RecordingDeskEvents Events { get; } = new();

        private IDeskEventPublisher Publisher { get; }

        public PaperTradingService Service { get; }

        public Task<SimulationSignalResponse> Book(string type, params SimulationSignalLegRequest[] legs)
            => Book(type, clientSignalId: null, legs);

        public Task<SimulationSignalResponse> Book(string type, string? clientSignalId, params SimulationSignalLegRequest[] legs)
            => Service.CreateSignalAsync(new CreateSimulationSignalRequest
            {
                SimulationRunId = RunId,
                StrategyName = "Ghost",
                SignalType = type,
                TimestampUtc = _clock.GetUtcNow().UtcDateTime,
                GroupId = "G1",
                MetadataJson = "{}",
                ClientSignalId = clientSignalId,
                Legs = legs.ToList()
            });

        public void Quote(string symbol, decimal ltp, decimal? bid = null, decimal? ask = null, DateTime? updatedUtc = null)
        {
            var row = _db.LiveQuotesLatest.SingleOrDefault(x => x.Symbol == symbol);
            if (row is null)
            {
                row = new LiveQuoteLatest { Symbol = symbol, RawPayload = "{}", SourceKey = "dhan" };
                _db.LiveQuotesLatest.Add(row);
            }
            row.LastTradedPrice = ltp;
            row.BidPrice = bid;
            row.AskPrice = ask;
            row.UpdatedUtc = updatedUtc ?? Morning.AddSeconds(-2);
            _db.SaveChanges();
        }

        public void Instrument(string symbol)
        {
            _db.Instruments.Add(new Instrument
            {
                Symbol = symbol, Exchange = "NSE", Segment = "FO", InstrumentType = "CE", OptionType = "CE",
                Underlying = "NIFTY", StrikePrice = 25_000m, TickSize = 0.05m, IsEnabled = true,
                ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
            });
            _db.SaveChanges();
        }

        public void SetRunStatus(string status)
        {
            var run = _db.SimulationRuns.Single(x => x.Id == RunId);
            run.Status = status;
            _db.SaveChanges();
        }

        public List<long> OpenPositionIds()
            => _db.PaperPositions.AsNoTracking().Where(x => x.SimulationRunId == RunId && x.Status == "Open").Select(x => x.Id).ToList();

        public int Orders() => _db.PaperOrders.AsNoTracking().Count(x => x.SimulationRunId == RunId);

        public long BookOf(long userId)
            => _db.SimulationRuns.AsNoTracking().Single(x => x.UserId == userId && x.StrategyName == ManualOrdersController.BookStrategyName).Id;

        /// <summary>The order ticket as <paramref name="userId"/> sees it, booking through this desk's service.</summary>
        public ManualOrdersController ManualTicket(long userId)
        {
            // The ticket stamps its own orders with the wall clock; the quote
            // age rule is off here (no sessions), as the ticket names its price.
            var paper = new PaperTradingService(
                _db, RecapClockTests.Inert<IRiskManagementService>.Create(), new PositionGreeksTests.FixedLots(65),
                deskEvents: Publisher);

            var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Name, "coderforchange")
            }, "Test"));

            return new ManualOrdersController(_db, paper, new PositionGreeksTests.FixedLots(65), null!,
                NullLogger<ManualOrdersController>.Instance, Publisher)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
            };
        }

        public void Dispose() => _db.Dispose();
    }

    private sealed class PaperTradingPublisherClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }
}
