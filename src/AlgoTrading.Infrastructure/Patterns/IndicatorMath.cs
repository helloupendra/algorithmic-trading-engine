namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>Supertrend's state on one candle: which way it points and where its line is.</summary>
/// <param name="Through">On the candle that turned it, the band its close went through (this
/// candle's lower band when it turned down, upper when up); null on every other candle. Not
/// the previous candle's line: the bands tighten each candle, so the level actually crossed
/// can sit above (below) where the line stood a candle ago.</param>
public readonly record struct SupertrendPoint(bool Up, double Line, double? Through = null);

/// <summary>
/// The indicators the alerts compute, as series: element <c>i</c> is the value
/// on candle <c>i</c> from candles <c>0..i</c> only, null until it is defined.
/// </summary>
/// <remarks>
/// <para>
/// Ported line for line from the strategies (<c>strategies/indicators.py</c> in
/// the Python engine: <c>ema</c>, <c>rsi</c>, <c>supertrend</c>, <c>vwap</c>), the
/// same way the candle patterns reuse the strategies' thresholds: an alert and
/// a strategy must never disagree about where RSI is. The Python functions
/// return the value at the last bar and are called on a growing prefix; a
/// series computed forward once gives the same number at every candle,
/// because every step reads only what came before it. The unit tests hold the
/// two to each other on the same bars.
/// </para>
/// <para>
/// In doubles, as the Python is: the numbers are compared with thresholds and
/// shown rounded, never summed into money.
/// </para>
/// </remarks>
public static class IndicatorMath
{
    /// <summary>EMA seeded with the SMA of the first <paramref name="period"/> values (defined from index period − 1).</summary>
    public static double?[] Ema(IReadOnlyList<double> values, int period)
    {
        var result = new double?[values.Count];
        if (period < 1 || values.Count < period) return result;

        double multiplier = 2.0 / (period + 1.0);
        double value = 0;
        for (int i = 0; i < period; i++) value += values[i];
        value /= period;
        result[period - 1] = value;

        for (int i = period; i < values.Count; i++)
        {
            value = values[i] * multiplier + value * (1.0 - multiplier);
            result[i] = value;
        }

        return result;
    }

    /// <summary>Wilder's RSI (defined from index <paramref name="period"/>).</summary>
    public static double?[] Rsi(IReadOnlyList<double> values, int period)
    {
        var result = new double?[values.Count];
        if (period < 1 || values.Count < period + 1) return result;

        double gains = 0, losses = 0;
        for (int i = 1; i <= period; i++)
        {
            double change = values[i] - values[i - 1];
            gains += Math.Max(change, 0.0);
            losses += Math.Max(-change, 0.0);
        }

        double avgGain = gains / period, avgLoss = losses / period;
        result[period] = RsiOf(avgGain, avgLoss);

        for (int i = period + 1; i < values.Count; i++)
        {
            double change = values[i] - values[i - 1];
            avgGain = (avgGain * (period - 1) + Math.Max(change, 0.0)) / period;
            avgLoss = (avgLoss * (period - 1) + Math.Max(-change, 0.0)) / period;
            result[i] = RsiOf(avgGain, avgLoss);
        }

        return result;
    }

    private static double RsiOf(double avgGain, double avgLoss)
    {
        // A window with no losses is 100 if it rose and 50 if it did not move:
        // the strategies' answer, and not a division by zero.
        if (avgLoss == 0) return avgGain > 0 ? 100.0 : 50.0;
        double rs = avgGain / avgLoss;
        return 100.0 - 100.0 / (1.0 + rs);
    }

    /// <summary>The classic true range: the candle's span, widened by any gap from the previous close.</summary>
    public static double TrueRange(TimeframeBar bar, TimeframeBar? previous)
    {
        double high = (double)bar.High, low = (double)bar.Low;
        if (previous is null) return high - low;
        double prevClose = (double)previous.Close;
        return Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
    }

    /// <summary>
    /// Supertrend on Wilder's ATR (defined from index <paramref name="period"/> + 1).
    /// The bands are the median price ± <paramref name="multiplier"/> ATRs; each
    /// only tightens while the trend holds, and a close through the active band
    /// turns it. The line is the lower band while it points up, the upper while down.
    /// </summary>
    public static SupertrendPoint?[] Supertrend(IReadOnlyList<TimeframeBar> bars, int period, double multiplier)
    {
        var result = new SupertrendPoint?[bars.Count];
        if (period < 1 || bars.Count < period + 2) return result;

        // ranges[k] is the true range of bar k + 1.
        var ranges = new double[bars.Count - 1];
        for (int k = 1; k < bars.Count; k++) ranges[k - 1] = TrueRange(bars[k], bars[k - 1]);

        double atr = 0;
        for (int k = 0; k < period; k++) atr += ranges[k];
        atr /= period;

        // Starts pointing up, as the strategies' does. The first answer is given
        // one candle after the seed, and the warm-up the alerts insist on (see
        // IndicatorRule.SettleCandles) is long enough for a wrong start to have
        // been turned by the market long before an alert reads it.
        bool up = true;
        double? upper = null, lower = null;
        for (int index = period; index < bars.Count; index++)
        {
            var bar = bars[index];
            if (index > period) atr = (atr * (period - 1) + ranges[index - 1]) / period;

            double median = ((double)bar.High + (double)bar.Low) / 2.0;
            double bandUp = median + multiplier * atr, bandDown = median - multiplier * atr;
            double close = (double)bar.Close, previousClose = (double)bars[index - 1].Close;

            upper = upper is null || bandUp < upper || previousClose > upper ? bandUp : upper;
            lower = lower is null || bandDown > lower || previousClose < lower ? bandDown : lower;

            double? through = null;
            if (up && close < lower)
            {
                through = lower;
                up = false;
                upper = bandUp;
            }
            else if (!up && close > upper)
            {
                through = upper;
                up = true;
                lower = bandDown;
            }

            if (index >= period + 1) result[index] = new SupertrendPoint(up, up ? lower!.Value : upper!.Value, through);
        }

        return result;
    }

    /// <summary>
    /// The session VWAP at the close of each candle: typical price
    /// ((high + low + close) / 3) weighted by volume, over the session's
    /// 1-minute bars up to that candle's end. Null for a candle before which
    /// nothing traded — the honest answer for an index, which has no volume,
    /// and the reason no VWAP is ever made up for one.
    /// </summary>
    /// <param name="minutes">The session's 1-minute bars, any order; minutes outside the session are ignored.</param>
    public static double?[] SessionVwap(IReadOnlyList<TimeframeBar> candles, IEnumerable<MinuteBar> minutes, SessionWindow session)
    {
        var result = new double?[candles.Count];
        var ordered = minutes
            .Where(m => m.StartUtc >= session.OpenUtc && m.StartUtc < session.CloseUtc)
            .GroupBy(m => m.StartUtc).Select(g => g.Last())
            .OrderBy(m => m.StartUtc)
            .ToList();

        double weighted = 0, volume = 0;
        int next = 0;
        for (int i = 0; i < candles.Count; i++)
        {
            // Candles from an earlier session have no VWAP of today's.
            if (candles[i].StartUtc < session.OpenUtc) continue;

            while (next < ordered.Count && ordered[next].StartUtc < candles[i].EndUtc)
            {
                var m = ordered[next++];
                if (m.Volume <= 0) continue;
                double typical = ((double)m.High + (double)m.Low + (double)m.Close) / 3.0;
                weighted += typical * m.Volume;
                volume += m.Volume;
            }

            result[i] = volume > 0 ? weighted / volume : null;
        }

        return result;
    }
}
