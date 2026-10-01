using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.UnitTests;

/// <summary>
/// An open option leg's greeks and what they are worth to the position
/// (27 Sep, the owner: "if I have bought or sold an option, the Greeks' effect
/// should show too — like theta shows when you buy").
/// </summary>
public class PositionGreeksTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 5, 0, 0, DateTimeKind.Utc); // Mon 28 Sep 10:30 IST

    private static PositionGreeks.Figures Feed(DateTime at, decimal theta = -12.5m) =>
        new(PositionGreeks.SourceFeed, at, 13.6m, 0.52m, 0.0008m, theta, 8.2m);

    // ------------------------------------------------------------- rupees --

    [Fact]
    public void A_bought_option_pays_its_theta_and_a_sold_one_collects_it()
    {
        // 1 lot of NIFTY (75): theta −12.5 points a day per unit.
        var bought = PositionGreeks.ToResponse(Feed(Now), isLong: true, quantity: 75, Now);
        var sold = PositionGreeks.ToResponse(Feed(Now), isLong: false, quantity: 75, Now);

        Assert.Equal(-937.50m, bought.ThetaRupeesPerDay);
        Assert.Equal(937.50m, sold.ThetaRupeesPerDay);

        // Delta and vega flip with the side the same way.
        Assert.Equal(39m, bought.DeltaQuantity);
        Assert.Equal(-39m, sold.DeltaQuantity);
        Assert.Equal(bought.DeltaQuantity, bought.DeltaRupeesPerPoint);
        Assert.Equal(615m, bought.VegaRupeesPerIvPoint);
        Assert.Equal(-615m, sold.VegaRupeesPerIvPoint);

        // The per-unit figures are the market's, whichever side holds them.
        Assert.Equal(bought.Theta, sold.Theta);
        Assert.Equal(-12.5m, sold.Theta);
    }

    [Fact]
    public void Quantity_is_lots_times_the_lot_size()
    {
        // 3 lots × 30 (BANKNIFTY) = 90 units.
        var three = PositionGreeks.ToResponse(Feed(Now, theta: -20m), true, 90, Now);
        Assert.Equal(-1800m, three.ThetaRupeesPerDay);
    }

    [Fact]
    public void Totals_add_rupees_across_underlyings_but_delta_only_within_one()
    {
        var niftyLong = PositionGreeks.ToResponse(Feed(Now, -10m), true, 75, Now);     // −750/day, Δ +39
        var niftyShort = PositionGreeks.ToResponse(Feed(Now, -4m), false, 150, Now);   // +600/day, Δ −78
        var crude = PositionGreeks.ToResponse(Feed(Now, -2m), true, 100, Now);         // −200/day, Δ +52

        var totals = PositionGreeks.Totals(new[]
        {
            ("NIFTY", niftyLong), ("NIFTY", niftyShort), ("CRUDEOIL", crude)
        }, unpriced: 1)!;

        Assert.Equal(-350m, totals.ThetaRupeesPerDay);
        Assert.Equal(615m - 1230m + 820m, totals.VegaRupeesPerIvPoint);
        // A NIFTY point and a crude rupee are not the same move.
        Assert.Null(totals.NetDeltaQuantity);
        Assert.Equal(-39m, totals.ByUnderlying.Single(x => x.Underlying == "NIFTY").DeltaQuantity);
        Assert.Equal(52m, totals.ByUnderlying.Single(x => x.Underlying == "CRUDEOIL").DeltaQuantity);
        Assert.Equal(3, totals.Legs);
        Assert.Equal(1, totals.Unpriced);

        var single = PositionGreeks.Totals(new[] { ("NIFTY", niftyLong), ("NIFTY", niftyShort) }, 0)!;
        Assert.Equal(-39m, single.NetDeltaQuantity);
    }

    [Fact]
    public void A_future_is_delta_one_and_never_stale()
    {
        var future = PositionGreeks.ToResponse(PositionGreeks.DeltaOne(Now.AddDays(-2)), isLong: false, quantity: 100, Now);

        Assert.Equal(-100m, future.DeltaQuantity);
        Assert.Equal(0m, future.ThetaRupeesPerDay);
        Assert.False(future.Stale);
        Assert.Null(future.AsOfUtc);
    }

    // ------------------------------------------------------------ sources --

    [Fact]
    public void A_fresh_feed_wins_and_nothing_is_computed()
    {
        bool computed = false;
        var chosen = PositionGreeks.Choose(Feed(Now.AddSeconds(-5)), null, () => { computed = true; return null; }, Now);

        Assert.Equal(PositionGreeks.SourceFeed, chosen!.Source);
        Assert.False(computed);
    }

    [Fact]
    public void A_stale_feed_gives_way_to_a_fresh_chain_snapshot()
    {
        var chain = Feed(Now.AddSeconds(-40)) with { Source = PositionGreeks.SourceChain };
        var chosen = PositionGreeks.Choose(Feed(Now.AddMinutes(-30)), chain, () => null, Now);

        Assert.Equal(PositionGreeks.SourceChain, chosen!.Source);
    }

    [Fact]
    public void With_nothing_fresh_the_greeks_are_computed()
    {
        var computed = Feed(Now.AddSeconds(-2)) with { Source = PositionGreeks.SourceComputed };
        var chosen = PositionGreeks.Choose(Feed(Now.AddHours(-19)), null, () => computed, Now);

        Assert.Equal(PositionGreeks.SourceComputed, chosen!.Source);
    }

    [Fact]
    public void Yesterdays_feed_greeks_are_shown_as_yesterdays_never_as_now()
    {
        // Monday morning, before the carried contract's first tick: the only
        // figures are Friday's last quote. They are shown — stale, with their age.
        var friday = Now.AddDays(-3);
        var chosen = PositionGreeks.Choose(Feed(friday), null, () => null, Now)!;
        var response = PositionGreeks.ToResponse(chosen, true, 75, Now);

        Assert.True(response.Stale);
        Assert.Equal(friday, response.AsOfUtc);
    }

    [Fact]
    public void A_computation_is_as_old_as_the_older_price_it_used()
    {
        var optionAt = Now.AddMinutes(-10);
        var figures = PositionGreeks.Compute(
            isCall: false, optionPrice: 95m, optionAsOfUtc: optionAt,
            underlyingPrice: 24650m, underlyingAsOfUtc: Now,
            strike: 24650m, expiryUtc: new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc),
            onFuture: false)!;

        Assert.Equal(optionAt, figures.AsOfUtc);
        Assert.True(figures.Theta < 0m);
        Assert.InRange(figures.Delta, -0.6m, -0.4m);
        Assert.True(PositionGreeks.ToResponse(figures, true, 75, Now).Stale);
    }

    [Theory]
    [InlineData(0.1365, false, 13.65)]   // the enricher and the FYERS poller: a fraction
    [InlineData(13.65, true, 13.65)]     // Dhan's chain: a percent
    [InlineData(18.2, false, 18.2)]      // past any fraction the poller would store: already a percent
    public void Implied_volatility_is_read_in_percent_whatever_the_source_stored(double raw, bool vendorPercent, double expected)
    {
        Assert.Equal((decimal)expected, PositionGreeks.IvPercent((decimal)raw, vendorPercent));
    }

    [Fact]
    public void The_feed_is_not_trusted_on_a_monthly_symbol_it_dates_to_the_wrong_day()
    {
        // Until 27 Sep 2026 the enricher dated NIFTY26SEP… to the last THURSDAY
        // (24 Sep). It now dates NSE monthlies to the last Tuesday, as NSE does.
        Assert.True(PositionGreeks.FeedExpiryMatches("NSE:NIFTY26SEP24500CE", new DateOnly(2026, 9, 29)));
        Assert.False(PositionGreeks.FeedExpiryMatches("NSE:NIFTY26SEP24500CE", new DateOnly(2026, 9, 24)));
        // BSE's monthlies are the last Thursday; NSE's were too before Sep 2025.
        Assert.True(PositionGreeks.FeedExpiryMatches("BSE:SENSEX26OCT81000CE", new DateOnly(2026, 10, 29)));
        Assert.False(PositionGreeks.FeedExpiryMatches("BSE:SENSEX26OCT81000CE", new DateOnly(2026, 10, 27)));
        Assert.True(PositionGreeks.FeedExpiryMatches("NSE:NIFTY25AUG24500CE", new DateOnly(2025, 8, 28)));
        // Moved by a holiday (Tue 24 Nov 2026): right only if the enricher's
        // calendar knew it, so not taken on trust.
        Assert.False(PositionGreeks.FeedExpiryMatches("NSE:NIFTY26NOV24500CE", new DateOnly(2026, 11, 23)));
        // A weekly carries its own date.
        Assert.True(PositionGreeks.FeedExpiryMatches("NSE:NIFTY2692924500CE", new DateOnly(2026, 9, 29)));
    }

    // ------------------------------------------------------------ builder --

    [Fact]
    public async Task The_builder_prices_each_leg_from_the_best_source_and_sums_the_book()
    {
        using var db = NewDb();
        const string niftyPut = "NSE:NIFTY2692924600PE";
        const string crudeCall = "MCX:CRUDEOIL26OCT6500CE";
        const string crudeFut = "MCX:CRUDEOIL26OCTFUT";
        db.Instruments.AddRange(
            Instrument(niftyPut, "NSE", "FO", "PE", "NIFTY", 24600m, new DateOnly(2026, 9, 29)),
            Instrument(crudeCall, "MCX", "COM", "CE", "CRUDEOIL", 6500m, new DateOnly(2026, 10, 15)),
            Instrument(crudeFut, "MCX", "COM", "FUT", "CRUDEOIL", null, new DateOnly(2026, 10, 19)));
        // The NIFTY put's quote carries fresh greeks from the enricher.
        db.LiveQuotesLatest.Add(Quote(niftyPut, 88m, Now.AddSeconds(-3), iv: 0.121m, delta: -0.45m, gamma: 0.0009m, theta: -14.2m, vega: 9.1m));
        // The crude call has only prices: no enricher greeks for MCX.
        db.LiveQuotesLatest.Add(Quote(crudeCall, 142m, Now.AddSeconds(-4)));
        db.LiveQuotesLatest.Add(Quote(crudeFut, 6540m, Now.AddSeconds(-1)));
        await db.SaveChangesAsync();

        var builder = new PositionGreeksBuilder(db, Sessions());
        var built = await builder.BuildAsync(new[]
        {
            Leg(1, niftyPut, "NIFTY", 24600m, "PE", new DateOnly(2026, 9, 29), "NSE", "PE", isLong: false, qty: 75, 88m, Now.AddSeconds(-3)),
            Leg(2, crudeCall, "CRUDEOIL", 6500m, "CE", new DateOnly(2026, 10, 15), "MCX", "CE", isLong: true, qty: 100, 142m, Now.AddSeconds(-4)),
            Leg(3, crudeFut, "CRUDEOIL", null, "", new DateOnly(2026, 10, 19), "MCX", "FUT", isLong: false, qty: 100, 6540m, Now.AddSeconds(-1)),
        }, Now, CancellationToken.None);

        var put = built.ByPosition[1];
        Assert.Equal(PositionGreeks.SourceFeed, put.Source);
        Assert.Equal(12.1m, put.IvPercent);
        Assert.Equal(1065m, put.ThetaRupeesPerDay);   // sold: −14.2 × 75 × −1
        Assert.False(put.Stale);

        var call = built.ByPosition[2];
        Assert.Equal(PositionGreeks.SourceComputed, call.Source);
        Assert.Equal(6540m, call.UnderlyingPrice);     // the October future it is written on
        Assert.True(call.ThetaRupeesPerDay < 0m);      // bought: time costs
        Assert.InRange(call.IvPercent!.Value, 5m, 150m);

        var future = built.ByPosition[3];
        Assert.Equal(PositionGreeks.SourceDeltaOne, future.Source);
        Assert.Equal(-100m, future.DeltaQuantity);

        var totals = built.Totals!;
        Assert.Equal(put.ThetaRupeesPerDay + call.ThetaRupeesPerDay, totals.ThetaRupeesPerDay);
        Assert.Equal(call.DeltaQuantity - 100m, totals.ByUnderlying.Single(x => x.Underlying == "CRUDEOIL").DeltaQuantity);
        Assert.Equal(0, totals.Unpriced);
    }

    [Fact]
    public async Task An_option_nothing_can_price_is_counted_not_guessed()
    {
        using var db = NewDb();
        const string put = "NSE:NIFTY2692924600PE";
        db.Instruments.Add(Instrument(put, "NSE", "FO", "PE", "NIFTY", 24600m, new DateOnly(2026, 9, 29)));
        await db.SaveChangesAsync();

        // No quote for the option or for NIFTY itself.
        var built = await new PositionGreeksBuilder(db, Sessions()).BuildAsync(new[]
        {
            Leg(1, put, "NIFTY", 24600m, "PE", new DateOnly(2026, 9, 29), "NSE", "PE", true, 75, null, null)
        }, Now, CancellationToken.None);

        Assert.Empty(built.ByPosition);
        Assert.Null(built.Totals);
    }

    // ------------------------------------------------------ market replay --

    // A recap run of Fri 11 Sep, played back on 28 Sep. Its call expired on Tue 15 Sep.
    private static readonly DateOnly ReplayedDay = new(2026, 9, 11);
    private static readonly DateTime ReplayClock = new(2026, 9, 11, 5, 0, 0, DateTimeKind.Utc);  // 10:30 IST that day
    private static readonly DateOnly ReplayedExpiry = new(2026, 9, 15);
    private const string ReplayedCall = "NSE:NIFTY2691525000CE";

    private static UpsertLiveTickRequest ReplayTick(string symbol, decimal ltp, DateTime stamp) => new()
    {
        Symbol = symbol, LastTradedPrice = ltp, ExchangeTimestampUtc = stamp, SourceKey = "desk-replay", IsReplay = true
    };

    /// <summary>A book replaying 11 Sep at 10:30, with NIFTY at 25,000 and the 25000 CE at 95.</summary>
    private static MarketReplayBook ReplayAtTenThirty(bool withCall = true)
    {
        var book = new MarketReplayBook();
        book.Begin(ReplayedDay);
        var ticks = new List<UpsertLiveTickRequest> { ReplayTick(UnderlyingCatalog.SpotSymbolFor("NIFTY"), 25000m, ReplayClock) };
        if (withCall) ticks.Add(ReplayTick(ReplayedCall, 95m, ReplayClock.AddSeconds(-20)));
        book.Apply(ticks);
        return book;
    }

    /// <summary>Today's live desk: a fresh quote of the same contract with greeks of its own, and NIFTY at 26,000.</summary>
    private static async Task AddLiveQuotesAsync(TradingDbContext db)
    {
        db.LiveQuotesLatest.Add(Quote(ReplayedCall, 140m, Now.AddSeconds(-2), iv: 0.2m, delta: 0.9m, gamma: 0.0001m, theta: -1m, vega: 0.5m));
        db.LiveQuotesLatest.Add(Quote(UnderlyingCatalog.SpotSymbolFor("NIFTY"), 26000m, Now.AddSeconds(-1)));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_recap_run_of_the_replayed_day_is_priced_from_the_replay_on_its_clock()
    {
        using var db = NewDb();
        await AddLiveQuotesAsync(db);
        var book = ReplayAtTenThirty();
        var leg = Leg(1, ReplayedCall, "NIFTY", 25000m, "CE", ReplayedExpiry, "NSE", "CE", isLong: true, qty: 75, 95m, Now);

        var built = await new PositionGreeksBuilder(db, Sessions(), book).BuildAsync(new[] { leg }, Now, CancellationToken.None, replayPriced: true);

        // Computed from the replay's own prices (no greeks come with a replayed tick), with the time to
        // expiry counted from the replayed moment: 11 Sep 10:30, four days before the expiry.
        var expected = PositionGreeks.Compute(
            isCall: true, optionPrice: 95m, optionAsOfUtc: ReplayClock.AddSeconds(-20),
            underlyingPrice: 25000m, underlyingAsOfUtc: ReplayClock,
            strike: 25000m, expiryUtc: ExpirySettler.ExpiryCloseUtc(Sessions(), "NSE", "FO", ReplayedExpiry),
            onFuture: false)!;
        var call = built.ByPosition[1];
        Assert.Equal(PositionGreeks.SourceComputed, call.Source);
        Assert.Equal(25000m, call.UnderlyingPrice);            // the replay's NIFTY, not today's 26,000
        Assert.Equal(expected.IvPercent, call.IvPercent);
        Assert.Equal(expected.Theta, call.Theta);
        Assert.Equal(expected.Delta, call.Delta);
        Assert.Equal(ReplayClock.AddSeconds(-20), call.AsOfUtc);
        // Fresh on the replay's clock, though the wall clock is seventeen days on.
        Assert.False(call.Stale);
        Assert.True(call.ThetaRupeesPerDay < 0m);
        Assert.Equal(0, built.Totals!.Unpriced);
    }

    [Fact]
    public async Task A_live_run_reads_the_live_greeks_while_a_replay_plays()
    {
        using var db = NewDb();
        await AddLiveQuotesAsync(db);
        var book = ReplayAtTenThirty();
        var leg = Leg(1, ReplayedCall, "NIFTY", 25000m, "CE", ReplayedExpiry, "NSE", "CE", isLong: true, qty: 75, 140m, Now.AddSeconds(-2));

        var built = await new PositionGreeksBuilder(db, Sessions(), book).BuildAsync(new[] { leg }, Now, CancellationToken.None);

        var call = built.ByPosition[1];
        Assert.Equal(PositionGreeks.SourceFeed, call.Source);
        Assert.Equal(0.9m, call.Delta);
    }

    [Fact]
    public async Task A_replayed_quote_that_carries_greeks_is_read_as_it_is()
    {
        using var db = NewDb();
        await AddLiveQuotesAsync(db);
        var book = new FakeReplayBook(ReplayedDay, ReplayClock);
        book.Quotes[ReplayedCall] = new LiveQuoteResponse
        {
            Symbol = ReplayedCall, LastTradedPrice = 95m, ExchangeTimestampUtc = ReplayClock.AddSeconds(-5),
            ImpliedVolatility = 0.131m, Delta = 0.51m, Gamma = 0.0007m, Theta = -11.5m, Vega = 7.9m
        };
        var leg = Leg(1, ReplayedCall, "NIFTY", 25000m, "CE", ReplayedExpiry, "NSE", "CE", isLong: true, qty: 75, 95m, Now);

        var built = await new PositionGreeksBuilder(db, Sessions(), book).BuildAsync(new[] { leg }, Now, CancellationToken.None, replayPriced: true);

        var call = built.ByPosition[1];
        Assert.Equal(PositionGreeks.SourceFeed, call.Source);
        Assert.Equal((0.51m, -11.5m, (decimal?)13.1m), (call.Delta, call.Theta, call.IvPercent));
        Assert.Equal(ReplayClock.AddSeconds(-5), call.AsOfUtc);
        Assert.False(call.Stale);
    }

    [Fact]
    public async Task A_replayed_leg_the_replay_has_no_price_for_is_counted_not_priced_off_its_stored_mark()
    {
        using var db = NewDb();
        await AddLiveQuotesAsync(db);
        var book = ReplayAtTenThirty(withCall: false);
        var leg = Leg(1, ReplayedCall, "NIFTY", 25000m, "CE", ReplayedExpiry, "NSE", "CE", isLong: true, qty: 75, 95m, Now);

        var built = await new PositionGreeksBuilder(db, Sessions(), book).BuildAsync(new[] { leg }, Now, CancellationToken.None, replayPriced: true);

        Assert.Empty(built.ByPosition);
    }

    [Fact]
    public async Task The_run_page_of_a_recap_run_shows_the_replays_greeks()
    {
        using var db = NewDb();
        db.Instruments.Add(Instrument(ReplayedCall, "NSE", "FO", "CE", "NIFTY", 25000m, ReplayedExpiry));
        await AddLiveQuotesAsync(db);
        var book = ReplayAtTenThirty();

        var view = new PositionViewBuilder(db, new FixedLots(75), new PositionGreeksBuilder(db, Sessions(), book), book);
        var built = await view.BuildAsync<LivePositionResponse>(new[]
        {
            new PaperPositionResponse
            {
                Id = 1, SimulationRunId = 7, GroupId = "G1", Symbol = ReplayedCall, Direction = "LONG",
                Quantity = 1, AveragePrice = 90m, LastMarkPrice = 95m, Status = "Open",
                OpenedUtc = DateTime.UtcNow.AddMinutes(-5), UpdatedUtc = DateTime.UtcNow
            }
        }, useLiveQuotes: true, spotSymbol: UnderlyingCatalog.SpotSymbolFor("NIFTY"), CancellationToken.None, replayPriced: true);

        var row = built.Positions.Single();
        Assert.Equal(95m, row.Ltp);
        Assert.Equal(PositionGreeks.SourceComputed, row.Greeks!.Source);
        Assert.Equal(25000m, row.Greeks.UnderlyingPrice);
        Assert.Equal(row.Greeks.ThetaRupeesPerDay, built.Greeks!.ThetaRupeesPerDay);
    }

    // ------------------------------------------------- carried positions --

    [Fact]
    public async Task A_carried_leg_with_no_quote_yet_shows_its_last_mark_with_that_marks_age()
    {
        using var db = NewDb();
        const string call = "NSE:NIFTY2692924500CE";
        db.Instruments.Add(Instrument(call, "NSE", "FO", "CE", "NIFTY", 24500m, new DateOnly(2026, 9, 29)));
        await db.SaveChangesAsync();

        var fridayClose = new DateTime(2026, 9, 25, 9, 59, 0, DateTimeKind.Utc);
        var view = new PositionViewBuilder(db, new FixedLots(75), new PositionGreeksBuilder(db, Sessions()));
        var built = await view.BuildAsync<LivePositionResponse>(new[]
        {
            new PaperPositionResponse
            {
                Id = 1, SimulationRunId = 7, GroupId = "MANUAL-1", Symbol = call, Direction = "LONG",
                Quantity = 1, AveragePrice = 120m, LastMarkPrice = 131m, Status = "Open",
                OpenedUtc = fridayClose.AddHours(-3), UpdatedUtc = fridayClose
            }
        }, useLiveQuotes: true, spotSymbol: "MANUAL", CancellationToken.None);

        var row = built.Positions.Single();
        Assert.Equal(131m, row.Ltp);
        // Was null: the console could not tell Friday's price from today's.
        Assert.Equal(fridayClose, row.LtpUpdatedUtc);
    }

    [Fact]
    public async Task A_carried_leg_is_marked_to_its_live_quote_with_the_quotes_age()
    {
        using var db = NewDb();
        const string call = "NSE:NIFTY2692924500CE";
        db.Instruments.Add(Instrument(call, "NSE", "FO", "CE", "NIFTY", 24500m, new DateOnly(2026, 9, 29)));
        var tick = DateTime.UtcNow.AddSeconds(-2);
        db.LiveQuotesLatest.Add(Quote(call, 140m, tick));
        await db.SaveChangesAsync();

        var view = new PositionViewBuilder(db, new FixedLots(75), new PositionGreeksBuilder(db, Sessions()));
        var built = await view.BuildAsync<LivePositionResponse>(new[]
        {
            new PaperPositionResponse
            {
                Id = 1, SimulationRunId = 7, GroupId = "MANUAL-1", Symbol = call, Direction = "LONG",
                Quantity = 2, AveragePrice = 120m, LastMarkPrice = 131m, Status = "Open",
                OpenedUtc = DateTime.UtcNow.AddDays(-3), UpdatedUtc = DateTime.UtcNow.AddDays(-3)
            }
        }, useLiveQuotes: true, spotSymbol: "MANUAL", CancellationToken.None);

        var row = built.Positions.Single();
        Assert.Equal(140m, row.Ltp);
        Assert.Equal(tick, row.LtpUpdatedUtc);
        Assert.Equal(150, row.Quantity);
    }

    // ------------------------------------------------------------ helpers --

    private static TradingDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"greeks-{Guid.NewGuid():N}").Options);

    internal static IMarketSessionService Sessions() => new MarketSessionService(new OpenCalendar());

    private static Instrument Instrument(string symbol, string exchange, string segment, string type, string underlying, decimal? strike, DateOnly expiry) => new()
    {
        Symbol = symbol, Exchange = exchange, Segment = segment, InstrumentType = type,
        OptionType = type is "CE" or "PE" ? type : string.Empty,
        Underlying = underlying, StrikePrice = strike, ExpiryDate = expiry, IsEnabled = true
    };

    private static LiveQuoteLatest Quote(string symbol, decimal ltp, DateTime at,
        decimal? iv = null, decimal? delta = null, decimal? gamma = null, decimal? theta = null, decimal? vega = null) => new()
    {
        Symbol = symbol, LastTradedPrice = ltp, UpdatedUtc = at, SourceKey = "dhan", RawPayload = "{}",
        ImpliedVolatility = iv, Delta = delta, Gamma = gamma, Theta = theta, Vega = vega
    };

    private static PositionGreeksBuilder.OpenLeg Leg(long id, string symbol, string underlying, decimal? strike, string optionType,
        DateOnly expiry, string exchange, string instrumentType, bool isLong, int qty, decimal? ltp, DateTime? at) =>
        new(id, symbol,
            new ContractInfo { Underlying = underlying, Strike = strike, OptionType = optionType, ExpiryDate = expiry, Label = symbol },
            exchange, instrumentType, isLong, qty, ltp, at);

    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public bool IsLoaded => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A replay book whose quotes are set by hand: the real one keeps no greeks, as no replayed tick carries any.</summary>
    private sealed class FakeReplayBook(DateOnly day, DateTime clockUtc) : IMarketReplayBook
    {
        public Dictionary<string, LiveQuoteResponse> Quotes { get; } = new(StringComparer.Ordinal);
        public DateOnly? Day => day;
        public DateTime? ClockUtc => clockUtc;
        public void Begin(DateOnly replayed) { }
        public void End() { }
        public int Apply(IReadOnlyList<UpsertLiveTickRequest> ticks) => 0;
        public LiveQuoteResponse? Quote(string symbol) => Quotes.GetValueOrDefault(symbol);
        public IReadOnlyList<LiveQuoteResponse> AllQuotes() => Quotes.Values.ToList();
        public LiveBarResponse? CurrentMinute(string symbol) => null;
        public bool Prices(string? parametersJson) => true;
    }

    internal sealed class FixedLots(int lotSize) : ILotSizeResolver
    {
        public Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default)
            => Task.FromResult(new LotSizeInfo(lotSize, "test", "NIFTY"));

        public Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, LotSizeInfo>>(
                symbols.Distinct().ToDictionary(s => s, _ => new LotSizeInfo(lotSize, "test", "NIFTY")));

        public Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default)
            => Task.FromResult(new LotSizeInfo(lotSize, "test", underlying));
    }
}
