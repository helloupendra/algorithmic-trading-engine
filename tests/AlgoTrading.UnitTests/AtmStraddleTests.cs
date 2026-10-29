using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Row = AlgoTrading.Infrastructure.Services.AtmStraddle.ChainRow;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The ATM straddle and IV history: which strike is the money, what a leg is
/// read at, what an IV rank means over the days there are, and which expiry a
/// day's chain is, now that later expiries are recorded beside the nearest.
/// </summary>
public class AtmStraddleTests
{
    private static readonly DateTime At = new(2026, 10, 29, 4, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------- the money

    [Fact]
    public void The_money_is_the_strike_nearest_the_spot()
    {
        var point = AtmStraddle.Point(Capture(24_530m, (24_500m, 120m, 110m), (24_550m, 95m, 135m), (24_600m, 70m, 160m)))!;

        Assert.Equal(24_550m, point.AtTheMoneyStrike);
        Assert.Equal(95m + 135m, point.StraddlePrice);
    }

    [Fact]
    public void A_spot_exactly_between_two_strikes_takes_the_lower_like_the_chain_page()
    {
        var capture = Capture(24_525m, (24_500m, 120m, 110m), (24_550m, 95m, 135m));

        var point = AtmStraddle.Point(capture)!;

        Assert.Equal(24_500m, point.AtTheMoneyStrike);
        Assert.Equal(OptionChainAnalytics.AtTheMoney(new[] { 24_500m, 24_550m }, 24_525m), point.AtTheMoneyStrike);
    }

    [Fact]
    public void A_missing_leg_at_the_money_leaves_the_minute_without_a_straddle_rather_than_borrowing_a_strike()
    {
        // The put at 24,550 never arrived. 24,500 has both legs, but it is 40
        // points from the spot against 10: another price, and a line that
        // switched to it would show a jump nobody traded.
        var rows = Capture(24_540m, (24_500m, 120m, 110m)).ToList();
        rows.Add(Leg(24_540m, 24_550m, "CE", ltp: 95m, iv: 13.0m));

        var point = AtmStraddle.Point(rows)!;

        Assert.Equal(24_550m, point.AtTheMoneyStrike);
        Assert.Equal(95m, point.CallPrice);
        Assert.Null(point.PutPrice);
        Assert.Null(point.PutPriceBasis);
        Assert.Null(point.StraddlePrice);
        // The IV is the call's alone, not an average with nothing.
        Assert.Equal(13.0m, point.AtTheMoneyIv);
    }

    [Fact]
    public void An_unpriced_leg_has_no_price_and_no_iv()
    {
        var rows = new[]
        {
            Leg(24_510m, 24_500m, "CE", ltp: 120m, bid: 119m, ask: 121m, iv: 12.4m),
            Leg(24_510m, 24_500m, "PE", ltp: null, bid: 0m, ask: 0m, iv: 0m),
        };

        var point = AtmStraddle.Point(rows)!;

        Assert.Equal(120m, point.CallPrice);
        Assert.Null(point.PutPrice);
        Assert.Null(point.StraddlePrice);
        Assert.Equal(12.4m, point.AtTheMoneyIv);
    }

    [Fact]
    public void A_capture_without_a_spot_has_no_money_to_be_at()
    {
        Assert.Null(AtmStraddle.Point(Capture(0m, (24_500m, 120m, 110m))));
        Assert.Null(AtmStraddle.Point(Array.Empty<Row>()));
    }

    [Fact]
    public void The_atm_iv_is_the_mean_of_the_two_legs()
    {
        var rows = new[]
        {
            Leg(24_500m, 24_500m, "CE", ltp: 120m, iv: 12.0m),
            Leg(24_500m, 24_500m, "PE", ltp: 110m, iv: 13.0m),
        };

        Assert.Equal(12.5m, AtmStraddle.Point(rows)!.AtTheMoneyIv);
    }

    // ------------------------------------------------------------- what a leg is read at

    [Theory]
    [InlineData(101.0, 100.0, 102.0, 101.0, "mid")]   // both sides quoted: the mid, not the last trade
    [InlineData(101.0, 100.0, 100.0, 100.0, "mid")]   // a locked quote is still a mid
    [InlineData(101.0, 102.0, 100.0, 101.0, "ltp")]   // crossed: the touch is broken, the trade is not
    [InlineData(101.0, 0.0, 102.0, 101.0, "ltp")]     // no bid: no mid
    [InlineData(101.0, 100.0, null, 101.0, "ltp")]    // no ask: no mid
    public void A_leg_is_read_at_the_mid_when_both_sides_are_quoted_else_at_its_last_trade(
        double? last, double? bid, double? ask, double expected, string basis)
    {
        var (price, readAs) = AtmStraddle.LegPrice((decimal?)last, (decimal?)bid, (decimal?)ask);

        Assert.Equal((decimal)expected, price);
        Assert.Equal(basis, readAs);
    }

    [Fact]
    public void A_leg_with_neither_a_quote_nor_a_trade_has_no_price()
    {
        foreach (var (price, basis) in new[] { AtmStraddle.LegPrice(null, null, null), AtmStraddle.LegPrice(0m, 0m, 0m) })
        {
            Assert.Null(price);
            Assert.Null(basis);
        }
    }

    [Fact]
    public void The_points_say_which_price_each_leg_was_read_at()
    {
        var rows = new[]
        {
            Leg(24_500m, 24_500m, "CE", ltp: 121m, bid: 119m, ask: 120m),
            Leg(24_500m, 24_500m, "PE", ltp: 110m),
        };

        var point = AtmStraddle.Point(rows)!;

        Assert.Equal((119.5m, "mid"), (point.CallPrice!.Value, point.CallPriceBasis));
        Assert.Equal((110m, "ltp"), (point.PutPrice!.Value, point.PutPriceBasis));
        Assert.Equal(229.5m, point.StraddlePrice);
    }

    // ------------------------------------------------------------- a session

    [Fact]
    public void A_session_is_one_point_per_minute_oldest_first_and_the_later_capture_wins_a_shared_minute()
    {
        var nextMinute = At.AddMinutes(1);
        var rows = Capture(24_500m, At.AddMinutes(2), (24_500m, 100m, 100m))
            .Concat(Capture(24_500m, nextMinute, (24_500m, 110m, 110m)))
            // A second recorder in the same minute, thirty seconds later.
            .Concat(Capture(24_500m, nextMinute.AddSeconds(30), (24_500m, 112m, 112m)))
            .Concat(Capture(24_500m, At, (24_500m, 120m, 120m)))
            .ToList();

        var series = AtmStraddle.Series(rows);

        Assert.Equal(new[] { At, nextMinute.AddSeconds(30), At.AddMinutes(2) }, series.Select(p => p.CapturedUtc));
        Assert.Equal(new decimal?[] { 240m, 224m, 200m }, series.Select(p => p.StraddlePrice));
    }

    // ------------------------------------------------------------- IV rank and percentile

    [Fact]
    public void Rank_is_where_the_value_sits_in_the_range_and_percentile_the_share_of_days_below_it()
    {
        var standing = AtmStraddle.Standing(14m, new[] { 10m, 12m, 16m, 18m });

        // (14 − 10) / (18 − 10) = 50%; two of the four days closed below 14.
        Assert.Equal(50m, standing.Rank);
        Assert.Equal(50m, standing.Percentile);
        Assert.Equal(4, standing.Days);
        Assert.Equal((10m, 18m), (standing.Low!.Value, standing.High!.Value));
    }

    [Fact]
    public void A_new_extreme_ranks_at_the_end_of_the_range_rather_than_past_it()
    {
        Assert.Equal(100m, AtmStraddle.Standing(20m, new[] { 10m, 12m, 16m }).Rank);
        Assert.Equal(0m, AtmStraddle.Standing(9m, new[] { 10m, 12m, 16m }).Rank);
        Assert.Equal(0m, AtmStraddle.Standing(9m, new[] { 10m, 12m, 16m }).Percentile);
    }

    [Fact]
    public void A_day_equal_to_the_value_does_not_count_as_below_it()
    {
        Assert.Equal(25m, AtmStraddle.Standing(12m, new[] { 10m, 12m, 12m, 16m }).Percentile);
    }

    [Fact]
    public void With_no_earlier_days_there_is_no_standing_and_it_says_zero_days()
    {
        var standing = AtmStraddle.Standing(14m, Array.Empty<decimal>());

        Assert.Null(standing.Rank);
        Assert.Null(standing.Percentile);
        Assert.Equal(0, standing.Days);
    }

    [Fact]
    public void Days_that_span_no_range_give_no_rank_but_still_a_percentile()
    {
        var standing = AtmStraddle.Standing(14m, new[] { 14m, 14m });

        Assert.Null(standing.Rank);
        Assert.Equal(0m, standing.Percentile);
        Assert.Equal(2, standing.Days);
    }

    // ------------------------------------------------------------- which expiry a day is

    [Fact]
    public void A_days_candidates_are_the_expiries_not_behind_it_that_were_captured_around_it_nearest_first()
    {
        var spans = new[]
        {
            // Expired the day before: behind the day.
            Span(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 20)),
            // The monthly, recorded every five minutes since 21 Oct.
            Span(new DateOnly(2026, 11, 24), new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 29)),
            // The weekly the recorder writes every minute.
            Span(new DateOnly(2026, 10, 27), new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 27)),
            // Not captured until after the day.
            Span(new DateOnly(2026, 11, 03), new DateOnly(2026, 10, 28), new DateOnly(2026, 10, 29)),
        };

        Assert.Equal(
            new[] { new DateOnly(2026, 10, 27), new DateOnly(2026, 11, 24) },
            AtmStraddle.ExpiryCandidates(spans, new DateOnly(2026, 10, 23)));
        // On expiry day the expiring contract is still the nearest.
        Assert.Equal(new DateOnly(2026, 10, 27), AtmStraddle.ExpiryCandidates(spans, new DateOnly(2026, 10, 27)).First());
        Assert.Equal(new DateOnly(2026, 11, 03), AtmStraddle.ExpiryCandidates(spans, new DateOnly(2026, 10, 28)).First());
    }

    // ------------------------------------------------------------- the service end to end

    [Fact]
    public async Task A_days_straddle_is_read_from_its_nearest_expiry_never_a_later_one_recorded_beside_it()
    {
        await using var db = Db();
        var tuesday = new DateOnly(2026, 10, 27);
        var weekly = tuesday;
        var nextWeek = new DateOnly(2026, 11, 3);

        AddCapture(db, weekly, tuesday, 9, 20, spot: 24_510m, (24_500m, 40m, 30m, 13m, 14m));
        AddCapture(db, weekly, tuesday, 9, 21, spot: 24_520m, (24_500m, 45m, 25m, 13m, 14m));
        // Next week's contract, recorded every five minutes, at four times the premium.
        AddCapture(db, nextWeek, tuesday, 9, 20, spot: 24_510m, (24_500m, 160m, 150m, 12m, 12m));
        AddCapture(db, nextWeek, tuesday.AddDays(1), 9, 20, spot: 24_610m, (24_600m, 150m, 140m, 12m, 12m));
        await db.SaveChangesAsync();

        var service = Service(db, Ist(new DateOnly(2026, 10, 29), 12, 0));

        var expiryDay = await service.GetSeriesAsync("nifty", tuesday);
        Assert.Equal(weekly, expiryDay.ExpiryDate);
        Assert.Equal(0, expiryDay.DaysToExpiry);
        Assert.Equal(new decimal?[] { 70m, 70m }, expiryDay.Points.Select(p => p.StraddlePrice));
        Assert.False(expiryDay.IsToday);
        Assert.False(expiryDay.MarketOpen);

        // The day after, the weekly has gone and next week's is the nearest.
        var wednesday = await service.GetSeriesAsync("NIFTY", tuesday.AddDays(1));
        Assert.Equal(nextWeek, wednesday.ExpiryDate);
        Assert.Equal(290m, Assert.Single(wednesday.Points).StraddlePrice);

        // A closed session is read once: a row written into it later is not read again.
        AddCapture(db, weekly, tuesday, 9, 22, spot: 24_520m, (24_500m, 45m, 25m, 13m, 14m));
        await db.SaveChangesAsync();
        Assert.Equal(2, (await service.GetSeriesAsync("NIFTY", tuesday)).Points.Count);
    }

    [Fact]
    public async Task Today_is_read_incrementally_and_comes_out_the_same_as_a_whole_read()
    {
        await using var db = Db();
        var today = new DateOnly(2026, 10, 29);
        var expiry = new DateOnly(2026, 11, 3);
        for (int minute = 15; minute <= 17; minute++)
            AddCapture(db, expiry, today, 9, minute, spot: 24_510m, (24_500m, 100m + minute, 90m, 13m, 14m));
        await db.SaveChangesAsync();

        var clock = new Clock(Ist(today, 9, 18));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new StraddleService(db, cache, time: clock);

        var first = await service.GetSeriesAsync("NIFTY", null);
        Assert.True(first.IsToday);
        Assert.Equal(today, first.Date);
        Assert.Equal(3, first.Points.Count);

        // Two new minutes, and one capture that was stamped 09:16:40 but
        // committed after the last read: it belongs behind the newest point.
        AddCapture(db, expiry, today, 9, 18, spot: 24_515m, (24_500m, 118m, 90m, 13m, 14m));
        AddCapture(db, expiry, today, 9, 19, spot: 24_515m, (24_500m, 119m, 90m, 13m, 14m));
        AddCapture(db, expiry, today, 9, 16, spot: 24_515m, (24_500m, 200m, 90m, 13m, 14m), second: 40);
        await db.SaveChangesAsync();
        clock.Now = Ist(today, 9, 20);

        var second = await service.GetSeriesAsync("NIFTY", null);
        var whole = await new StraddleService(db, new MemoryCache(new MemoryCacheOptions()), time: clock).GetSeriesAsync("NIFTY", null);

        Assert.Equal(5, second.Points.Count);
        Assert.Equal(whole.Points.Select(p => (p.CapturedUtc, p.StraddlePrice)), second.Points.Select(p => (p.CapturedUtc, p.StraddlePrice)));
        Assert.Equal(290m, second.Points[1].StraddlePrice);
        Assert.Equal(Ist(today, 9, 19), second.LastCapturedUtc);
    }

    [Fact]
    public async Task Iv_history_reads_near_the_open_and_close_in_percent_and_ranks_over_the_days_there_are()
    {
        await using var db = Db();
        var expiry = new DateOnly(2026, 11, 3);
        var monday = new DateOnly(2026, 10, 26);

        // Monday from the FYERS poller (IV as a fraction), the rest from Dhan (IV in percent).
        foreach (var (day, source, openIv, closeIv) in new[]
                 {
                     (monday, "fyers", 0.13m, 0.12m),
                     (monday.AddDays(1), "dhan", 15m, 14m),
                     (monday.AddDays(2), "dhan", 17m, 16m),
                 })
        {
            // The opening minutes and the last ones are noisier, and must not be the readings.
            AddCapture(db, expiry, day, 9, 15, spot: 24_510m, (24_500m, 100m, 90m, openIv * 2, openIv * 2), source);
            AddCapture(db, expiry, day, 9, 20, spot: 24_510m, (24_500m, 100m, 90m, openIv, openIv), source);
            AddCapture(db, expiry, day, 15, 25, spot: 24_510m, (24_500m, 80m, 70m, closeIv, closeIv), source);
            AddCapture(db, expiry, day, 15, 29, spot: 24_510m, (24_500m, 80m, 70m, closeIv * 2, closeIv * 2), source);
        }
        await db.SaveChangesAsync();

        // Thursday before the open: today has nothing yet, so the newest close is ranked.
        var thursday = monday.AddDays(3);
        var history = await Service(db, Ist(thursday, 8, 0)).GetIvHistoryAsync("NIFTY");

        Assert.Equal(new[] { monday, monday.AddDays(1), monday.AddDays(2) }, history.Days.Select(d => d.Date));
        var first = history.Days[0];
        Assert.Equal((13m, 12m), (first.OpenIv!.Value, first.CloseIv!.Value));
        Assert.Equal(Ist(monday, 9, 20), first.OpenCapturedUtc);
        Assert.Equal(Ist(monday, 15, 25), first.CloseCapturedUtc);
        Assert.Equal(8, first.DaysToExpiry);
        Assert.Equal("fyers", first.Source);

        Assert.Equal("close", history.CurrentBasis);
        Assert.Equal(16m, history.CurrentIv);
        Assert.Equal(2, history.DaysUsed);
        Assert.Equal(100m, history.IvRank);
        Assert.Equal(100m, history.IvPercentile);

        // Thursday at 11:00, recorded since the open: today's newest is ranked
        // against every close before it, and today has no close yet.
        AddCapture(db, expiry, thursday, 9, 20, spot: 24_510m, (24_500m, 100m, 90m, 15m, 15m));
        AddCapture(db, expiry, thursday, 10, 59, spot: 24_510m, (24_500m, 95m, 85m, 13m, 13m));
        await db.SaveChangesAsync();

        var live = await Service(db, Ist(thursday, 11, 0)).GetIvHistoryAsync("NIFTY");

        Assert.Equal("latest", live.CurrentBasis);
        Assert.Equal(13m, live.CurrentIv);
        Assert.Equal(Ist(thursday, 10, 59), live.CurrentCapturedUtc);
        Assert.Equal(3, live.DaysUsed);
        Assert.Equal(25m, live.IvRank);          // (13 − 12) / (16 − 12)
        Assert.Equal(33.33m, live.IvPercentile); // one of three closes below 13
        var todayRow = live.Days[^1];
        Assert.Equal((thursday, 15m), (todayRow.Date, todayRow.OpenIv!.Value));
        Assert.Null(todayRow.CloseIv);
    }

    // ------------------------------------------------------------- helpers

    private static Row Leg(decimal spot, decimal strike, string type, decimal? ltp = null, decimal? bid = null, decimal? ask = null, decimal? iv = null, DateTime? at = null)
        => new(at ?? At, spot, strike, type, ltp, bid, ask, iv, "dhan");

    private static Row[] Capture(decimal spot, params (decimal Strike, decimal Call, decimal Put)[] strikes)
        => Capture(spot, At, strikes);

    private static Row[] Capture(decimal spot, DateTime at, params (decimal Strike, decimal Call, decimal Put)[] strikes)
        => strikes.SelectMany(s => new[]
        {
            Leg(spot, s.Strike, "CE", ltp: s.Call, iv: 12m, at: at),
            Leg(spot, s.Strike, "PE", ltp: s.Put, iv: 13m, at: at),
        }).ToArray();

    private static AtmStraddle.ExpirySpan Span(DateOnly expiry, DateOnly first, DateOnly last)
        => new(expiry, Ist(first, 9, 15), Ist(last, 15, 29));

    private static DateTime Ist(DateOnly day, int hour, int minute, int second = 0)
        => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute, second)));

    /// <summary>One capture of a chain: each strike's call and put at their last trade, with their IVs.</summary>
    private static void AddCapture(
        TradingDbContext db, DateOnly expiry, DateOnly day, int hour, int minute, decimal spot,
        (decimal Strike, decimal Call, decimal Put, decimal CallIv, decimal PutIv) strike,
        string source = "dhan", int second = 0)
    {
        var at = Ist(day, hour, minute, second);
        // A far strike outside the band read, which must never be the money.
        foreach (var (k, type, ltp, iv) in new[]
                 {
                     (strike.Strike, "CE", strike.Call, strike.CallIv),
                     (strike.Strike, "PE", strike.Put, strike.PutIv),
                     (strike.Strike + 2_000m, "CE", 1m, 30m),
                 })
        {
            db.OptionChainSnapshots.Add(new OptionChainSnapshot
            {
                Underlying = "NIFTY",
                ExpiryDate = expiry,
                StrikePrice = k,
                OptionType = type,
                Symbol = $"NSE:NIFTY{expiry:yyMMdd}{k:0}{type}",
                CapturedUtc = at,
                SpotPrice = spot,
                LastTradedPrice = ltp,
                ImpliedVolatility = iv,
                SourceKey = source,
            });
        }
    }

    private static StraddleService Service(TradingDbContext db, DateTime nowUtc)
        => new(db, new MemoryCache(new MemoryCacheOptions()), time: new Clock(nowUtc));

    private static TradingDbContext Db() => new(new DbContextOptionsBuilder<TradingDbContext>()
        .UseInMemoryDatabase($"straddle-{Guid.NewGuid():N}").Options);

    private sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }
}
