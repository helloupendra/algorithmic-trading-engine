using System.Reflection;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The manual ticket's fills go through the paper-fill guards a strategy's do (review 2 Nov, F2).
/// </summary>
/// <remarks>
/// Until 2 Nov the ticket sent its price to the engine as final, past the live pricing: a limit was booked AT the limit
/// whatever the book said (a BUY limit of 0.05 on a ₹150 option was a position ₹149.95 a unit in profit at its first
/// mark), and a market order took a quote of any age, which is the fill the strategy path has refused since the feed
/// stall of 24 Sep. The clock is Monday 28 Sep 2026, a trading day, so the quote-age rule is on unless a test moves it
/// out of hours.
/// </remarks>
public class ManualTicketFillTests
{
    private const string Call = "NSE:NIFTY2692925000CE";
    private const long Trader = 7;

    private static DateTime Ist(int h, int m, int s = 0) => IstTime.FromIst(new DateTime(2026, 9, 28, h, m, s));

    private static readonly DateTime Morning = Ist(10, 0);

    // ============================================================== market

    [Fact]
    public async Task An_order_after_the_close_is_refused_and_says_when_the_market_opens()
    {
        // Until 2 Nov it filled at the last quote of the day, a price nobody could have had.
        using var desk = new Desk(Ist(16, 0));
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(1));

        var outcome = await desk.Place("BUY", limit: null);

        Assert.Null(outcome.Fill);
        Assert.Equal(
            $"Not filled: MARKET NOT OPEN: {Call} opens Tue 29 Sep 09:15 IST. The OPEN_GROUP signal was refused; exits remain allowed.",
            outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
        Assert.Empty(desk.Positions());
    }

    [Fact]
    public async Task A_market_order_on_a_quote_older_than_the_limit_is_refused_while_the_market_is_open()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromMinutes(5));

        var outcome = await desk.Place("BUY", limit: null);

        Assert.Null(outcome.Fill);
        Assert.Equal(
            $"No live price for {Call}: its latest quote is 300 s old; while the market is open a fill needs one under 60 s. "
            + "Wait for the next tick and try again.",
            outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
        Assert.Empty(desk.Positions());
    }

    [Fact]
    public async Task A_market_order_is_priced_live_by_the_engine_at_the_ask()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("BUY", limit: null);

        var fill = Assert.IsType<ManualOrderFill>(outcome.Fill);
        Assert.Equal(150.5m, fill.Price);
        Assert.Equal("ask", fill.PriceBasis);
        var order = Assert.Single(desk.Orders());
        Assert.Equal(150.5m, order.FillPrice);
        // The engine's live rule, not "signal": the leg was not filled as given.
        Assert.Equal("ask", Read(order.MetadataJson, "rule"));
    }

    [Fact]
    public async Task A_market_order_with_no_book_crosses_half_the_spread_from_the_last_trade()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 100m, bid: null, ask: null, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("SELL", limit: null);

        var fill = Assert.IsType<ManualOrderFill>(outcome.Fill);
        Assert.Equal(100m * (1m - 0.0015m), fill.Price);
        Assert.Equal("last trade less half the spread", fill.PriceBasis);
        Assert.Contains("at 99.85 — filled on the last trade less half the spread.", fill.Message);
    }

    [Fact]
    public async Task With_no_quote_at_all_nothing_is_booked()
    {
        using var desk = new Desk(Morning);

        var outcome = await desk.Place("BUY", limit: null);

        Assert.Equal($"No live price for {Call}. Add it to the watchlist and wait for a tick.", outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
    }

    // =============================================================== limit

    [Fact]
    public async Task A_buy_limit_below_the_ask_is_refused_not_booked_at_the_limit()
    {
        // The review's case: BUY limit 0.05 on a CE at 150 was booked at 0.05.
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150m, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("BUY", limit: 0.05m);

        Assert.Null(outcome.Fill);
        Assert.Equal(
            "Not filled: a BUY limit of 0.05 is below the ask 150.00, so it would not fill now. A limit fills only when "
            + "the book has reached it (a buy at the ask, a sell at the bid), and this ticket keeps no resting orders.",
            outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
        Assert.Empty(desk.Positions());
    }

    [Fact]
    public async Task A_sell_limit_above_the_bid_is_refused()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("SELL", limit: 300m);

        Assert.Null(outcome.Fill);
        Assert.StartsWith("Not filled: a SELL limit of 300.00 is above the bid 149.50", outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
    }

    [Fact]
    public async Task A_marketable_buy_limit_fills_at_the_ask_not_at_the_limit()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("BUY", limit: 155m);

        var fill = Assert.IsType<ManualOrderFill>(outcome.Fill);
        Assert.Equal(150.5m, fill.Price);
        Assert.Equal("ask", fill.PriceBasis);
        Assert.Contains("filled on the ask, within your limit 155.00.", fill.Message);
        var order = Assert.Single(desk.Orders());
        Assert.Equal(150.5m, order.FillPrice);
        Assert.Equal("ask", Read(order.MetadataJson, "rule"));
    }

    [Fact]
    public async Task A_marketable_sell_limit_fills_at_the_bid()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("SELL", limit: 140m);

        Assert.Equal(149.5m, Assert.IsType<ManualOrderFill>(outcome.Fill).Price);
        Assert.Equal(149.5m, Assert.Single(desk.Orders()).FillPrice);
    }

    [Fact]
    public async Task A_limit_at_the_touch_fills()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("BUY", limit: 150.5m);

        Assert.Equal(150.5m, Assert.IsType<ManualOrderFill>(outcome.Fill).Price);
    }

    [Fact]
    public async Task A_limit_is_no_substitute_for_a_quote()
    {
        // The refusal used to say "or give a limitPrice", and a limit with no
        // quote was booked at the limit.
        using var desk = new Desk(Morning);

        var outcome = await desk.Place("BUY", limit: 100m);

        Assert.Null(outcome.Fill);
        Assert.Empty(desk.Orders());
    }

    [Fact]
    public async Task A_limit_on_a_stale_quote_is_refused_too()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromMinutes(5));

        var outcome = await desk.Place("BUY", limit: 155m);

        Assert.StartsWith($"No live price for {Call}: its latest quote is 300 s old", outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
    }

    [Fact]
    public async Task The_stop_is_checked_against_the_fill_not_the_limit()
    {
        // A stop below a leftover limit of 155 but above the 150.50 fill would
        // close the position on the guard's next sweep.
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(2));

        var outcome = await desk.Place("BUY", limit: 155m, stopLoss: 152m);

        Assert.Equal("stopLossPrice 152 must be BELOW the 150.50 fill for a BUY.", outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
    }

    [Fact]
    public async Task The_engine_holds_the_limit_when_the_book_moves_after_the_check()
    {
        // The ask moves past the limit between the ticket's check and the
        // booking: the engine refuses the leg rather than fill beyond it.
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150m, age: TimeSpan.FromSeconds(2));
        desk.BeforeBooking = () => desk.Quote(ltp: 151m, bid: 150.5m, ask: 151m, age: TimeSpan.FromSeconds(1));

        var outcome = await desk.Place("BUY", limit: 150m);

        Assert.Null(outcome.Fill);
        Assert.StartsWith($"Not filled: No fill price for {Call} (a BUY limit of 150.00 is below the ask 151.00", outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
        Assert.Empty(desk.Positions());
    }

    // ===================================================== the engine's rule

    [Theory]
    [InlineData("BUY", 90, null, 80)]
    [InlineData("BUY", 90, null, 90)]
    [InlineData("BUY", null, 120, 130)]
    [InlineData("BUY", null, 120, 120)]
    [InlineData("SELL", 110, null, 120)]
    [InlineData("SELL", 110, null, 110)]
    [InlineData("SELL", null, 80, 70)]
    [InlineData("SELL", null, 80, 80)]
    public async Task Protection_is_rechecked_when_the_fill_moves_after_the_preview(
        string side, int? stopLoss, int? target, decimal fill)
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 100m, bid: 99.5m, ask: 100.5m);
        desk.BeforeBooking = () => desk.Quote(fill,
            bid: side == "SELL" ? fill : fill - 0.5m,
            ask: side == "BUY" ? fill : fill + 0.5m);

        var outcome = await desk.Place(side, limit: null, stopLoss: stopLoss, target: target, carry: true);

        Assert.Null(outcome.Fill);
        Assert.StartsWith($"Not filled: {(stopLoss is not null ? "stopLossPrice" : "targetPrice")}", outcome.RefusalMessage);
        Assert.Empty(desk.Orders());
        Assert.Empty(desk.Positions());
        Assert.Empty(desk.Signals());
    }

    [Theory]
    [InlineData("BUY", 90, 120)]
    [InlineData("SELL", 110, 80)]
    public async Task A_valid_fill_keeps_its_stop_target_and_carry(string side, decimal stopLoss, decimal target)
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 100m, bid: 99.5m, ask: 100.5m);

        var outcome = await desk.Place(side, limit: null, stopLoss: stopLoss, target: target, carry: true);

        Assert.NotNull(outcome.Fill);
        var position = Assert.Single(desk.Positions());
        Assert.Equal(stopLoss, position.StopLossPrice);
        Assert.Equal(target, position.TargetPrice);
        Assert.True(position.CarryForward);
    }

    [Fact]
    public void A_strategy_leg_cannot_carry_ticket_settings_in_its_body()
    {
        // LimitPrice is the ticket's, set inside the API: a runner's post that
        // names one is read without it, so a strategy's legs price as before.
        var request = JsonSerializer.Deserialize<CreateSimulationSignalRequest>(
            """{"simulationRunId":1,"signalType":"OPEN_GROUP","legs":[{"symbol":"NSE:X","side":"BUY","quantity":1,"price":100,"limitPrice":0.05,"stopLossPrice":90,"targetPrice":120,"carryForward":true}],"legPricesAreFinal":true}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Null(request.Legs![0].LimitPrice);
        Assert.Null(request.Legs[0].StopLossPrice);
        Assert.Null(request.Legs[0].TargetPrice);
        Assert.False(request.Legs[0].CarryForward);
        Assert.False(request.LegPricesAreFinal);
    }

    [Fact]
    public async Task The_live_fill_quote_is_the_engines_own_price()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(2));

        var buy = await desk.Paper.QuoteLiveFillAsync(Call, "BUY");
        var sell = await desk.Paper.QuoteLiveFillAsync(Call, "SELL");

        Assert.Equal((150.5m, "ask", (string?)null), (buy.Price!.Value, buy.Rule!, buy.Refusal));
        Assert.Equal((149.5m, "bid"), (sell.Price!.Value, sell.Rule!));

        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromSeconds(61));
        var stale = await desk.Paper.QuoteLiveFillAsync(Call, "BUY");
        Assert.Null(stale.Price);
        Assert.Equal("its latest quote is 61 s old; while the market is open a fill needs one under 60 s", stale.Refusal);
    }

    // ======================================================= the ticket's view

    [Fact]
    public async Task The_ticket_shows_no_buy_price_on_a_stale_quote_and_says_why()
    {
        using var desk = new Desk(Morning);
        desk.Quote(ltp: 150m, bid: 149.5m, ask: 150.5m, age: TimeSpan.FromMinutes(5));

        var view = await desk.Instrument();

        Assert.Equal(JsonValueKind.Null, view.GetProperty("buyAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("sellAt").ValueKind);
        Assert.False(view.GetProperty("tradable").GetBoolean());
        Assert.Equal("its latest quote is 300 s old; while the market is open a fill needs one under 60 s",
            view.GetProperty("untradableReason").GetString());

        desk.Quote(ltp: 100m, bid: null, ask: null, age: TimeSpan.FromSeconds(2));
        view = await desk.Instrument();
        Assert.Equal(100.15m, view.GetProperty("buyAt").GetDecimal());
        Assert.Equal(99.85m, view.GetProperty("sellAt").GetDecimal());
        Assert.True(view.GetProperty("tradable").GetBoolean());
    }

    // ============================================================ helpers

    private static string? Read(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    /// <summary>A trader's ticket over an in-memory database, on a clock the test sets.</summary>
    private sealed class Desk : IDisposable
    {
        private readonly TradingDbContext _db;
        private readonly PaperTradingServiceTests.ManualClock _clock;

        public Desk(DateTime nowUtc)
        {
            _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase($"manual-ticket-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
            _db.Instruments.Add(new Instrument
            {
                Symbol = Call, Exchange = "NSE", Segment = "FO", InstrumentType = "CE", OptionType = "CE",
                Underlying = "NIFTY", StrikePrice = 25_000m, TickSize = 0.05m, IsEnabled = true,
                // The ticket's expiry check reads the wall clock.
                ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
            });
            _db.SaveChanges();

            _clock = new PaperTradingServiceTests.ManualClock(nowUtc);
            Paper = new PaperTradingService(
                _db,
                RecapClockTests.Inert<IRiskManagementService>.Create(),
                new PositionGreeksTests.FixedLots(65),
                PositionGreeksTests.Sessions(),
                Options.Create(new PaperFillOptions()),
                _clock);
        }

        public PaperTradingService Paper { get; }

        /// <summary>Runs just before the ticket books its signal, after its own price check.</summary>
        public Action? BeforeBooking { get; set; }

        public void Quote(decimal ltp, decimal? bid, decimal? ask, TimeSpan? age = null, DateTime? updatedUtc = null)
        {
            var row = _db.LiveQuotesLatest.SingleOrDefault(x => x.Symbol == Call);
            if (row is null)
            {
                row = new LiveQuoteLatest { Symbol = Call, RawPayload = "{}", SourceKey = "dhan" };
                _db.LiveQuotesLatest.Add(row);
            }
            row.LastTradedPrice = ltp;
            row.BidPrice = bid;
            row.AskPrice = ask;
            row.UpdatedUtc = updatedUtc ?? _clock.Now - (age ?? TimeSpan.Zero);
            _db.SaveChanges();
        }

        public Task<ManualOrderOutcome> Place(string side, decimal? limit, decimal? stopLoss = null, decimal? target = null, bool carry = false)
        {
            var paper = BookingHook.Wrap(Paper, () => BeforeBooking?.Invoke());
            var service = new ManualOrderService(_db, paper, new PositionGreeksTests.FixedLots(65), NullLogger.Instance);
            return service.PlaceAsync(new ManualOrderCommand(Trader, "trader", Call, side, 1, limit, stopLoss, target, carry), CancellationToken.None);
        }

        /// <summary>GET /api/ManualOrders/instrument, as JSON.</summary>
        public async Task<JsonElement> Instrument()
        {
            var controller = new ManualOrdersController(_db, Paper, new PositionGreeksTests.FixedLots(65), null!,
                NullLogger<ManualOrdersController>.Instance);
            var result = Assert.IsType<OkObjectResult>(await controller.GetInstrument(Call, CancellationToken.None));
            return JsonSerializer.SerializeToElement(result.Value);
        }

        public List<PaperOrder> Orders() => _db.PaperOrders.AsNoTracking().ToList();

        public List<PaperPosition> Positions() => _db.PaperPositions.AsNoTracking().ToList();

        public List<SimulationSignal> Signals() => _db.SimulationSignals.AsNoTracking().ToList();

        public void Dispose() => _db.Dispose();
    }

    /// <summary>The engine as the ticket sees it, with a moment between the ticket's check and the booking.</summary>
    public class BookingHook : DispatchProxy
    {
        private IPaperTradingService _inner = null!;
        private Action _before = null!;

        public static IPaperTradingService Wrap(IPaperTradingService inner, Action before)
        {
            var proxy = Create<IPaperTradingService, BookingHook>();
            var hook = (BookingHook)(object)proxy;
            hook._inner = inner;
            hook._before = before;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IPaperTradingService.CreateSignalAsync)) _before();
            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
