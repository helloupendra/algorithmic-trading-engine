using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Carried manual positions are settled at expiry.
///
/// Once the owner could carry a hand-placed position overnight (27 Sep), an
/// expired option in the book had nothing to close it: it stopped quoting and
/// sat "open" for ever at its last trade. These pin the settlement — intrinsic
/// value against the underlying's close, stamped at the expiry's close, no
/// order and so no brokerage, once and only once, and on start-up for anything
/// that expired while the API was down.
/// </summary>
public class ExpirySettlementTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string Call24500 = "NSE:NIFTY2692924500CE";
    private const string Put24400 = "NSE:NIFTY2692924400PE";
    private static readonly DateOnly Tuesday = new(2026, 9, 29);
    private static readonly DateTime TuesdayCloseUtc = new(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc); // 15:30 IST

    private static DateTime Ist(int y, int mo, int d, int h, int mi) =>
        IstTime.FromIst(new DateTime(y, mo, d, h, mi, 0));

    [Fact]
    public async Task An_in_the_money_call_settles_at_intrinsic_against_the_index_close()
    {
        using var h = Harness.Create();
        long id = h.OpenManual(Call24500, "LONG", lots: 2, entry: 120m);
        h.Contract(Call24500, "NSE", "FO", "CE", "NIFTY", 24500m, Tuesday);
        h.Bar(Nifty, Ist(2026, 9, 29, 15, 29), 24650.35m);
        decimal chargesBefore = await h.ChargesAsync();

        var result = await h.Settler.SettleDueAsync(Ist(2026, 9, 29, 15, 36), CancellationToken.None);

        Assert.Equal(1, result.Settled);
        var pos = h.Position(id);
        Assert.Equal("Closed", pos.Status);
        Assert.Equal(0, pos.Quantity);
        Assert.Equal(150.35m, pos.LastMarkPrice);                 // the exit the row shows
        Assert.Equal((150.35m - 120m) * 2 * 75, pos.RealizedPnl);  // ₹4,552.50
        Assert.Equal(TuesdayCloseUtc, pos.ClosedUtc);             // at the expiry, not at the sweep

        var signal = h.Db.SimulationSignals.AsNoTracking().Single(x => x.GroupId == pos.GroupId);
        Assert.Equal("CLOSE_GROUP", signal.SignalType);
        string reason = ReasonOf(signal.MetadataJson);
        Assert.StartsWith("Expired — settled at intrinsic 150.35, S=24,650.35 (NIFTY close 29 Sep", reason);
        Assert.EndsWith("K=24,500 CE", reason);

        // An expiry is not a trade: no fill is booked, so nothing is charged.
        Assert.Equal(1, h.Db.PaperOrders.Count());
        Assert.Equal(chargesBefore, await h.ChargesAsync());
    }

    [Fact]
    public async Task An_out_of_the_money_put_settles_at_zero()
    {
        using var h = Harness.Create();
        long id = h.OpenManual(Put24400, "SHORT", lots: 1, entry: 35m);
        h.Contract(Put24400, "NSE", "FO", "PE", "NIFTY", 24400m, Tuesday);
        h.Bar(Nifty, Ist(2026, 9, 29, 15, 29), 24650.35m);

        await h.Settler.SettleDueAsync(Ist(2026, 9, 29, 15, 36), CancellationToken.None);

        var pos = h.Position(id);
        Assert.Equal("Closed", pos.Status);
        Assert.Equal(0m, pos.LastMarkPrice);
        Assert.Equal(35m * 75, pos.RealizedPnl);   // the writer keeps the whole premium
        Assert.Contains("settled at intrinsic 0.00 (out of the money)", ReasonOf(h.Db.SimulationSignals.Single().MetadataJson));
    }

    [Fact]
    public async Task Nothing_settles_before_the_close_and_its_grace()
    {
        using var h = Harness.Create();
        long id = h.OpenManual(Call24500, "LONG", 1, 120m);
        h.Contract(Call24500, "NSE", "FO", "CE", "NIFTY", 24500m, Tuesday);
        h.Bar(Nifty, Ist(2026, 9, 29, 15, 29), 24650m);

        var result = await h.Settler.SettleDueAsync(Ist(2026, 9, 29, 15, 33), CancellationToken.None);

        Assert.Equal(0, result.Settled);
        Assert.Equal(1, result.NotDueYet);
        Assert.Equal("Open", h.Position(id).Status);
    }

    [Fact]
    public async Task A_second_pass_changes_nothing()
    {
        using var h = Harness.Create();
        long id = h.OpenManual(Call24500, "LONG", 1, 120m);
        h.Contract(Call24500, "NSE", "FO", "CE", "NIFTY", 24500m, Tuesday);
        h.Bar(Nifty, Ist(2026, 9, 29, 15, 29), 24650m);

        var first = await h.Settler.SettleDueAsync(Ist(2026, 9, 29, 15, 36), CancellationToken.None);
        decimal realized = h.Position(id).RealizedPnl;
        var second = await h.Settler.SettleDueAsync(Ist(2026, 9, 29, 15, 41), CancellationToken.None);

        Assert.Equal(1, first.Settled);
        Assert.Equal(0, second.Settled);
        Assert.Equal(realized, h.Position(id).RealizedPnl);
        Assert.Single(h.Db.SimulationSignals);

        // And the write itself refuses a closed position, whoever calls it.
        Assert.False(await h.Paper.SettleExpiredPositionAsync(h.BookId, id, 1m, "{}", TuesdayCloseUtc));
    }

    [Fact]
    public async Task On_start_up_a_contract_that_expired_while_the_API_was_down_is_caught_up()
    {
        using var h = Harness.Create();
        const string fridayCall = "NSE:NIFTY2692524500CE";
        var friday = new DateOnly(2026, 9, 25);
        long id = h.OpenManual(fridayCall, "LONG", 1, 60m);
        h.Contract(fridayCall, "NSE", "FO", "CE", "NIFTY", 24500m, friday);
        // No feed ran on Friday: no bars, and the index's quote is Monday's.
        h.Quote(Nifty, 24700m, Ist(2026, 9, 28, 9, 20));
        // The daily candle backfilled since is the one record of Friday's close.
        h.Candle(Nifty, "D", new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc), 24480m);

        await h.Settler.SettleDueAsync(Ist(2026, 9, 28, 9, 25), CancellationToken.None);

        var pos = h.Position(id);
        Assert.Equal("Closed", pos.Status);
        Assert.Equal(0m, pos.LastMarkPrice);   // 24,480 < 24,500: expired worthless, not Monday's 200
        Assert.Equal(-60m * 75, pos.RealizedPnl);
        Assert.Equal(Ist(2026, 9, 25, 15, 30), pos.ClosedUtc);
    }

    [Fact]
    public async Task Without_a_closing_price_the_position_stays_open_rather_than_invented()
    {
        using var h = Harness.Create();
        long id = h.OpenManual(Call24500, "LONG", 1, 120m);
        h.Contract(Call24500, "NSE", "FO", "CE", "NIFTY", 24500m, Tuesday);
        // A bar from the morning is not the close.
        h.Bar(Nifty, Ist(2026, 9, 29, 11, 0), 24900m);

        var result = await h.Settler.SettleDueAsync(Ist(2026, 9, 29, 16, 0), CancellationToken.None);

        Assert.Equal(1, result.AwaitingPrice);
        Assert.Equal("Open", h.Position(id).Status);
    }

    [Fact]
    public async Task An_MCX_option_settles_against_the_future_it_is_written_on()
    {
        using var h = Harness.Create();
        const string option = "MCX:CRUDEOIL26SEP6500CE";
        const string future = "MCX:CRUDEOIL26SEPFUT";
        var optionExpiry = new DateOnly(2026, 9, 17);
        long id = h.OpenManual(option, "LONG", 1, 80m);
        h.Contract(option, "MCX", "COM", "CE", "CRUDEOIL", 6500m, optionExpiry);
        h.Contract(future, "MCX", "COM", "FUT", "CRUDEOIL", null, new DateOnly(2026, 9, 21));
        h.Contract("MCX:CRUDEOIL26OCTFUT", "MCX", "COM", "FUT", "CRUDEOIL", null, new DateOnly(2026, 10, 19));
        // MCX closes at 23:30 IST while New York is on summer time.
        h.Bar(future, Ist(2026, 9, 17, 23, 29), 6612m);

        var early = await h.Settler.SettleDueAsync(Ist(2026, 9, 17, 23, 20), CancellationToken.None);
        Assert.Equal(0, early.Settled);   // still trading at 23:20

        await h.Settler.SettleDueAsync(Ist(2026, 9, 17, 23, 36), CancellationToken.None);

        var pos = h.Position(id);
        Assert.Equal(112m, pos.LastMarkPrice);
        Assert.Equal(Ist(2026, 9, 17, 23, 30), pos.ClosedUtc);
        Assert.Contains("S=6,612.00 (MCX:CRUDEOIL26SEPFUT close 17 Sep", ReasonOf(h.Db.SimulationSignals.Single().MetadataJson));
    }

    [Fact]
    public async Task A_future_settles_at_its_own_last_price()
    {
        using var h = Harness.Create();
        const string future = "MCX:CRUDEOIL26SEPFUT";
        long id = h.OpenManual(future, "SHORT", 1, 6650m);
        h.Contract(future, "MCX", "COM", "FUT", "CRUDEOIL", null, new DateOnly(2026, 9, 21));
        h.Bar(future, Ist(2026, 9, 21, 23, 29), 6590m);

        await h.Settler.SettleDueAsync(Ist(2026, 9, 21, 23, 40), CancellationToken.None);

        var pos = h.Position(id);
        Assert.Equal(6590m, pos.LastMarkPrice);
        Assert.Equal((6650m - 6590m) * 75, pos.RealizedPnl);
        Assert.StartsWith("Expired — settled at the future's last price 6,590.00", ReasonOf(h.Db.SimulationSignals.Single().MetadataJson));
    }

    [Fact]
    public async Task Only_the_manual_book_is_settled()
    {
        using var h = Harness.Create();
        long manual = h.OpenManual(Call24500, "LONG", 1, 120m);
        long strategy = h.Open(h.StrategyRunId, "Ghost", Call24500, "LONG", 1, 120m);
        h.Contract(Call24500, "NSE", "FO", "CE", "NIFTY", 24500m, Tuesday);
        h.Bar(Nifty, Ist(2026, 9, 29, 15, 29), 24650m);

        await h.Settler.SettleDueAsync(Ist(2026, 9, 29, 15, 36), CancellationToken.None);

        Assert.Equal("Closed", h.Position(manual).Status);
        // A strategy run squares off at its own close; this is not its path.
        Assert.Equal("Open", h.Position(strategy).Status);
    }

    [Fact]
    public async Task A_carried_contract_missing_from_the_feed_is_named_so_it_can_be_put_back()
    {
        using var h = Harness.Create();
        const string onTheList = "NSE:NIFTY26O0624500CE";
        const string removed = "NSE:NIFTY26O0624600CE";
        const string expired = "NSE:NIFTY2692224500CE";
        h.OpenManual(onTheList, "LONG", 1, 100m);
        h.OpenManual(removed, "LONG", 1, 90m);
        h.OpenManual(expired, "LONG", 1, 10m);
        h.Contract(onTheList, "NSE", "FO", "CE", "NIFTY", 24500m, new DateOnly(2026, 10, 6));
        h.Contract(removed, "NSE", "FO", "CE", "NIFTY", 24600m, new DateOnly(2026, 10, 6));
        h.Contract(expired, "NSE", "FO", "CE", "NIFTY", 24500m, new DateOnly(2026, 9, 22));
        h.Db.LiveWatchlistItems.Add(new LiveWatchlistItem { Symbol = onTheList, IsActive = true });
        h.Db.SaveChanges();

        var missing = await h.Settler.CarriedSymbolsOffTheFeedAsync(Ist(2026, 9, 28, 8, 45), CancellationToken.None);

        Assert.Equal(new[] { removed }, missing);
    }

    /// <summary>The line the activity feed shows: the metadata's "reason".</summary>
    private static string ReasonOf(string metadataJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
        return doc.RootElement.GetProperty("reason").GetString()!;
    }

    // ------------------------------------------------------------ harness --

    private sealed class Harness : IDisposable
    {
        public TradingDbContext Db { get; }
        public PaperTradingService Paper { get; }
        public ExpirySettler Settler { get; }
        public long BookId { get; }
        public long StrategyRunId { get; }
        private readonly ILotSizeResolver _lots = new PositionGreeksTests.FixedLots(75);

        private Harness(TradingDbContext db)
        {
            Db = db;
            var book = new SimulationRun
            {
                Mode = PaperTradingService.LivePaperMode, Status = "Running", Symbol = "MANUAL",
                StrategyName = ManualOrdersController.BookStrategyName, ParametersJson = "{}"
            };
            var run = new SimulationRun
            {
                Mode = PaperTradingService.LivePaperMode, Status = "Stopped", Symbol = Nifty,
                StrategyName = "Ghost", ParametersJson = "{}"
            };
            db.SimulationRuns.AddRange(book, run);
            db.SaveChanges();
            BookId = book.Id;
            StrategyRunId = run.Id;

            Paper = new PaperTradingService(db, RecapClockTests.Inert<IRiskManagementService>.Create(), _lots);
            Settler = new ExpirySettler(db, Paper, PositionGreeksTests.Sessions(), NullLogger<ExpirySettler>.Instance);
        }

        public static Harness Create() =>
            new(new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase($"expiry-{Guid.NewGuid():N}").Options));

        public long OpenManual(string symbol, string direction, int lots, decimal entry) =>
            Open(BookId, ManualOrdersController.BookStrategyName, symbol, direction, lots, entry);

        public long Open(long runId, string strategy, string symbol, string direction, int lots, decimal entry)
        {
            string group = $"G-{Guid.NewGuid():N}"[..12];
            var opened = new DateTime(2026, 9, 24, 5, 0, 0, DateTimeKind.Utc);
            Db.PaperOrders.Add(new PaperOrder
            {
                SimulationRunId = runId, StrategyName = strategy, GroupId = group, Symbol = symbol,
                Side = direction == "LONG" ? "BUY" : "SELL", Quantity = lots, OrderType = "MARKET_SIM",
                Status = "Filled", RequestedPrice = entry, FillPrice = entry, CreatedUtc = opened, FilledUtc = opened
            });
            var position = new PaperPosition
            {
                SimulationRunId = runId, StrategyName = strategy, GroupId = group, Symbol = symbol,
                Direction = direction, Quantity = lots, AveragePrice = entry, LastMarkPrice = entry,
                Status = "Open", OpenedUtc = opened, UpdatedUtc = opened
            };
            Db.PaperPositions.Add(position);
            Db.SaveChanges();
            return position.Id;
        }

        public void Contract(string symbol, string exchange, string segment, string type, string underlying, decimal? strike, DateOnly expiry)
        {
            Db.Instruments.Add(new Instrument
            {
                Symbol = symbol, Exchange = exchange, Segment = segment, InstrumentType = type,
                OptionType = type is "CE" or "PE" ? type : string.Empty, Underlying = underlying,
                StrikePrice = strike, ExpiryDate = expiry,
                // Expired rows stay in the master, disabled.
                IsEnabled = false
            });
            Db.SaveChanges();
        }

        public void Bar(string symbol, DateTime startUtc, decimal close)
        {
            Db.LiveBars.Add(new LiveBar
            {
                Symbol = symbol, Resolution = "1m", BarStartUtc = startUtc,
                Open = close, High = close, Low = close, Close = close, UpdatedUtc = startUtc.AddMinutes(1)
            });
            Db.SaveChanges();
        }

        public void Candle(string symbol, string resolution, DateTime atUtc, decimal close)
        {
            Db.Candles.Add(new Candle { Symbol = symbol, Resolution = resolution, TimeStampUtc = atUtc, Open = close, High = close, Low = close, Close = close });
            Db.SaveChanges();
        }

        public void Quote(string symbol, decimal ltp, DateTime atUtc)
        {
            Db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = symbol, LastTradedPrice = ltp, UpdatedUtc = atUtc, RawPayload = "{}" });
            Db.SaveChanges();
        }

        public PaperPosition Position(long id) => Db.PaperPositions.AsNoTracking().Single(x => x.Id == id);

        public Task<decimal> ChargesAsync() => new RunCharges(Db, _lots).ForRunAsync(BookId, CancellationToken.None);

        public void Dispose() => Db.Dispose();
    }
}
