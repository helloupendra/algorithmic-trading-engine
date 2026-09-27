using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// How a live paper fill is priced, and when it is not.
/// </summary>
/// <remarks>
/// Three findings of the 27 Sep audit meet here. Fills crossed no spread: a
/// sold straddle sold at the last trade and bought back at the last trade.
/// Fills trusted any quote: on 24 Sep the feed stalled from 11:27:36 to
/// 11:34:06 and every quote froze while still looking current. And a signal
/// the runner posted twice — a retry after a lost answer — could book an
/// OPEN_GROUP twice. The clock is Monday 28 Sep 2026, a trading day, so the
/// quote-age rule is on unless a test says otherwise.
/// </remarks>
public class PaperTradingServiceTests
{
    private const string Call = "NSE:NIFTY2692925000CE";
    private const string Put = "NSE:NIFTY2692925000PE";
    private const int LotSize = 65;

    private static DateTime Ist(int h, int m, int s = 0) => IstTime.FromIst(new DateTime(2026, 9, 28, h, m, s));

    private static readonly DateTime Morning = Ist(10, 0);

    // ============================================================ the spread

    [Fact]
    public async Task Sell_FillsAtBid_WhenQuoteHasBid()
    {
        using var book = new Book(Morning);
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(2));

        await book.Open(Call, "SELL", price: 100m);

        var order = book.Orders().Single();
        Assert.Equal(99.5m, order.FillPrice);
        Assert.Equal(100m, order.RequestedPrice);
        Assert.Equal("bid", Read(order.MetadataJson, "rule"));
        Assert.Equal("filled at the bid", Read(order.MetadataJson, "note"));
        Assert.Equal(99.5m, book.Positions().Single().AveragePrice);
    }

    [Fact]
    public async Task BuyAtAsk()
    {
        using var book = new Book(Morning);
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(2));

        await book.Open(Call, "BUY", price: 100m);

        var order = book.Orders().Single();
        Assert.Equal(100.5m, order.FillPrice);
        Assert.Equal("ask", Read(order.MetadataJson, "rule"));
        Assert.Equal("filled at the ask", Read(order.MetadataJson, "note"));
    }

    [Fact]
    public async Task FallsBackToLtpMinusHalfSpread()
    {
        // Some SENSEX contracts quote no book at all; a one-sided or crossed
        // book is no better for the side that is missing.
        using var book = new Book(Morning);
        book.Quote(Call, ltp: 100m, age: TimeSpan.FromSeconds(2));
        book.Quote(Put, ltp: 80m, bid: 81m, ask: 79m, age: TimeSpan.FromSeconds(2));

        await book.Open(Call, "SELL", price: 100m, group: "G1");
        await book.Open(Put, "BUY", price: 80m, group: "G2");

        var sell = book.Orders().Single(x => x.Symbol == Call);
        Assert.Equal(100m * (1m - 0.0015m), sell.FillPrice);
        Assert.Equal("ltp-less-half-spread", Read(sell.MetadataJson, "rule"));
        Assert.Equal("LTP 100.00 less half-spread (0.15%)", Read(sell.MetadataJson, "note"));

        var buy = book.Orders().Single(x => x.Symbol == Put);
        Assert.Equal(80m * (1m + 0.0015m), buy.FillPrice);
        Assert.Equal("ltp-plus-half-spread", Read(buy.MetadataJson, "rule"));
    }

    [Fact]
    public async Task The_half_spread_and_the_book_are_settings()
    {
        using var book = new Book(Morning, new PaperFillOptions { UseBidAsk = false, HalfSpreadFraction = 0.01m });
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(2));

        await book.Open(Call, "SELL", price: 100m);

        Assert.Equal(99m, book.Orders().Single().FillPrice);
    }

    [Fact]
    public async Task A_contract_with_no_quote_at_all_fills_at_the_signals_price_across_the_spread()
    {
        using var book = new Book(Morning);

        await book.Open(Call, "BUY", price: 100m);

        var order = book.Orders().Single();
        Assert.Equal(100m * 1.0015m, order.FillPrice);
        Assert.Equal("signal-plus-half-spread", Read(order.MetadataJson, "rule"));
    }

    [Fact]
    public async Task The_manual_ticket_and_a_replay_fill_as_given()
    {
        using var book = new Book(Morning);
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(2));

        // A limit the person typed, or the ask the ticket showed them.
        var ticket = book.Signal("OPEN_GROUP", "M1", Leg(Call, "BUY", 99.8m));
        ticket.LegPricesAreFinal = true;
        await book.Service.CreateSignalAsync(ticket);

        var order = book.Orders().Single();
        Assert.Equal(99.8m, order.FillPrice);
        Assert.Equal("signal", Read(order.MetadataJson, "rule"));

        using var replay = new Book(Morning, mode: PaperTradingService.OfflineReplayMode);
        replay.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromHours(3));
        await replay.Open(Call, "SELL", price: 101.25m);
        Assert.Equal(101.25m, replay.Orders().Single().FillPrice);
    }

    // ======================================================= stale quotes

    [Fact]
    public async Task StaleQuote_NotUsedForFill()
    {
        // 24 Sep: the feed stopped at 11:27:36 and the quote table froze with it.
        var now = Ist(11, 34, 0);
        using var book = new Book(now);
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, updatedUtc: Ist(11, 27, 36));
        book.Quote(Put, ltp: 80m, bid: 79.5m, ask: 80.5m, age: TimeSpan.FromSeconds(3));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => book.Service.CreateSignalAsync(
            book.Signal("OPEN_GROUP", "G1", Leg(Call, "SELL", 100m), Leg(Put, "SELL", 80m))));

        Assert.Contains($"{Call} (its latest quote is 384 s old; while the market is open a fill needs one under 60 s)", refused.Message);
        Assert.DoesNotContain(Put, refused.Message);
        // Neither leg, and no signal row a retry could mistake for a booking.
        Assert.Empty(book.Orders());
        Assert.Empty(book.Positions());
        Assert.Empty(book.Signals());
    }

    [Fact]
    public async Task A_strategys_close_on_a_stale_quote_is_refused_too()
    {
        using var book = new Book(Ist(11, 34, 0));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));
        await book.Open(Call, "SELL", price: 100m);
        book.Quote(Call, ltp: 140m, bid: 139.5m, ask: 140.5m, age: TimeSpan.FromMinutes(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => book.Service.CreateSignalAsync(
            book.Signal("CLOSE_GROUP", "G1", Leg(Call, "BUY", 140m))));

        Assert.Equal("Open", book.Positions().Single().Status);
    }

    [Fact]
    public async Task Out_of_hours_the_last_quote_still_prices_and_the_fill_says_how_old_it_was()
    {
        using var book = new Book(Ist(16, 0));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, updatedUtc: Ist(15, 29, 59));

        await book.Open(Call, "SELL", price: 100m);

        var order = book.Orders().Single();
        Assert.Equal(99.5m, order.FillPrice);
        Assert.Equal("filled at the bid; priced on a stale quote (1801 s old)", Read(order.MetadataJson, "note"));
        Assert.Equal("true", Read(order.MetadataJson, "staleQuote"));
    }

    [Fact]
    public async Task MarketCloseFlatten_ClosesOnStaleQuote_AndSaysSo()
    {
        // Unattended, the 15:30 flatten would have met the 11:27 prices. A leg
        // left open overnight is worse than a leg closed at a stale price.
        using var book = new Book(Ist(11, 0));
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));
        await book.Open(Call, "SELL", price: 100m);

        book.Clock.Now = Ist(15, 30, 1);
        book.Quote(Call, ltp: 90m, bid: 89.5m, ask: 90.5m, updatedUtc: Ist(11, 27, 36));

        int closed = await book.Service.FlattenRunAsync(book.RunId, "Market closed (15:30 IST)");

        Assert.Equal(1, closed);
        var position = book.Positions().Single();
        Assert.Equal("Closed", position.Status);
        var exit = book.Orders().Single(x => x.Side == "BUY");
        Assert.Equal(90.5m, exit.FillPrice);
        Assert.Equal("filled at the ask; priced on a stale quote (14545 s old)", Read(exit.MetadataJson, "note"));
    }

    [Fact]
    public async Task A_person_stopping_a_leg_closes_it_on_a_stale_quote_during_market_hours()
    {
        using var book = new Book(Ist(11, 0));
        book.Quote(Call, ltp: 100m, age: TimeSpan.FromSeconds(1));
        await book.Open(Call, "SELL", price: 100m);

        book.Clock.Now = Ist(11, 34, 0);
        book.Quote(Call, ltp: 90m, updatedUtc: Ist(11, 27, 36));

        int closed = await book.Service.ClosePositionsAsync(book.RunId, new[] { book.Positions().Single().Id }, "Squared off by trader", "trader");

        Assert.Equal(1, closed);
        var exit = book.Orders().Single(x => x.Side == "BUY");
        Assert.Equal(90m * 1.0015m, exit.FillPrice);
        Assert.Equal("LTP 90.00 plus half-spread (0.15%); priced on a stale quote (384 s old)", Read(exit.MetadataJson, "note"));
    }

    [Fact]
    public async Task A_mark_is_as_old_as_the_quote_it_came_from()
    {
        using var book = new Book(Ist(11, 0));
        book.Quote(Call, ltp: 100m, age: TimeSpan.FromSeconds(1));
        await book.Open(Call, "SELL", price: 100m);

        book.Clock.Now = Ist(11, 34, 0);
        book.Quote(Call, ltp: 90m, updatedUtc: Ist(11, 27, 36));
        var marked = (await book.Service.GetPaperPositionsAsync(book.RunId)).Single();

        // Re-read every few seconds by the guard, a frozen quote must not turn
        // into a fresh mark.
        Assert.Equal(90m, marked.LastMarkPrice);
        Assert.Equal(Ist(11, 27, 36), marked.UpdatedUtc);
    }

    // ======================================================= idempotency

    [Fact]
    public async Task SameClientSignalId_BooksOnce()
    {
        using var book = new Book(Morning);
        book.Quote(Call, ltp: 100m, bid: 99.5m, ask: 100.5m, age: TimeSpan.FromSeconds(1));

        var first = book.Signal("OPEN_GROUP", "G1", Leg(Call, "SELL", 100m));
        first.ClientSignalId = "3f0c6b0e-6a55-4d0b-9d0c-2b1f7f6a9c11";
        var booked = await book.Service.CreateSignalAsync(first);

        // The runner's retry after a lost answer: same id, sent unpriced.
        var retry = book.Signal("OPEN_GROUP", "G1", Leg(Call, "SELL", null));
        retry.ClientSignalId = first.ClientSignalId;
        var again = await book.Service.CreateSignalAsync(retry);

        Assert.Equal(booked.Id, again.Id);
        Assert.Equal(first.ClientSignalId, again.ClientSignalId);
        Assert.Single(book.Signals());
        Assert.Single(book.Orders());
        Assert.Equal(1, book.Positions().Single().Quantity);

        // A new id is a new signal.
        var another = book.Signal("OPEN_GROUP", "G2", Leg(Call, "SELL", 100m));
        another.ClientSignalId = "another";
        await book.Service.CreateSignalAsync(another);
        Assert.Equal(2, book.Signals().Count);
    }

    [Fact]
    public async Task A_retry_is_answered_even_after_the_run_began_to_stop()
    {
        using var book = new Book(Morning);
        book.Quote(Call, ltp: 100m, age: TimeSpan.FromSeconds(1));
        var first = book.Signal("OPEN_GROUP", "G1", Leg(Call, "SELL", 100m));
        first.ClientSignalId = "abc";
        var booked = await book.Service.CreateSignalAsync(first);

        book.SetRunStatus("Stopping");

        var again = await book.Service.CreateSignalAsync(first);
        Assert.Equal(booked.Id, again.Id);
    }

    [Fact]
    public async Task A_client_id_longer_than_the_column_is_refused()
    {
        using var book = new Book(Morning);
        var signal = book.Signal("OPEN_GROUP", "G1", Leg(Call, "SELL", 100m));
        signal.ClientSignalId = new string('x', PaperTradingService.MaxClientSignalIdLength + 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => book.Service.CreateSignalAsync(signal));
        Assert.Empty(book.Signals());
    }

    // ============================================================ helpers

    private static SimulationSignalLegRequest Leg(string symbol, string side, decimal? price, int lots = 1)
        => new() { Symbol = symbol, Side = side, Quantity = lots, Price = price };

    private static string? Read(string? json, string property)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    internal sealed class ManualClock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    /// <summary>One live run over an in-memory database, on a clock the test moves.</summary>
    private sealed class Book : IDisposable
    {
        private readonly TradingDbContext _db;

        public Book(DateTime nowUtc, PaperFillOptions? fills = null, string mode = PaperTradingService.LivePaperMode)
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase($"paper-fills-{Guid.NewGuid():N}")
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;
            _db = new TradingDbContext(options);

            var run = new SimulationRun
            {
                Mode = mode, Symbol = "NSE:NIFTY50-INDEX", Status = "Running",
                StrategyName = "Ghost", ParametersJson = "{}"
            };
            _db.SimulationRuns.Add(run);
            _db.SaveChanges();
            RunId = run.Id;

            Clock = new ManualClock(nowUtc);
            Service = new PaperTradingService(
                _db,
                RecapClockTests.Inert<IRiskManagementService>.Create(),
                new PositionGreeksTests.FixedLots(LotSize),
                PositionGreeksTests.Sessions(),
                Options.Create(fills ?? new PaperFillOptions()),
                Clock);
        }

        public long RunId { get; }
        public ManualClock Clock { get; }
        public PaperTradingService Service { get; }

        public void Quote(string symbol, decimal ltp, decimal? bid = null, decimal? ask = null,
            TimeSpan? age = null, DateTime? updatedUtc = null)
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
            row.UpdatedUtc = updatedUtc ?? Clock.Now - (age ?? TimeSpan.Zero);
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
            Legs = legs.ToList()
        };

        public Task<SimulationSignalResponse> Open(string symbol, string side, decimal? price, string group = "G1")
            => Service.CreateSignalAsync(Signal("OPEN_GROUP", group, Leg(symbol, side, price)));

        public void SetRunStatus(string status)
        {
            var run = _db.SimulationRuns.Single(x => x.Id == RunId);
            run.Status = status;
            _db.SaveChanges();
        }

        public List<PaperOrder> Orders() => _db.PaperOrders.AsNoTracking().Where(x => x.SimulationRunId == RunId).ToList();
        public List<PaperPosition> Positions() => _db.PaperPositions.AsNoTracking().Where(x => x.SimulationRunId == RunId).ToList();
        public List<SimulationSignal> Signals() => _db.SimulationSignals.AsNoTracking().Where(x => x.SimulationRunId == RunId).ToList();

        public void Dispose() => _db.Dispose();
    }
}
