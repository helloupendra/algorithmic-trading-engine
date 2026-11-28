namespace AlgoTrading.Infrastructure.Smc;

/// <summary>What a trading range is read as, from the trend it formed in.</summary>
public enum RangeKind
{
    /// <summary>No structure trend was known when the range began.</summary>
    Range,

    /// <summary>The range formed in a bearish structure: the fall paused, and the range is where buyers may be absorbing it.</summary>
    Accumulation,

    /// <summary>The range formed in a bullish structure: the rally paused, and the range is where sellers may be absorbing it.</summary>
    Distribution,
}

/// <summary>
/// A wick that left a range and closed back inside it: a spring below the floor,
/// an upthrust above the ceiling. Price is the wick's extreme.
/// </summary>
public sealed record RangeProbe(bool Below, int Index, DateTime TimeUtc, decimal Price);

/// <summary>
/// A trading range: a run of candles whose whole extent stayed inside a band a
/// few average candles tall, until a candle closed outside it.
/// </summary>
public sealed record TradingRange(
    RangeKind Kind,
    decimal Top,
    decimal Bottom,
    /// <summary>The range's first candle. The box is drawn from here.</summary>
    int StartIndex,
    DateTime StartTimeUtc,
    /// <summary>The candle that made the run long enough to be a range — the first that could know it.</summary>
    int ConfirmedIndex,
    DateTime ConfirmedTimeUtc,
    /// <summary>The candle that closed outside the range. Null while it still stands.</summary>
    int? EndIndex,
    DateTime? EndTimeUtc,
    /// <summary>Which way that candle left: <see cref="TrendDirection.Bullish"/> up, <see cref="TrendDirection.Bearish"/> down, None while it stands.</summary>
    TrendDirection Breakout,
    IReadOnlyList<RangeProbe> Probes);

/// <summary>
/// Finds trading ranges — the sideways boxes that Wyckoff reads as accumulation
/// after a fall and distribution after a rally. Pure: no clock, no database, no I/O.
/// </summary>
/// <remarks>
/// <para>
/// Like the rest of SMC, accumulation is taught, not specified. This is one
/// reading, stated exactly so a chart can be checked against it:
/// </para>
/// <list type="number">
/// <item><b>Yardstick.</b> The average candle height (high minus low) over the
/// <c>lookback</c> candles before the range's first candle. High minus low
/// rather than true range, so an overnight gap does not inflate it.</item>
/// <item><b>Range.</b> A run of consecutive candles whose highest high and lowest
/// low stay within <c>width</c> yardsticks of each other. It becomes a range at
/// the candle that makes it <c>minBars</c> long; until then a candle that does
/// not fit simply means there was no range there.</item>
/// <item><b>Growing.</b> Once a range stands, every candle that still fits the
/// band widens the box to take it in — the box is the whole run.</item>
/// <item><b>Probe.</b> A candle that would stretch the box past the band but
/// closes back inside it is a spring (wick below) or an upthrust (wick above).
/// It does not widen the box.</item>
/// <item><b>Breakout.</b> A candle that would stretch the box past the band and
/// closes outside it ends the range. The next search starts after it.</item>
/// <item><b>Kind.</b> The structure trend when the range began — the direction of
/// the last break of structure or change of character before its first candle:
/// bearish makes it accumulation, bullish distribution, none a plain range.
/// Whether it then breaks the "right" way is reported, not assumed.</item>
/// </list>
/// <para>
/// A range can only be known once it is <c>minBars</c> long, so its box is drawn
/// back to a first candle the chart had already passed. That is what a box over
/// a range is; <see cref="TradingRange.ConfirmedIndex"/> says when it was known.
/// Ranges do not overlap. On intraday charts they may span the night: a quiet
/// close and a quiet open are the same range.
/// </para>
/// </remarks>
public static class TradingRanges
{
    public const int DefaultMinBars = 12;
    public const decimal DefaultWidth = 2.5m;
    public const int DefaultLookback = 14;

    public const int MinMinBars = 3;
    public const int MaxMinBars = 200;
    public const decimal MinWidth = 0.5m;
    public const decimal MaxWidth = 10m;

    public static IReadOnlyList<TradingRange> Read(
        IReadOnlyList<StructureBar> bars,
        IReadOnlyList<StructureEvent> events,
        int minBars = DefaultMinBars,
        decimal width = DefaultWidth,
        int lookback = DefaultLookback)
    {
        minBars = Math.Clamp(minBars, MinMinBars, MaxMinBars);
        width = Math.Clamp(width, MinWidth, MaxWidth);
        lookback = Math.Max(lookback, 1);

        var ranges = new List<TradingRange>();
        var start = lookback;
        while (start + minBars - 1 < bars.Count)
        {
            var range = TryRange(bars, events, start, minBars, width, lookback);
            if (range is null)
            {
                start++;
                continue;
            }

            ranges.Add(range);
            if (range.EndIndex is not { } end) break;   // still standing at the last candle
            start = end + 1;
        }

        return ranges;
    }

    /// <summary>The range that begins at <paramref name="start"/>, or null when the candles there do not make one.</summary>
    private static TradingRange? TryRange(
        IReadOnlyList<StructureBar> bars, IReadOnlyList<StructureEvent> events, int start, int minBars, decimal width,
        int lookback)
    {
        var yardstick = 0m;
        for (var i = start - lookback; i < start; i++) yardstick += bars[i].High - bars[i].Low;
        yardstick /= lookback;
        if (yardstick <= 0) return null;
        var band = width * yardstick;

        var top = bars[start].High;
        var bottom = bars[start].Low;
        if (top - bottom > band) return null;

        var confirmed = start + minBars - 1;
        for (var i = start + 1; i <= confirmed; i++)
        {
            top = Math.Max(top, bars[i].High);
            bottom = Math.Min(bottom, bars[i].Low);
            if (top - bottom > band) return null;
        }

        var probes = new List<RangeProbe>();
        int? endIndex = null;
        var breakout = TrendDirection.None;
        for (var i = confirmed + 1; i < bars.Count; i++)
        {
            var bar = bars[i];
            var newTop = Math.Max(top, bar.High);
            var newBottom = Math.Min(bottom, bar.Low);
            if (newTop - newBottom <= band)
            {
                top = newTop;
                bottom = newBottom;
                continue;
            }

            if (bar.Close > top || bar.Close < bottom)
            {
                endIndex = i;
                breakout = bar.Close > top ? TrendDirection.Bullish : TrendDirection.Bearish;
                break;
            }

            // A wick that does not fit, closing back inside. A candle can poke out of
            // both sides at once; each side is its own probe.
            if (bar.Low < bottom) probes.Add(new RangeProbe(true, i, bar.TimeUtc, bar.Low));
            if (bar.High > top) probes.Add(new RangeProbe(false, i, bar.TimeUtc, bar.High));
        }

        return new TradingRange(
            KindAt(events, start), top, bottom,
            start, bars[start].TimeUtc,
            confirmed, bars[confirmed].TimeUtc,
            endIndex, endIndex is { } e ? bars[e].TimeUtc : null,
            breakout, probes);
    }

    /// <summary>The structure trend before the range's first candle, as the last break before it left it.</summary>
    private static RangeKind KindAt(IReadOnlyList<StructureEvent> events, int start)
    {
        var trend = TrendDirection.None;
        foreach (var e in events)
        {
            if (e.BreakIndex >= start) break;
            trend = e.Direction;
        }

        return trend switch
        {
            TrendDirection.Bearish => RangeKind.Accumulation,
            TrendDirection.Bullish => RangeKind.Distribution,
            _ => RangeKind.Range,
        };
    }
}
