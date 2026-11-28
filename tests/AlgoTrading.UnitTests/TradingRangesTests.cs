using AlgoTrading.Infrastructure.Smc;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Trading ranges — the sideways boxes read as accumulation after a fall and
/// distribution after a rally. The candles are a hand-built schematic, one
/// five-minute bar each, read with a four-candle yardstick, a four-candle
/// minimum and a band two yardsticks tall, so every number can be checked by eye.
/// </summary>
public class TradingRangesTests
{
    private static readonly DateTime Start = new(2026, 9, 18, 3, 45, 0, DateTimeKind.Utc);

    private static List<StructureBar> Bars(params (decimal Open, decimal High, decimal Low, decimal Close)[] rows)
        => rows.Select((r, i) => new StructureBar(Start.AddMinutes(5 * i), r.Open, r.High, r.Low, r.Close)).ToList();

    /// <summary>
    /// Four falling candles 4 points tall (bars 0–3: the yardstick is 4, the band
    /// 8), a range from bar 4 that is four candles long at bar 7, a candle that
    /// widens it to 106 (bar 8), a wick to 96 that closes back inside (bar 9: a
    /// spring), a candle that still fits and lifts the top to 107 (bar 10), and a
    /// close at 111 that leaves it (bar 11).
    /// </summary>
    private static List<StructureBar> Schematic() => Bars(
        (110, 112, 108, 109),   // 0  yardstick
        (109, 110, 106, 107),   // 1
        (107, 108, 104, 105),   // 2
        (105, 106, 102, 103),   // 3
        (103, 104, 100, 101),   // 4  the range's first candle
        (101, 103, 99, 102),    // 5
        (102, 105, 100, 104),   // 6
        (104, 105, 101, 102),   // 7  four candles: a range
        (102, 106, 100, 105),   // 8  fits, widens the top to 106
        (105, 106, 96, 104),    // 9  10 points with the wick: a spring, closed inside
        (104, 107, 103, 106),   // 10 fits exactly: 107 - 99 = 8
        (106, 112, 105, 111));  // 11 closes above 107: the breakout

    private static StructureEvent Break(TrendDirection direction, int index) => new(
        StructureEventKind.Choch, direction, 100m, 0, Start, index, Start.AddMinutes(5 * index), 100m);

    private static IReadOnlyList<TradingRange> Read(List<StructureBar> bars, params StructureEvent[] events)
        => TradingRanges.Read(bars, events, minBars: 4, width: 2m, lookback: 4);

    [Fact]
    public void A_range_is_drawn_from_its_first_candle_and_known_once_it_is_long_enough()
    {
        var range = Assert.Single(Read(Schematic()));

        Assert.Equal(4, range.StartIndex);
        Assert.Equal(7, range.ConfirmedIndex);
        Assert.Equal(Start.AddMinutes(35), range.ConfirmedTimeUtc);
    }

    [Fact]
    public void Candles_that_still_fit_the_band_widen_the_box_and_a_probe_does_not()
    {
        var range = Assert.Single(Read(Schematic()));

        Assert.Equal(107m, range.Top);
        Assert.Equal(99m, range.Bottom);
    }

    [Fact]
    public void A_wick_out_that_closes_back_inside_is_a_spring()
    {
        var probe = Assert.Single(Assert.Single(Read(Schematic())).Probes);

        Assert.True(probe.Below);
        Assert.Equal(9, probe.Index);
        Assert.Equal(96m, probe.Price);
    }

    [Fact]
    public void A_close_outside_ends_the_range_and_says_which_way()
    {
        var range = Assert.Single(Read(Schematic()));

        Assert.Equal(11, range.EndIndex);
        Assert.Equal(TrendDirection.Bullish, range.Breakout);
    }

    [Fact]
    public void A_range_still_standing_at_the_last_candle_has_no_end()
    {
        var range = Assert.Single(Read(Schematic().Take(11).ToList()));

        Assert.Null(range.EndIndex);
        Assert.Null(range.EndTimeUtc);
        Assert.Equal(TrendDirection.None, range.Breakout);
    }

    [Fact]
    public void A_wick_above_that_closes_back_inside_is_an_upthrust()
    {
        var bars = Schematic().Take(11).ToList();
        bars.Add(new StructureBar(Start.AddMinutes(55), 106, 110, 104, 105));   // 110 - 99 = 11: out, closed inside

        var probe = Assert.Single(Read(bars)).Probes[^1];

        Assert.False(probe.Below);
        Assert.Equal(11, probe.Index);
        Assert.Equal(110m, probe.Price);
    }

    [Fact]
    public void A_range_in_a_bearish_structure_is_accumulation()
        => Assert.Equal(RangeKind.Accumulation, Assert.Single(Read(Schematic(), Break(TrendDirection.Bearish, 2))).Kind);

    [Fact]
    public void A_range_in_a_bullish_structure_is_distribution()
        => Assert.Equal(RangeKind.Distribution, Assert.Single(Read(Schematic(), Break(TrendDirection.Bullish, 2))).Kind);

    [Fact]
    public void The_kind_comes_from_the_last_break_before_the_range_began()
    {
        var ranges = Read(Schematic(), Break(TrendDirection.Bullish, 1), Break(TrendDirection.Bearish, 3), Break(TrendDirection.Bullish, 4));

        Assert.Equal(RangeKind.Accumulation, Assert.Single(ranges).Kind);
    }

    [Fact]
    public void With_no_break_before_it_a_range_is_only_a_range()
        => Assert.Equal(RangeKind.Range, Assert.Single(Read(Schematic())).Kind);

    [Fact]
    public void A_steady_trend_is_not_a_range()
    {
        var bars = Bars(Enumerable.Range(0, 20)
            .Select(i => (100m + 5 * i, 104m + 5 * i, 100m + 5 * i, 103m + 5 * i))
            .ToArray());

        Assert.Empty(Read(bars));
    }

    [Fact]
    public void The_next_range_is_looked_for_after_the_breakout()
    {
        var bars = Schematic();
        // Four quiet candles just above the breakout: a second range starting at bar 12.
        for (var i = 0; i < 4; i++)
            bars.Add(new StructureBar(Start.AddMinutes(5 * (12 + i)), 111, 113, 110, 112));

        var ranges = Read(bars);

        Assert.Equal(2, ranges.Count);
        Assert.Equal(12, ranges[1].StartIndex);
        Assert.True(ranges[1].StartIndex > ranges[0].EndIndex);
    }
}
