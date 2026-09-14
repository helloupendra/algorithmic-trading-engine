using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The live option chain: which quotes may replace the per-minute snapshot, and
/// the header strip read off the result. The rule under test throughout is the
/// owner's: never show a stale number as live.
/// </summary>
public class OptionChainLiveViewTests
{
    private static readonly DateTime Snapshot = new(2026, 9, 15, 5, 0, 0, DateTimeKind.Utc); // 10:30 IST
    private static readonly DateTime Now = Snapshot.AddSeconds(40);

    // ------------------------------------------------------------- overlay

    [Fact]
    public void A_fresh_quote_brings_the_leg_up_to_the_second_and_re_reads_the_build_up()
    {
        // Snapshot: LTP 100, up 20 on a previous close of 80; OI 5,000 on a baseline of 4,000.
        var leg = Leg("NSE:BANKNIFTY2692957500CE", ltp: 100m, change: 20m, oi: 5000, baseline: 4000);
        var quote = Quote(leg.Symbol, ltp: 70m, oi: 6000, volume: 90_000, updated: Now.AddSeconds(-2), bid: 69.5m, ask: 70.5m);

        Assert.True(OptionChainLiveView.ApplyQuote(leg, quote, Snapshot, Now));

        Assert.True(leg.IsLive);
        Assert.Equal(70m, leg.LastTradedPrice);
        Assert.Equal(69.5m, leg.BidPrice);
        Assert.Equal(90_000, leg.Volume);
        Assert.Equal(6000, leg.OpenInterest);
        // Re-measured from the snapshot's own baselines, not from the snapshot's values.
        Assert.Equal(-10m, leg.PriceChange);
        Assert.Equal(-12.5m, leg.PriceChangePercent);
        Assert.Equal(2000, leg.OpenInterestChange);
        Assert.Equal(50m, leg.OpenInterestChangePercent);
        // Price was up on the snapshot (long build); live it is down with OI up.
        Assert.Equal(nameof(BuildUp.ShortBuildUp), leg.BuildUp);
    }

    [Fact]
    public void A_quote_older_than_two_minutes_is_not_shown_as_live()
    {
        var snapshot = Snapshot;
        var now = snapshot.AddMinutes(4);
        var leg = Leg("NSE:NIFTY2691524000PE", ltp: 50m, change: 5m, oi: 1000, baseline: 900);
        var stale = Quote(leg.Symbol, ltp: 60m, oi: 1200, updated: now.AddMinutes(-3));

        Assert.False(OptionChainLiveView.ApplyQuote(leg, stale, snapshot.AddMinutes(-10), now));
        Assert.False(leg.IsLive);
        Assert.Equal(50m, leg.LastTradedPrice);
        Assert.Equal(1000, leg.OpenInterest);
    }

    [Fact]
    public void A_fresh_quote_older_than_the_snapshot_does_not_overwrite_it()
    {
        // The feed last heard of this strike before the recorder captured it.
        var leg = Leg("NSE:NIFTY2691524000PE", ltp: 50m, change: 5m, oi: 1000, baseline: 900);
        var quote = Quote(leg.Symbol, ltp: 48m, oi: 990, updated: Snapshot.AddSeconds(-20));

        Assert.False(OptionChainLiveView.ApplyQuote(leg, quote, Snapshot, Now));
        Assert.Equal(50m, leg.LastTradedPrice);
    }

    [Fact]
    public void A_live_oi_an_order_of_magnitude_off_the_snapshot_is_kept_out()
    {
        // Lots against shares: 30x. Price still overlays; OI does not.
        var leg = Leg("NSE:BANKNIFTY2692957500CE", ltp: 100m, change: 20m, oi: 300_000, baseline: 290_000);
        var quote = Quote(leg.Symbol, ltp: 101m, oi: 10_000, updated: Now);

        Assert.True(OptionChainLiveView.ApplyQuote(leg, quote, Snapshot, Now));
        Assert.Equal(101m, leg.LastTradedPrice);
        Assert.Equal(300_000, leg.OpenInterest);
        Assert.Equal(10_000, leg.OpenInterestChange);
    }

    [Fact]
    public void A_feed_without_a_book_leaves_the_snapshots_bid_and_ask()
    {
        var leg = Leg("MCX:CRUDEOIL26SEP10000CE", ltp: 270m, change: 97m, oi: 7900, baseline: 7000);
        leg.BidPrice = 269m;
        leg.AskPrice = 271m;

        OptionChainLiveView.ApplyQuote(leg, Quote(leg.Symbol, ltp: 272m, oi: 7950, updated: Now), Snapshot, Now);

        Assert.Equal(269m, leg.BidPrice);
        Assert.Equal(271m, leg.AskPrice);
    }

    [Fact]
    public void The_overlay_counts_live_legs_and_names_the_newest_quote()
    {
        var chain = Chain(spot: 57369.65m,
            (57300m, 10_000, 30_000),
            (57400m, 20_000, 15_000));
        var quotes = new Dictionary<string, LiveQuoteSample>
        {
            [chain.Strikes[0].Call!.Symbol] = Quote(chain.Strikes[0].Call!.Symbol, 900m, 10_500, updated: Now.AddSeconds(-5)),
            [chain.Strikes[1].Put!.Symbol] = Quote(chain.Strikes[1].Put!.Symbol, 450m, 15_500, updated: Now.AddSeconds(-1)),
            // Stale: counted as a leg, not as live.
            [chain.Strikes[1].Call!.Symbol] = Quote(chain.Strikes[1].Call!.Symbol, 800m, 20_100, updated: Now.AddMinutes(-5)),
        };

        var result = OptionChainLiveView.Overlay(chain, quotes, Snapshot, Now);

        Assert.Equal(2, result.LiveLegs);
        Assert.Equal(4, result.TotalLegs);
        Assert.Equal(Now.AddSeconds(-1), result.NewestUtc);
        Assert.Equal("dhan", result.SourceKey);
    }

    // ------------------------------------------------------------- header

    [Fact]
    public void Support_is_the_heaviest_put_strike_and_resistance_the_heaviest_call_strike()
    {
        var chain = Chain(spot: 57369.65m,
            (57200m, 80_000, 196_000),
            (57300m, 102_000, 347_000),
            (57400m, 137_000, 299_000),
            (57500m, 1_949_000, 1_943_000),
            (57600m, 252_000, 116_000));
        OptionChainLiveView.Summarise(chain);

        var header = OptionChainLiveView.Header(chain, Now, Now);

        Assert.Equal(57500m, header.SupportStrike);
        Assert.Equal(1_943_000, header.SupportOpenInterest);
        Assert.Equal(57500m, header.ResistanceStrike);
        Assert.Equal(1_949_000, header.ResistanceOpenInterest);
        Assert.Equal(57400m, header.AtTheMoneyStrike);
        Assert.Equal(chain.TotalPutOpenInterest, header.TotalPutOpenInterest);
    }

    [Fact]
    public void The_spot_line_sits_between_the_strikes_either_side_of_the_spot()
    {
        var strikes = new[] { 57200m, 57300m, 57400m, 57500m };

        Assert.Equal((57300m, (decimal?)57400m), OptionChainLiveView.SpotBetween(strikes, 57369.65m));
        // On a strike: that strike is below the line.
        Assert.Equal((57400m, (decimal?)57500m), OptionChainLiveView.SpotBetween(strikes, 57400m));
        // Off the ends: one side is open.
        Assert.Equal(((decimal?)null, (decimal?)57200m), OptionChainLiveView.SpotBetween(strikes, 57000m));
        Assert.Equal((57500m, (decimal?)null), OptionChainLiveView.SpotBetween(strikes, 58000m));
        Assert.Equal(((decimal?)null, (decimal?)null), OptionChainLiveView.SpotBetween(strikes, 0m));
    }

    [Fact]
    public void Days_to_expiry_are_counted_on_the_Indian_calendar()
    {
        var expiry = new DateOnly(2026, 9, 29);

        Assert.Equal(15, OptionChainLiveView.DaysToExpiry(expiry, new DateTime(2026, 9, 14, 10, 0, 0, DateTimeKind.Utc)));
        // 20:00 UTC on the 14th is already 01:30 on the 15th in India.
        Assert.Equal(14, OptionChainLiveView.DaysToExpiry(expiry, new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(0, OptionChainLiveView.DaysToExpiry(expiry, new DateTime(2026, 9, 29, 4, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Atm_iv_is_the_mean_of_both_sides_or_whichever_is_priced()
    {
        var chain = Chain(spot: 100m, (100m, 10, 10));
        OptionChainLiveView.Summarise(chain);
        chain.Strikes[0].Call!.ImpliedVolatility = 14m;
        chain.Strikes[0].Put!.ImpliedVolatility = 16m;
        Assert.Equal(15m, OptionChainLiveView.AtTheMoneyIv(chain));

        chain.Strikes[0].Put!.ImpliedVolatility = 0m; // exactly zero = not priced
        Assert.Equal(14m, OptionChainLiveView.AtTheMoneyIv(chain));
    }

    [Fact]
    public void The_pcr_of_oi_change_is_only_given_when_both_sides_added_contracts()
    {
        Assert.Equal(2m, OptionChainLiveView.PutCallRatioOfChange(50_000, 100_000));
        Assert.Null(OptionChainLiveView.PutCallRatioOfChange(-50_000, 50_000));
        Assert.Null(OptionChainLiveView.PutCallRatioOfChange(0, 50_000));
    }

    // ------------------------------------------------------------- spot and previous close

    [Fact]
    public void A_live_spot_quote_wins_over_the_snapshot_spot()
    {
        var quote = OptionChainLiveView.QuoteOf(
            Quote("NSE:NIFTYBANK-INDEX", 57369.65m, null, updated: Now.AddSeconds(-1), previousClose: 57380.60m), Now, marketOpen: true);

        var spot = OptionChainLiveView.ChooseSpot(quote, "NSE:NIFTYBANK-INDEX", 57300m, Snapshot, "dhan");

        Assert.True(spot!.IsLive);
        Assert.Equal("live-quote", spot.Basis);
        Assert.Equal(57369.65m, spot.LastPrice);
        Assert.Equal(-10.95m, spot.Change);
        Assert.Equal("feed", spot.PreviousCloseBasis);
    }

    [Fact]
    public void A_stale_spot_quote_older_than_the_snapshot_gives_way_to_the_snapshot_but_lends_its_previous_close()
    {
        var quote = OptionChainLiveView.QuoteOf(
            Quote("NSE:NIFTYBANK-INDEX", 57000m, null, updated: Snapshot.AddMinutes(-30), previousClose: 57380.60m), Now, marketOpen: true);

        var spot = OptionChainLiveView.ChooseSpot(quote, "NSE:NIFTYBANK-INDEX", 57400m, Snapshot, "dhan");

        Assert.False(spot!.IsLive);
        Assert.Equal("snapshot", spot.Basis);
        Assert.Equal(57400m, spot.LastPrice);
        Assert.Equal(Snapshot, spot.AsOfUtc);
        Assert.Equal(19.40m, spot.Change);
    }

    [Fact]
    public void After_the_bell_the_last_quote_is_shown_with_its_own_time_and_not_as_live()
    {
        var closeUtc = Snapshot.AddMinutes(1);
        var now = Snapshot.AddHours(3);
        var quote = OptionChainLiveView.QuoteOf(Quote("NSE:NIFTYBANK-INDEX", 57410m, null, updated: closeUtc), now, marketOpen: false);

        var spot = OptionChainLiveView.ChooseSpot(quote, "NSE:NIFTYBANK-INDEX", 57400m, Snapshot, "dhan");

        Assert.False(spot!.IsLive);
        Assert.Equal("last-quote", spot.Basis);
        Assert.Equal(closeUtc, spot.AsOfUtc);
    }

    [Fact]
    public void A_last_quote_from_another_day_does_not_move_an_older_chain()
    {
        // A 9 Sep capture, an 11 Sep last quote: the chain is read against its own spot.
        var capture = new DateTime(2026, 9, 9, 9, 59, 0, DateTimeKind.Utc);
        var quote = OptionChainLiveView.QuoteOf(
            Quote("NSE:NIFTYBANK-INDEX", 56471.95m, null, updated: new DateTime(2026, 9, 11, 0, 10, 0, DateTimeKind.Utc), previousClose: 56295.60m),
            new DateTime(2026, 9, 14, 16, 0, 0, DateTimeKind.Utc), marketOpen: false);

        var spot = OptionChainLiveView.ChooseSpot(quote, "NSE:NIFTYBANK-INDEX", 57000m, capture, "fyers");

        Assert.Equal("snapshot", spot!.Basis);
        Assert.Equal(57000m, spot.LastPrice);
        // Nor does it lend 10 Sep's close as the 9 Sep chain's baseline.
        Assert.Null(spot.Change);
    }

    [Fact]
    public void Dhans_packet_close_stands_in_for_the_previous_close_but_not_when_it_equals_the_last_price_after_the_bell()
    {
        var quote = Quote("MCX:CRUDEOIL26SEPFUT", 9971m, 18234, updated: Now, packetClose: 9527m);

        Assert.Equal(((decimal?)9527m, "dhan-close-field"), OptionChainLiveView.PreviousCloseOf(quote, marketOpen: true));
        Assert.Equal(((decimal?)null, (string?)null),
            OptionChainLiveView.PreviousCloseOf(quote with { PacketClose = 9971m }, marketOpen: false));
        // Another vendor's close field is not assumed to mean the same thing.
        Assert.Equal(((decimal?)null, (string?)null),
            OptionChainLiveView.PreviousCloseOf(quote with { SourceKey = "truedata" }, marketOpen: true));
    }

    [Fact]
    public void The_close_is_read_out_of_the_raw_payload_and_nothing_else_is_assumed()
    {
        Assert.Equal(9527.0m, OptionChainLiveView.PacketCloseOf("{\"type\":\"full\",\"ltq\":1,\"close\":9527.0}"));
        Assert.Null(OptionChainLiveView.PacketCloseOf("{\"type\":\"full\"}"));
        Assert.Null(OptionChainLiveView.PacketCloseOf(""));
        Assert.Null(OptionChainLiveView.PacketCloseOf("{not json"));
    }

    [Fact]
    public void A_future_carries_its_premium_over_spot()
    {
        var future = OptionChainLiveView.FutureOf(
            new OptionChainQuoteResponse { Symbol = "NSE:BANKNIFTY26SEPFUT", LastPrice = 57775m, IsLive = true, Basis = "live-quote" },
            new DateOnly(2026, 9, 29), 57369.65m);

        Assert.Equal(405.35m, future!.PremiumOverSpot);
        Assert.True(future.IsLive);
    }

    [Fact]
    public void A_long_session_is_thinned_evenly_and_always_ends_on_the_newest_capture()
    {
        var items = Enumerable.Range(0, 1067).ToList();
        var thinned = OptionChainService.Thin(items, 400);

        Assert.True(thinned.Count <= 401);
        Assert.Equal(0, thinned[0]);
        Assert.Equal(1066, thinned[^1]);
        Assert.Same(items, OptionChainService.Thin(items, 2000));
    }

    // ------------------------------------------------------------- the service end to end

    [Fact]
    public async Task The_view_overlays_fresh_quotes_live_and_ignores_them_in_replay()
    {
        await using var db = Db();
        var captured = DateTime.UtcNow.AddSeconds(-50);
        captured = new DateTime(captured.Ticks - captured.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var expiry = IstTime.DateOf(DateTime.UtcNow).AddDays(3);

        foreach (var (strike, callLtp, putLtp) in new[] { (9900m, 315m, 240m), (10000m, 270m, 297m), (10100m, 227m, 357m) })
        {
            db.OptionChainSnapshots.Add(Row("CE", strike, callLtp, captured, expiry));
            db.OptionChainSnapshots.Add(Row("PE", strike, putLtp, captured, expiry));
        }
        db.Instruments.Add(new Instrument { Symbol = "MCX:CRUDEOIL26SEPFUT", Exchange = "MCX", InstrumentType = "FUT", Underlying = "CRUDEOIL", ExpiryDate = expiry.AddDays(4) });
        db.LiveQuotesLatest.Add(new LiveQuoteLatest
        {
            Symbol = "MCX:CRUDEOIL26SEPFUT", LastTradedPrice = 10060m, UpdatedUtc = DateTime.UtcNow.AddSeconds(-1),
            SourceKey = "dhan", RawPayload = "{\"type\":\"full\",\"close\":9527.0}",
        });
        db.LiveQuotesLatest.Add(new LiveQuoteLatest
        {
            Symbol = Symbol("CE", 10000m), LastTradedPrice = 300m, OpenInterest = 7_500, Volume = 90_000,
            UpdatedUtc = DateTime.UtcNow.AddSeconds(-2), SourceKey = "dhan",
        });
        await db.SaveChangesAsync();

        var service = new OptionChainService(db, new Sessions(open: true), new Lots(100));

        var live = await service.GetViewAsync("CRUDEOIL", null, null);
        var header = live.Header!;

        Assert.Equal("live", header.Mode);
        Assert.True(header.MarketOpen);
        Assert.Equal("MCX", header.Exchange);
        Assert.True(header.SpotIsFuture);
        Assert.Null(header.Future);
        Assert.Equal(10060m, header.Spot!.LastPrice);
        Assert.Equal(533m, header.Spot.Change);
        Assert.Equal(1, header.LiveLegs);
        Assert.Equal(6, header.TotalLegs);
        Assert.Equal(100, header.LotSize);
        Assert.Equal(3, header.DaysToExpiry);
        // Read against the live spot: 10,060 lies between 10,000 and 10,100 and the ATM is 10,100.
        Assert.Equal(10000m, header.SpotBetweenLower);
        Assert.Equal(10100m, header.SpotBetweenUpper);
        Assert.Equal(10100m, header.AtTheMoneyStrike);
        var overlaid = live.Strikes.Single(s => s.StrikePrice == 10000m).Call!;
        Assert.True(overlaid.IsLive);
        Assert.Equal(300m, overlaid.LastTradedPrice);
        Assert.Equal(2_500, overlaid.OpenInterestChange);

        var replay = await service.GetViewAsync("CRUDEOIL", null, captured);

        Assert.Equal("replay", replay.Header!.Mode);
        Assert.False(replay.Header.MarketOpen);
        Assert.Equal(0, replay.Header.LiveLegs);
        Assert.Equal(9577m, replay.Header.Spot!.LastPrice);
        Assert.Equal("snapshot", replay.Header.Spot.Basis);
        Assert.Equal(270m, replay.Strikes.Single(s => s.StrikePrice == 10000m).Call!.LastTradedPrice);
    }

    // ------------------------------------------------------------- helpers

    private static OptionChainLegResponse Leg(string symbol, decimal ltp, decimal change, long oi, long baseline) => new()
    {
        Symbol = symbol,
        LastTradedPrice = ltp,
        PriceChange = change,
        OpenInterest = oi,
        OpenInterestBaseline = baseline,
        OpenInterestChange = oi - baseline,
    };

    private static LiveQuoteSample Quote(
        string symbol, decimal ltp, long? oi, DateTime updated,
        long? volume = null, decimal? bid = null, decimal? ask = null,
        decimal? previousClose = null, decimal? packetClose = null)
        => new(symbol, ltp, bid, ask, volume, oi, previousClose, packetClose, updated, "dhan");

    private static OptionChainResponse Chain(decimal spot, params (decimal Strike, long CallOi, long PutOi)[] strikes)
    {
        var chain = new OptionChainResponse { Underlying = "BANKNIFTY", SpotPrice = spot, ExpiryDate = new DateOnly(2026, 9, 29) };
        foreach (var (strike, callOi, putOi) in strikes)
        {
            chain.Strikes.Add(new OptionChainStrikeResponse
            {
                StrikePrice = strike,
                Call = Leg($"NSE:BANKNIFTY26SEP{strike:0}CE", 100m, 1m, callOi, callOi),
                Put = Leg($"NSE:BANKNIFTY26SEP{strike:0}PE", 100m, 1m, putOi, putOi),
            });
        }
        return chain;
    }

    private static string Symbol(string type, decimal strike) => $"MCX:CRUDEOIL26SEP{strike:0}{type}";

    private static OptionChainSnapshot Row(string type, decimal strike, decimal ltp, DateTime captured, DateOnly expiry) => new()
    {
        Underlying = "CRUDEOIL",
        ExpiryDate = expiry,
        StrikePrice = strike,
        OptionType = type,
        Symbol = Symbol(type, strike),
        CapturedUtc = captured,
        SpotPrice = 9577m,
        LastTradedPrice = ltp,
        PriceChange = 10m,
        Volume = 50_000,
        OpenInterest = 6_000,
        OpenInterestAtOpen = 5_000,
        SourceKey = "dhan",
    };

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"chain-view-{Guid.NewGuid():N}").Options);

    private sealed class Sessions(bool open) : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => open;
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
    }

    private sealed class Lots(int size) : ILotSizeResolver
    {
        public Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default)
            => Task.FromResult(new LotSizeInfo(size, LotSizeInfo.SourceConfigured, "CRUDEOIL"));

        public Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default)
            => Task.FromResult(new LotSizeInfo(size, LotSizeInfo.SourceConfigured, underlying));
    }
}
