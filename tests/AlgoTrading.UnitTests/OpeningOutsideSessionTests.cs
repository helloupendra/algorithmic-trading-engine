using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Nothing opens while its contract's market is closed.
/// </summary>
/// <remarks>
/// The 2 Nov review's finding: a runner started before the open handles every tick
/// it reads, and vendors send ticks outside the session. A strategy that enters on
/// its first tick was filled at yesterday's closing bid, and at 09:15 the gap became
/// P&amp;L on a trade that could never have happened. Closes stay allowed, and a
/// replay or a recap trades a session that already happened, on its own clock.
/// </remarks>
public class OpeningOutsideSessionTests
{
    // Tue 3 Nov 2026: a trading day, before the derivatives close moves, with the US on standard time.
    private const string Call = "NSE:NIFTY2692925000CE";
    private const string Put = "NSE:NIFTY2692925000PE";
    private const string SensexCall = "BSE:SENSEX2692982000CE";
    private const string CrudeCall = "MCX:CRUDEOIL26NOV5500CE";
    private const string Share = "NSE:RELIANCE-EQ";

    private static DateTime Ist(int month, int day, int h, int m, int s = 0) => IstTime.FromIst(new DateTime(2026, month, day, h, m, s));

    private static DateTime Nov3(int h, int m, int s = 0) => Ist(11, 3, h, m, s);

    // ======================================================= the equity session

    [Theory]
    [InlineData(8, 46, 0, "opens 09:15 IST")]      // the morning job has just started the runner
    [InlineData(9, 5, 0, "opens 09:15 IST")]       // NSE's pre-open
    [InlineData(9, 14, 59, "opens 09:15 IST")]
    [InlineData(15, 30, 0, "opens Wed 4 Nov 09:15 IST")]
    [InlineData(15, 30, 45, "opens Wed 4 Nov 09:15 IST")]  // before the close sweep's next pass
    [InlineData(20, 0, 0, "opens Wed 4 Nov 09:15 IST")]
    public async Task An_option_cannot_open_outside_its_session(int h, int m, int s, string opens)
    {
        using var book = new Book(Nov3(h, m, s));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", Call));

        Assert.Equal($"MARKET NOT OPEN: {Call} {opens}. The OPEN_GROUP signal was refused; exits remain allowed.", refused.Message);
        // No row a retry could mistake for a booking.
        Assert.Empty(book.Signals());
        Assert.Empty(book.Orders());
        Assert.Empty(book.Positions());
    }

    [Theory]
    [InlineData(9, 15, 0)]
    [InlineData(12, 0, 0)]
    [InlineData(15, 29, 59)]
    public async Task An_option_opens_inside_its_session(int h, int m, int s)
    {
        using var book = new Book(Nov3(h, m, s));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        await book.Open("G1", Call);

        Assert.Equal(100.5m, book.Orders().Single().FillPrice);
    }

    [Fact]
    public async Task A_bse_contract_follows_the_same_hours()
    {
        using var book = new Book(Nov3(9, 5));
        book.Quote(SensexCall, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", SensexCall));

        Assert.Contains($"{SensexCall} opens 09:15 IST", refused.Message);
    }

    [Fact]
    public async Task Nothing_opens_on_a_weekend()
    {
        using var book = new Book(Ist(11, 7, 11, 0));   // a Saturday
        book.Quote(Call, ltp: 100m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", Call));

        Assert.Contains("opens Mon 9 Nov 09:15 IST", refused.Message);
    }

    [Fact]
    public async Task Nothing_opens_on_a_holiday()
    {
        var holiday = new DateOnly(2026, 11, 3);
        using var book = new Book(Nov3(11, 0), new Calendar { Holidays = { ("NSE", holiday) } });
        book.Quote(Call, ltp: 100m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", Call));

        Assert.Contains("opens Wed 4 Nov 09:15 IST", refused.Message);
    }

    [Fact]
    public async Task A_special_session_opens_a_day_the_market_is_otherwise_shut()
    {
        var sunday = new DateOnly(2026, 11, 8);
        var calendar = new Calendar { Special = { ("NSE", sunday, new TimeOnly(18, 15), new TimeOnly(19, 15)) } };
        using var book = new Book(Ist(11, 8, 18, 30), calendar);
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        await book.Open("G1", Call);

        Assert.Single(book.Orders());
    }

    // ===================================================================== MCX

    [Theory]
    [InlineData(11, 3, 8, 59, false)]   // before MCX's own open, which is earlier than NSE's
    [InlineData(11, 3, 9, 0, true)]
    [InlineData(11, 3, 16, 0, true)]    // the evening NSE is closed for
    [InlineData(11, 3, 23, 54, true)]   // the US on standard time: MCX trades to 23:55
    [InlineData(11, 3, 23, 55, false)]
    [InlineData(10, 20, 23, 29, true)]  // the US on daylight time: MCX trades to 23:30
    [InlineData(10, 20, 23, 30, false)]
    public async Task A_crude_option_opens_on_mcxs_hours(int month, int day, int h, int m, bool opens)
    {
        using var book = new Book(Ist(month, day, h, m));
        book.Quote(CrudeCall, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        if (opens)
        {
            await book.Open("G1", CrudeCall);
            Assert.Single(book.Orders());
        }
        else
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", CrudeCall));
            Assert.Contains($"MARKET NOT OPEN: {CrudeCall} opens", refused.Message);
        }
    }

    // ==================================================== the close from 7 Dec 2026

    private const string WeeklyToday = "NSE:NIFTY26D0825000CE";       // expires Tue 8 Dec 2026
    private const string WeeklyNext = "NSE:NIFTY26D1525000CE";        // expires Tue 15 Dec 2026
    private const string MonthlyIndex = "NSE:NIFTY26DEC25000CE";
    private const string StockOption = "NSE:RELIANCE26DEC1400CE";
    private static readonly DateTime Dec8 = Ist(12, 8, 15, 40);

    [Fact]
    public async Task A_contract_that_is_not_expiring_trades_to_1545()
    {
        using var book = new Book(Dec8);
        book.Quote(WeeklyNext, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        await book.Open("G1", WeeklyNext);

        Assert.Single(book.Orders());
    }

    [Fact]
    public async Task At_1545_it_stops_too()
    {
        using var book = new Book(Ist(12, 8, 15, 45));
        book.Quote(WeeklyNext, ltp: 100m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", WeeklyNext));

        Assert.Contains($"{WeeklyNext} opens Wed 9 Dec 09:15 IST", refused.Message);
    }

    [Fact]
    public async Task A_share_stops_at_1530()
    {
        using var book = new Book(Dec8);
        book.Quote(Share, ltp: 1400m, bid: 1399.5m, ask: 1400.5m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", Share));

        Assert.Contains($"{Share} opens Wed 9 Dec 09:15 IST", refused.Message);
    }

    [Fact]
    public async Task One_closed_leg_refuses_the_whole_group()
    {
        using var book = new Book(Dec8);
        book.Quote(WeeklyNext, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));
        book.Quote(Share, ltp: 1400m, bid: 1399.5m, ask: 1400.5m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Service.CreateSignalAsync(
            book.Signal("OPEN_GROUP", "G1", Leg(WeeklyNext), Leg(Share))));

        Assert.Contains(Share, refused.Message);
        Assert.DoesNotContain(WeeklyNext, refused.Message);
        Assert.Empty(book.Orders());
    }

    [Theory]
    [InlineData(15, 29, 59, true)]
    [InlineData(15, 30, 0, false)]
    [InlineData(15, 40, 0, false)]
    public async Task An_index_contract_on_its_expiry_day_stops_at_the_cash_close(int h, int m, int s, bool opens)
    {
        using var book = new Book(Ist(12, 8, h, m, s));
        book.Quote(WeeklyToday, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        if (opens)
        {
            await book.Open("G1", WeeklyToday);
            Assert.Single(book.Orders());
        }
        else
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", WeeklyToday));
            Assert.Contains($"{WeeklyToday} stopped at 15:30 IST: an index contract stops at the cash close on its expiry day", refused.Message);
        }
    }

    [Fact]
    public async Task A_monthly_contract_is_read_from_the_instrument_master()
    {
        using var book = new Book(Dec8);
        book.Instrument(MonthlyIndex, new DateOnly(2026, 12, 8));
        book.Quote(MonthlyIndex, ltp: 100m, age: TimeSpan.FromSeconds(1));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Open("G1", MonthlyIndex));

        Assert.Contains("stopped at 15:30 IST", refused.Message);
    }

    [Fact]
    public async Task A_stock_option_on_its_expiry_day_trades_to_1545()
    {
        using var book = new Book(Dec8);
        book.Instrument(StockOption, new DateOnly(2026, 12, 8));
        book.Quote(StockOption, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        await book.Open("G1", StockOption);

        Assert.Single(book.Orders());
    }

    // ==================================================================== exits

    [Fact]
    public async Task A_close_is_allowed_after_the_close()
    {
        using var book = new Book(Nov3(15, 29));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));
        await book.Open("G1", Call);

        book.Clock.Now = Nov3(15, 30, 45);
        await book.Service.CreateSignalAsync(book.Signal("CLOSE_GROUP", "G1", Leg(Call, "SELL")));

        Assert.Equal("Closed", book.Positions().Single().Status);
    }

    [Theory]
    [InlineData("""{"square_off":true}""")]
    [InlineData("""{"risk_rule":"leg-stop"}""")]
    public async Task A_square_off_or_a_risk_exit_is_allowed_after_the_close(string metadata)
    {
        using var book = new Book(Nov3(15, 29));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));
        await book.Open("G1", Call);

        book.Clock.Now = Nov3(16, 0);
        var exit = book.Signal("SQUARE_OFF", "G1", Leg(Call, "SELL"));
        exit.MetadataJson = metadata;
        await book.Service.CreateSignalAsync(exit);

        Assert.Equal("Closed", book.Positions().Single().Status);
    }

    [Fact]
    public async Task Flatten_is_allowed_after_the_close()
    {
        using var book = new Book(Nov3(15, 29));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));
        await book.Open("G1", Call);

        book.Clock.Now = Nov3(15, 31);

        Assert.Equal(1, await book.Service.FlattenRunAsync(book.RunId, "Market closed (15:30 IST)"));
    }

    // ==================================================== booked once, whenever asked

    [Fact]
    public async Task An_open_booked_before_the_close_is_answered_with_its_row_after_it()
    {
        // The runner's re-post of an OPEN whose answer was lost: the id is looked up
        // first, so a booked one is confirmed, and an unbooked one is refused.
        using var book = new Book(Nov3(15, 29, 50));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));
        var first = book.Signal("OPEN_GROUP", "G1", Leg(Call));
        first.ClientSignalId = "3f0c6b0e-6a55-4d0b-9d0c-2b1f7f6a9c11";
        var booked = await book.Service.CreateSignalAsync(first);

        book.Clock.Now = Nov3(15, 31);
        var again = await book.Service.CreateSignalAsync(first);

        Assert.Equal(booked.Id, again.Id);
        Assert.Single(book.Orders());

        var never = book.Signal("OPEN_GROUP", "G2", Leg(Call));
        never.ClientSignalId = "another";
        await Assert.ThrowsAsync<InvalidOperationException>(() => book.Service.CreateSignalAsync(never));
        Assert.Single(book.Orders());
    }

    // ================================================== replays, recaps and the rest

    [Fact]
    public async Task A_recap_run_opens_in_the_evening()
    {
        using var book = new Book(Nov3(20, 0), parameters: """{"session":"recap","recap_date":"2026-09-11"}""");
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromMinutes(1));

        await book.Open("G1", Call);

        Assert.Single(book.Orders());
    }

    [Fact]
    public async Task A_backtest_opens_at_any_hour()
    {
        using var book = new Book(Nov3(20, 0), mode: PaperTradingService.OfflineReplayMode);

        await book.Service.CreateSignalAsync(book.Signal("OPEN_GROUP", "G1", new SimulationSignalLegRequest
        {
            Symbol = Call, Side = "BUY", Quantity = 1, Price = 101.25m
        }));

        Assert.Equal(101.25m, book.Orders().Single().FillPrice);
    }

    [Fact]
    public async Task A_market_the_desk_keeps_no_hours_for_is_not_refused()
    {
        using var book = new Book(Nov3(20, 0));
        const string elsewhere = "NYSE:ACME";

        await book.Service.CreateSignalAsync(book.Signal("OPEN_GROUP", "G1", new SimulationSignalLegRequest
        {
            Symbol = elsewhere, Side = "BUY", Quantity = 1, Price = 50m
        }));

        Assert.Single(book.Orders());
    }

    [Fact]
    public async Task The_setting_turns_it_off_for_the_dev_stack()
    {
        using var book = new Book(Nov3(20, 0), fills: new PaperFillOptions { RefuseOpensWhileMarketClosed = false });
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromMinutes(1));

        await book.Open("G1", Call);

        Assert.Single(book.Orders());
    }

    [Fact]
    public async Task A_host_with_no_session_service_is_not_refused()
    {
        using var book = new Book(Nov3(20, 0), withoutSessions: true);
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromMinutes(1));

        await book.Open("G1", Call);

        Assert.Single(book.Orders());
    }

    private static SimulationSignalLegRequest Leg(string symbol, string side = "BUY")
        => new() { Symbol = symbol, Side = side, Quantity = 1 };

    private sealed class Calendar : IMarketCalendar
    {
        public List<(string Exchange, DateOnly Date)> Holidays { get; } = [];
        public List<(string Exchange, DateOnly Date, TimeOnly Open, TimeOnly Close)> Special { get; } = [];

        public MarketHoliday? HolidayOn(string exchange, DateOnly date)
            => Holidays.Contains((exchange, date))
                ? new MarketHoliday { Exchange = exchange, Date = date, Name = "test holiday", Closure = MarketClosure.FullDay, Source = "test" }
                : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date)
        {
            foreach (var s in Special)
            {
                if (s.Exchange == exchange && s.Date == date)
                    return new MarketSpecialSession { Exchange = exchange, Date = date, Name = "Muhurat", OpenIst = s.Open, CloseIst = s.Close };
            }
            return null;
        }

        public bool HasYear(string exchange, int year) => true;
        public bool IsLoaded => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>One run over an in-memory database, on a clock the test moves.</summary>
    private sealed class Book : IDisposable
    {
        private readonly TradingDbContext _db;

        public Book(
            DateTime nowUtc,
            Calendar? calendar = null,
            PaperFillOptions? fills = null,
            string mode = PaperTradingService.LivePaperMode,
            string parameters = "{}",
            bool withoutSessions = false)
        {
            _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase($"open-session-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            var run = new SimulationRun
            {
                Mode = mode, Symbol = "NSE:NIFTY50-INDEX", Status = "Running",
                StrategyName = "Ghost", ParametersJson = parameters
            };
            _db.SimulationRuns.Add(run);
            _db.SaveChanges();
            RunId = run.Id;

            Clock = new PaperTradingServiceTests.ManualClock(nowUtc);
            Service = new PaperTradingService(
                _db,
                RecapClockTests.Inert<IRiskManagementService>.Create(),
                new PositionGreeksTests.FixedLots(65),
                withoutSessions ? null : new MarketSessionService(calendar ?? new Calendar()),
                Options.Create(fills ?? new PaperFillOptions()),
                Clock);
        }

        public long RunId { get; }
        public PaperTradingServiceTests.ManualClock Clock { get; }
        public PaperTradingService Service { get; }

        public void Quote(string symbol, decimal ltp, decimal? bid = null, decimal? ask = null, TimeSpan? age = null)
        {
            _db.LiveQuotesLatest.Add(new LiveQuoteLatest
            {
                Symbol = symbol, RawPayload = "{}", SourceKey = "dhan", LastTradedPrice = ltp, BidPrice = bid, AskPrice = ask,
                UpdatedUtc = Clock.Now - (age ?? TimeSpan.Zero),
            });
            _db.SaveChanges();
        }

        public void Instrument(string symbol, DateOnly expiry)
        {
            _db.Instruments.Add(new Instrument
            {
                Symbol = symbol, Exchange = "NSE", Segment = "FO", InstrumentType = "CE", OptionType = "CE",
                Underlying = "NIFTY", StrikePrice = 25_000m, TickSize = 0.05m, IsEnabled = true, ExpiryDate = expiry,
            });
            _db.SaveChanges();
        }

        public CreateSimulationSignalRequest Signal(string type, string group, params SimulationSignalLegRequest[] legs) => new()
        {
            SimulationRunId = RunId,
            StrategyName = "Ghost",
            SignalType = type,
            TimestampUtc = Clock.Now,
            GroupId = group,
            MetadataJson = "{}",
            Legs = legs.ToList(),
        };

        public Task<SimulationSignalResponse> Open(string group, string symbol)
            => Service.CreateSignalAsync(Signal("OPEN_GROUP", group, Leg(symbol)));

        public List<PaperOrder> Orders() => _db.PaperOrders.AsNoTracking().Where(x => x.SimulationRunId == RunId).ToList();
        public List<PaperPosition> Positions() => _db.PaperPositions.AsNoTracking().Where(x => x.SimulationRunId == RunId).ToList();
        public List<SimulationSignal> Signals() => _db.SimulationSignals.AsNoTracking().Where(x => x.SimulationRunId == RunId).ToList();

        public void Dispose() => _db.Dispose();
    }
}
