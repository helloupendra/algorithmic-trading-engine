using AlgoTrading.Infrastructure.Patterns;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Every pattern on hand-computed candles: a positive case, the threshold itself
/// where the definition is inclusive, and a near miss just outside it.
/// </summary>
/// <remarks>
/// The engulfing, hammer, shooting-star and marubozu cases mirror the thresholds
/// in strategies/indicators.py (is_bullish_engulfing, is_bearish_engulfing,
/// is_hammer, is_shooting_star, is_marubozu). If one of these fails after a change
/// to either side, an alert and a strategy filter now disagree about a candle.
/// </remarks>
public class CandlePatternDetectorTests
{
    private static readonly DateTime Open = new(2026, 9, 15, 3, 45, 0, DateTimeKind.Utc); // 09:15 IST

    /// <summary>15-minute candles with consecutive indices, oldest first.</summary>
    private static List<TimeframeBar> Candles(params (decimal O, decimal H, decimal L, decimal C)[] ohlc)
        => ohlc.Select((x, i) => Candle(i, x.O, x.H, x.L, x.C)).ToList();

    private static TimeframeBar Candle(int index, decimal o, decimal h, decimal l, decimal c)
    {
        var start = Open.AddMinutes(15 * index);
        return new TimeframeBar(15, index, start, start.AddMinutes(15), o, h, l, c, 15, 15, true);
    }

    private static IReadOnlyList<CandlePattern> Last(List<TimeframeBar> bars)
        => CandlePatternDetector.Detect(bars, bars.Count - 1).Select(h => h.Pattern).ToList();

    private static PatternDirection DirectionOf(List<TimeframeBar> bars, CandlePattern pattern)
        => CandlePatternDetector.Detect(bars, bars.Count - 1).Single(h => h.Pattern == pattern).Direction;

    // ----------------------------------------------------------------- doji --

    [Fact]
    public void Doji_body_within_ten_percent_of_range()
    {
        // Range 10, body 0.5, both wicks long.
        var bars = Candles((100m, 105m, 95m, 100.5m));
        Assert.Contains(CandlePattern.Doji, Last(bars));
        Assert.Equal(PatternDirection.Neutral, DirectionOf(bars, CandlePattern.Doji));
    }

    [Fact]
    public void Doji_threshold_is_inclusive_and_a_hair_over_is_not()
    {
        Assert.Contains(CandlePattern.Doji, Last(Candles((100m, 105m, 95m, 101m))));      // body 1.0 = 10%
        Assert.DoesNotContain(CandlePattern.Doji, Last(Candles((100m, 105m, 95m, 101.1m)))); // body 1.1 = 11%
    }

    [Fact]
    public void A_candle_with_no_range_is_no_pattern_at_all()
    {
        Assert.Empty(Last(Candles((100m, 100m, 100m, 100m))));
    }

    [Fact]
    public void Dragonfly_doji_replaces_plain_doji_when_open_and_close_sit_at_the_high()
    {
        // Range 10, body 0.2, upper wick 0 → dragonfly. The shape also passes is_hammer.
        var hits = Last(Candles((104.8m, 105m, 95m, 105m)));
        Assert.Contains(CandlePattern.DragonflyDoji, hits);
        Assert.DoesNotContain(CandlePattern.Doji, hits);
        Assert.Contains(CandlePattern.Hammer, hits);
    }

    [Fact]
    public void Dragonfly_near_miss_upper_wick_eleven_percent_is_a_plain_doji()
    {
        // Upper wick 105 − 103.9 = 1.1 (11%), body 0.1.
        var hits = Last(Candles((103.9m, 105m, 95m, 103.8m)));
        Assert.Contains(CandlePattern.Doji, hits);
        Assert.DoesNotContain(CandlePattern.DragonflyDoji, hits);
    }

    [Fact]
    public void Gravestone_doji_when_open_and_close_sit_at_the_low()
    {
        var bars = Candles((95.2m, 105m, 95m, 95m));
        var hits = Last(bars);
        Assert.Contains(CandlePattern.GravestoneDoji, hits);
        Assert.DoesNotContain(CandlePattern.Doji, hits);
        Assert.Equal(PatternDirection.Bearish, DirectionOf(bars, CandlePattern.GravestoneDoji));
    }

    [Fact]
    public void Gravestone_near_miss_lower_wick_eleven_percent_is_a_plain_doji()
    {
        // Lower wick 96.1 − 95 = 1.1 (11%), body 0.1.
        var hits = Last(Candles((96.1m, 105m, 95m, 96.2m)));
        Assert.Contains(CandlePattern.Doji, hits);
        Assert.DoesNotContain(CandlePattern.GravestoneDoji, hits);
    }

    // --------------------------------------------------- hammer / shooting star --

    [Fact]
    public void Hammer_as_is_hammer_defines_it()
    {
        // Body 1, lower wick 3 (≥ 2 × body), upper wick 0.5 (≤ body).
        var hits = Last(Candles((100m, 101.5m, 97m, 101m)));
        Assert.Contains(CandlePattern.Hammer, hits);
        Assert.DoesNotContain(CandlePattern.Doji, hits);
    }

    [Fact]
    public void Hammer_lower_wick_exactly_twice_the_body_counts_and_just_under_does_not()
    {
        Assert.Contains(CandlePattern.Hammer, Last(Candles((100m, 101m, 98m, 101m))));      // wick 2.0
        Assert.DoesNotContain(CandlePattern.Hammer, Last(Candles((100m, 101m, 98.1m, 101m)))); // wick 1.9
    }

    [Fact]
    public void Hammer_near_miss_upper_wick_longer_than_the_body()
    {
        Assert.DoesNotContain(CandlePattern.Hammer, Last(Candles((100m, 102.1m, 97m, 101m)))); // upper 1.1 > body 1
    }

    [Fact]
    public void Hammer_needs_a_body_as_is_hammer_does()
    {
        // Body 0: a dragonfly, never a hammer (Python returns False when body <= 0).
        Assert.DoesNotContain(CandlePattern.Hammer, Last(Candles((105m, 105m, 95m, 105m))));
    }

    [Fact]
    public void Shooting_star_as_is_shooting_star_defines_it_and_its_near_miss()
    {
        Assert.Contains(CandlePattern.ShootingStar, Last(Candles((101m, 104m, 99.5m, 100m))));     // upper 3, lower 0.5
        Assert.DoesNotContain(CandlePattern.ShootingStar, Last(Candles((101m, 102.9m, 99.5m, 100m)))); // upper 1.9
        Assert.DoesNotContain(CandlePattern.ShootingStar, Last(Candles((101m, 104m, 98.9m, 100m))));   // lower 1.1 > body
    }

    // -------------------------------------------- trend context: hanging man, inverted hammer --

    private static readonly (decimal, decimal, decimal, decimal)[] Rising =
    [
        (99m, 100.5m, 98.5m, 100m), (100m, 102.5m, 99.5m, 102m), (102m, 104.5m, 101.5m, 104m),
        (104m, 106.5m, 103.5m, 106m), (106m, 108.5m, 105.5m, 108m),
    ]; // average close 104

    private static readonly (decimal, decimal, decimal, decimal)[] Falling =
    [
        (121m, 121.5m, 119.5m, 120m), (120m, 120.5m, 117.5m, 118m), (118m, 118.5m, 115.5m, 116m),
        (116m, 116.5m, 113.5m, 114m), (114m, 114.5m, 111.5m, 112m),
    ]; // average close 116

    [Fact]
    public void Hanging_man_is_the_hammer_shape_after_a_rise_and_is_reported_beside_the_hammer()
    {
        var bars = Candles([.. Rising, (110m, 111.5m, 107m, 111m)]); // close 111 > 104
        var hits = Last(bars);
        Assert.Contains(CandlePattern.Hammer, hits);
        Assert.Contains(CandlePattern.HangingMan, hits);
        Assert.Equal(PatternDirection.Bearish, DirectionOf(bars, CandlePattern.HangingMan));
        Assert.Equal(PatternDirection.Bullish, DirectionOf(bars, CandlePattern.Hammer));
    }

    [Fact]
    public void Hammer_shape_after_a_fall_is_not_a_hanging_man()
    {
        var hits = Last(Candles([.. Falling, (110m, 111.5m, 107m, 111m)])); // close 111 < 116
        Assert.Contains(CandlePattern.Hammer, hits);
        Assert.DoesNotContain(CandlePattern.HangingMan, hits);
    }

    [Fact]
    public void Trend_context_needs_five_candles_before_the_pattern()
    {
        var hits = Last(Candles([.. Rising[1..], (110m, 111.5m, 107m, 111m)])); // only four before
        Assert.Contains(CandlePattern.Hammer, hits);
        Assert.DoesNotContain(CandlePattern.HangingMan, hits);
    }

    [Fact]
    public void A_missing_candle_inside_the_lookback_breaks_the_trend_context()
    {
        var bars = Candles([.. Rising, (110m, 111.5m, 107m, 111m)]);
        // Re-index so candle 2 is missing: indices 0,1,3,4,5,6.
        var gapped = bars.Select((b, i) => i < 2 ? b : b with { Index = b.Index + 1 }).ToList();
        Assert.DoesNotContain(CandlePattern.HangingMan, Last(gapped));
    }

    [Fact]
    public void Inverted_hammer_is_the_shooting_star_shape_after_a_fall()
    {
        var bars = Candles([.. Falling, (101m, 104m, 99.5m, 100m)]); // close 100 < 116
        var hits = Last(bars);
        Assert.Contains(CandlePattern.InvertedHammer, hits);
        Assert.Contains(CandlePattern.ShootingStar, hits);
        Assert.Equal(PatternDirection.Bullish, DirectionOf(bars, CandlePattern.InvertedHammer));

        var afterRise = Last(Candles([.. Rising, (111m, 114m, 109.5m, 110m)])); // close 110 > 104
        Assert.Contains(CandlePattern.ShootingStar, afterRise);
        Assert.DoesNotContain(CandlePattern.InvertedHammer, afterRise);
    }

    // ------------------------------------------------------------ engulfing --

    [Fact]
    public void Bullish_engulfing_as_is_bullish_engulfing_defines_it()
    {
        // prev bearish 102→100; last bullish 99.8→102.5: close ≥ prev open, open ≤ prev close, body 2.7 > 2.
        var bars = Candles((102m, 102.5m, 99.5m, 100m), (99.8m, 102.8m, 99.6m, 102.5m));
        Assert.Contains(CandlePattern.BullishEngulfing, Last(bars));
    }

    [Fact]
    public void Bullish_engulfing_near_misses()
    {
        // Equal bodies: is_bullish_engulfing requires last.body > prev.body.
        Assert.DoesNotContain(CandlePattern.BullishEngulfing, Last(Candles((102m, 102.5m, 99.5m, 100m), (100m, 102.2m, 99.8m, 102m))));
        // Opens 0.1 above the previous close.
        Assert.DoesNotContain(CandlePattern.BullishEngulfing, Last(Candles((102m, 102.5m, 99.5m, 100m), (100.1m, 102.8m, 99.6m, 102.5m))));
        // Previous candle was not bearish.
        Assert.DoesNotContain(CandlePattern.BullishEngulfing, Last(Candles((100m, 102.5m, 99.5m, 102m), (99.8m, 102.8m, 99.6m, 102.5m))));
    }

    [Fact]
    public void Bearish_engulfing_as_is_bearish_engulfing_defines_it_and_its_near_miss()
    {
        var bars = Candles((100m, 102.5m, 99.5m, 102m), (102.2m, 102.4m, 99.3m, 99.5m));
        Assert.Contains(CandlePattern.BearishEngulfing, Last(bars));

        // Closes 0.1 above the previous open.
        Assert.DoesNotContain(CandlePattern.BearishEngulfing, Last(Candles((100m, 102.5m, 99.5m, 102m), (102.2m, 102.4m, 99.3m, 100.1m))));
    }

    [Fact]
    public void Engulfing_is_not_read_across_a_missing_candle()
    {
        var prev = Candle(0, 102m, 102.5m, 99.5m, 100m);
        var last = Candle(2, 99.8m, 102.8m, 99.6m, 102.5m); // candle 1 never arrived
        Assert.DoesNotContain(CandlePattern.BullishEngulfing, Last([prev, last]));
    }

    // ------------------------------------------------------------- marubozu --

    [Fact]
    public void Marubozu_body_eighty_percent_of_range_takes_the_candle_colour()
    {
        var up = Candles((100m, 109m, 99m, 108m)); // body 8 of 10
        Assert.Contains(CandlePattern.Marubozu, Last(up));
        Assert.Equal(PatternDirection.Bullish, DirectionOf(up, CandlePattern.Marubozu));

        var down = Candles((108m, 109m, 99m, 100m));
        Assert.Equal(PatternDirection.Bearish, DirectionOf(down, CandlePattern.Marubozu));

        Assert.DoesNotContain(CandlePattern.Marubozu, Last(Candles((100m, 109m, 99m, 107.9m)))); // 79%
    }

    // ----------------------------------------------------------- inside bar --

    [Fact]
    public void Inside_bar_within_the_previous_range()
    {
        Assert.Contains(CandlePattern.InsideBar, Last(Candles((100m, 110m, 100m, 108m), (104m, 108m, 102m, 105m))));
        // Sharing the previous high still counts while the range is smaller.
        Assert.Contains(CandlePattern.InsideBar, Last(Candles((100m, 110m, 100m, 108m), (104m, 110m, 101m, 105m))));
    }

    [Fact]
    public void Inside_bar_near_misses()
    {
        // Identical range.
        Assert.DoesNotContain(CandlePattern.InsideBar, Last(Candles((100m, 110m, 100m, 108m), (104m, 110m, 100m, 105m))));
        // High pokes 0.5 above.
        Assert.DoesNotContain(CandlePattern.InsideBar, Last(Candles((100m, 110m, 100m, 108m), (104m, 110.5m, 102m, 105m))));
        // One price all candle long: no range, no contraction.
        Assert.DoesNotContain(CandlePattern.InsideBar, Last(Candles((100m, 110m, 100m, 108m), (105m, 105m, 105m, 105m))));
    }

    // ------------------------------------------------------ morning / evening star --

    private static readonly (decimal, decimal, decimal, decimal) BigDown = (110m, 111m, 99m, 100m);  // body 10 of 12, mid 105
    private static readonly (decimal, decimal, decimal, decimal) StarLow = (99m, 100m, 98m, 99.5m);  // body 0.5, top 99.5

    [Fact]
    public void Morning_star()
    {
        var bars = Candles(BigDown, StarLow, (100m, 106.5m, 99.8m, 106m)); // bullish, closes 106 > 105
        Assert.Contains(CandlePattern.MorningStar, Last(bars));
        Assert.Equal(PatternDirection.Bullish, DirectionOf(bars, CandlePattern.MorningStar));
    }

    [Fact]
    public void Morning_star_near_misses()
    {
        // Third candle closes exactly at the first body's midpoint, not above it.
        Assert.DoesNotContain(CandlePattern.MorningStar, Last(Candles(BigDown, StarLow, (100m, 105.5m, 99.8m, 105m))));
        // The star's body is 3.1, more than 30% of the first body.
        Assert.DoesNotContain(CandlePattern.MorningStar, Last(Candles(BigDown, (99m, 102.5m, 98m, 102.1m), (100m, 106.5m, 99.8m, 106m))));
        // The first candle is not decisive: body 6 of a 16 range.
        Assert.DoesNotContain(CandlePattern.MorningStar, Last(Candles((110m, 115m, 99m, 104m), StarLow, (100m, 108.5m, 99.8m, 108m))));
        // The star sits above the first body's midpoint.
        Assert.DoesNotContain(CandlePattern.MorningStar, Last(Candles(BigDown, (105.5m, 106.5m, 105m, 106m), (100m, 108.5m, 99.8m, 108m))));
    }

    [Fact]
    public void Evening_star_and_its_near_miss()
    {
        var up = (100m, 111m, 99m, 110m);     // body 10 of 12, mid 105
        var star = (111m, 112m, 110m, 110.5m); // body 0.5, bottom 110.5
        Assert.Contains(CandlePattern.EveningStar, Last(Candles(up, star, (110m, 110.2m, 103.5m, 104m))));
        Assert.DoesNotContain(CandlePattern.EveningStar, Last(Candles(up, star, (110m, 110.2m, 104.5m, 105m)))); // closes at the midpoint
    }

    [Fact]
    public void Three_candle_patterns_are_not_read_across_a_missing_candle()
    {
        var bars = new List<TimeframeBar>
        {
            Candle(0, 110m, 111m, 99m, 100m),
            Candle(1, 99m, 100m, 98m, 99.5m),
            Candle(3, 100m, 106.5m, 99.8m, 106m),
        };
        Assert.DoesNotContain(CandlePattern.MorningStar, Last(bars));
    }

    [Fact]
    public void The_catalog_names_every_pattern_once_and_cites_the_five_python_ports()
    {
        Assert.Equal(Enum.GetValues<CandlePattern>().Length, CandlePatternCatalog.All.Count);
        Assert.Equal(CandlePatternCatalog.All.Count, CandlePatternCatalog.All.Select(p => p.Key).Distinct().Count());
        Assert.Equal(
            ["is_bearish_engulfing", "is_bullish_engulfing", "is_hammer", "is_marubozu", "is_shooting_star"],
            CandlePatternCatalog.All.Select(p => p.PortedFrom).OfType<string>().Order().ToArray());
    }
}
