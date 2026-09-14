namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>One live 1-minute bar, as the aggregator needs it.</summary>
public readonly record struct MinuteBar(DateTime StartUtc, decimal Open, decimal High, decimal Low, decimal Close);

/// <summary>An exchange's trading window for one day, in UTC.</summary>
public readonly record struct SessionWindow(DateTime OpenUtc, DateTime CloseUtc);

/// <summary>
/// One candle of a coarser timeframe, built from the 1-minute bars inside it.
/// </summary>
/// <param name="Index">Position in the session: 0 is the candle that starts at the open.
/// Consecutive candles have consecutive indices, so a gap of a whole candle is visible.</param>
/// <param name="EndUtc">When the candle closes: its last minute's end, or the session close for the final, shorter candle.</param>
/// <param name="MinutesWithData">How many of its minutes had a live bar.</param>
/// <param name="MinutesExpected">How many minutes the candle spans (less than the timeframe only for the session's last candle).</param>
/// <param name="IsClosed">True once its last minute has closed; false for the candle still forming.</param>
public sealed record TimeframeBar(
    int TimeframeMinutes,
    int Index,
    DateTime StartUtc,
    DateTime EndUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    int MinutesWithData,
    int MinutesExpected,
    bool IsClosed)
{
    /// <summary>
    /// Minutes of the candle that have already started by <paramref name="asOfUtc"/>,
    /// capped at <see cref="MinutesExpected"/>. For a forming candle this is "7 of 15".
    /// </summary>
    public int MinutesElapsed(DateTime asOfUtc)
    {
        if (IsClosed) return MinutesExpected;
        var elapsed = (int)Math.Floor((asOfUtc - StartUtc).TotalMinutes);
        return Math.Clamp(elapsed, 0, MinutesExpected);
    }
}

/// <summary>
/// Rolls live 1-minute bars up into 3/5/15/30/60-minute candles aligned to the
/// exchange's session open.
/// </summary>
/// <remarks>
/// <para>
/// Anchored on the session, not on the UTC clock. <see cref="AlgoTrading.Infrastructure.Services.LiveBarRollup"/>
/// floors to UTC multiples, which happens to land on the open for 5 and 15
/// minutes but not for 30 or 60: an NSE hourly candle must run 09:15–10:15, and a
/// UTC hour boundary is 09:30 IST. MCX anchors on 09:00.
/// </para>
/// <para>
/// A candle is closed only when its last minute has closed, i.e. when the clock
/// has reached its end. A 1-minute bar is filed under the exchange's minute (see
/// LiveDataService), so "the minute has closed" is the wall clock passing the
/// minute's end; callers that want late ticks to land first pass an
/// <c>asOfUtc</c> a few seconds behind now.
/// </para>
/// <para>
/// A candle with missing minutes is still a candle, built from the minutes that
/// exist, and says how many it had. A candle with no minutes at all is absent,
/// which is what breaks a multi-candle pattern across a feed outage.
/// </para>
/// <para>
/// Minutes outside the session (NSE's 09:00–09:08 pre-open, anything after the
/// close) are ignored: they belong to no candle a chart would draw.
/// </para>
/// </remarks>
public static class SessionBarAggregator
{
    /// <summary>The timeframes the alerts offer, in minutes.</summary>
    public static readonly IReadOnlyList<int> SupportedTimeframes = [3, 5, 15, 30, 60];

    public static IReadOnlyList<TimeframeBar> Aggregate(
        IEnumerable<MinuteBar> minuteBars,
        SessionWindow session,
        int timeframeMinutes,
        DateTime asOfUtc)
    {
        if (timeframeMinutes < 1) throw new ArgumentOutOfRangeException(nameof(timeframeMinutes));
        if (session.CloseUtc <= session.OpenUtc) return [];

        var span = TimeSpan.FromMinutes(timeframeMinutes);

        return minuteBars
            .Where(b => b.StartUtc >= session.OpenUtc && b.StartUtc < session.CloseUtc)
            // One bar per minute; a duplicate stamp would otherwise count twice.
            .GroupBy(b => b.StartUtc)
            .Select(g => g.Last())
            .OrderBy(b => b.StartUtc)
            .GroupBy(b => (int)((b.StartUtc - session.OpenUtc).Ticks / span.Ticks))
            .Select(g =>
            {
                var start = session.OpenUtc + TimeSpan.FromTicks(span.Ticks * g.Key);
                var end = Min(start + span, session.CloseUtc);
                var minutes = g.ToList();
                return new TimeframeBar(
                    timeframeMinutes,
                    g.Key,
                    start,
                    end,
                    minutes[0].Open,
                    minutes.Max(b => b.High),
                    minutes.Min(b => b.Low),
                    minutes[^1].Close,
                    minutes.Count,
                    (int)Math.Round((end - start).TotalMinutes),
                    asOfUtc >= end);
            })
            .ToList();
    }

    /// <summary>
    /// The candle containing <paramref name="asOfUtc"/>'s bucket — start and end
    /// only, since it may have no minutes yet. Null outside the session.
    /// </summary>
    public static (int Index, DateTime StartUtc, DateTime EndUtc)? CurrentBucket(
        SessionWindow session, int timeframeMinutes, DateTime asOfUtc)
    {
        if (asOfUtc < session.OpenUtc || asOfUtc >= session.CloseUtc) return null;
        var span = TimeSpan.FromMinutes(timeframeMinutes);
        int index = (int)((asOfUtc - session.OpenUtc).Ticks / span.Ticks);
        var start = session.OpenUtc + TimeSpan.FromTicks(span.Ticks * index);
        return (index, start, Min(start + span, session.CloseUtc));
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
}
