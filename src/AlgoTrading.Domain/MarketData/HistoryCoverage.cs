using System;
using System.Collections.Generic;

namespace AlgoTrading.Domain.MarketData;

/// <summary>How complete one trading day's candles are.</summary>
public enum DayCoverage
{
    /// <summary>A trading day with no candles at all.</summary>
    Missing,

    /// <summary>A trading day with candles, but fewer than a full session.</summary>
    Partial,

    /// <summary>A full session.</summary>
    Complete,
}

/// <summary>
/// What a complete trading day looks like for an instrument.
/// </summary>
public enum CoveragePolicy
{
    /// <summary>
    /// An instrument that prints continuously — an index, a liquid future. It
    /// has a value at every moment of the session, so a day with a third of
    /// its bars is a day with a hole in it.
    /// </summary>
    Continuous,

    /// <summary>
    /// An instrument that prints only when it trades — an option. A deep
    /// out-of-the-money strike genuinely goes minutes or hours without a
    /// trade, so counting bars against a full session would mark every
    /// illiquid strike permanently incomplete and re-request it forever.
    /// Presence on the day is what can be checked; absence of a trade cannot
    /// be distinguished from absence of data.
    /// </summary>
    Sparse,
}

/// <summary>One run of consecutive dates to ask the broker about.</summary>
public readonly record struct CoverageGap(DateOnly From, DateOnly To, DayCoverage Kind)
{
    public override string ToString() => From == To
        ? $"{From:yyyy-MM-dd} ({Kind.ToString().ToLowerInvariant()})"
        : $"{From:yyyy-MM-dd} -> {To:yyyy-MM-dd} ({Kind.ToString().ToLowerInvariant()})";
}

/// <summary>
/// Whether the candles held locally actually cover a date range.
/// </summary>
/// <remarks>
/// Coverage used to mean "at least one candle exists between these dates". A
/// symbol holding one day out of twenty therefore reported full coverage, was
/// never completed, and every backtest over it ran across the hole without
/// saying so. A backtest that silently skips days is worse than one that
/// refuses to run: it still produces a number, and the number is believed.
/// <para>
/// The unit here is the trading day, and a day counts only when it holds close
/// to a full session. The intraday resolutions divide a fixed 09:15-15:30
/// session, so the expected bar count is arithmetic rather than a guess.
/// </para>
/// </remarks>
public static class HistoryCoverage
{
    /// <summary>NSE/BSE session length in minutes (09:15-15:30).</summary>
    public const int SessionMinutes = 375;

    /// <summary>
    /// A day is accepted slightly under a full session. Shortened sessions are
    /// real — a special trading window, a mid-session halt — and demanding the
    /// exact count would refetch those days forever.
    /// </summary>
    public const double CompleteFraction = 0.9;

    /// <summary>
    /// Bars a full session holds at this resolution, or null when the
    /// resolution is not something this can reason about.
    /// </summary>
    public static int? ExpectedBarsPerDay(string? resolution)
    {
        string code = (resolution ?? string.Empty).Trim();
        if (code.Length == 0) return null;

        if (code.Equals("D", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("1D", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        // Coarser than daily: a range may legitimately hold no bar at all.
        if (code.Equals("W", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("M", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!int.TryParse(code, out int minutes) || minutes <= 0) return null;

        // Ceiling: a 375-minute session at 60m is 7 bars, the last one short.
        return (int)Math.Ceiling(SessionMinutes / (double)minutes);
    }

    /// <summary>
    /// Whether a symbol prints continuously or only when it trades, from the
    /// symbol itself. An option contract ends in CE or PE.
    /// </summary>
    public static CoveragePolicy PolicyFor(string? symbol)
    {
        string s = (symbol ?? string.Empty).TrimEnd();
        return s.EndsWith("CE", StringComparison.OrdinalIgnoreCase) ||
               s.EndsWith("PE", StringComparison.OrdinalIgnoreCase)
            ? CoveragePolicy.Sparse
            : CoveragePolicy.Continuous;
    }

    /// <summary>How complete a single day is, given the bars stored for it.</summary>
    public static DayCoverage ClassifyDay(
        int barsPresent,
        int? expectedBarsPerDay,
        CoveragePolicy policy = CoveragePolicy.Continuous)
    {
        if (barsPresent <= 0) return DayCoverage.Missing;

        // A sparse instrument that traded at all that day is as complete as it
        // is ever going to be; there is no second source to fill the quiet
        // minutes from, because nothing happened in them.
        if (policy == CoveragePolicy.Sparse) return DayCoverage.Complete;

        if (expectedBarsPerDay is not > 0) return DayCoverage.Complete;

        return barsPresent >= expectedBarsPerDay.Value * CompleteFraction
            ? DayCoverage.Complete
            : DayCoverage.Partial;
    }

    /// <summary>
    /// Weekdays in the range. Weekends are the only closures knowable without
    /// asking the broker; a holiday looks exactly like a missing day until it
    /// has been asked for once and come back empty.
    /// </summary>
    /// <param name="lastCompletedSession">
    /// The most recent day whose session has finished. Days after it are not
    /// expected to hold anything yet, so they are neither reported as gaps nor
    /// requested from the broker. Null means "no limit" — for a range that
    /// ends in the past.
    /// </param>
    /// <remarks>
    /// Without this bound the range "…to today" asks the broker for a session
    /// that has not happened. FYERS answers that with "Something went wrong.
    /// Please contact support", which reads like a broken symbol and is not
    /// one — the same call for the same symbol succeeds the moment the end
    /// date is a day that has actually traded.
    /// </remarks>
    public static IEnumerable<DateOnly> ExpectedTradingDays(
        DateOnly from,
        DateOnly to,
        DateOnly? lastCompletedSession = null)
    {
        if (lastCompletedSession is { } last && to > last) to = last;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            yield return day;
        }
    }

    /// <summary>
    /// The days still needing a fetch, merged into consecutive runs so one
    /// broker call covers a stretch rather than one call per day.
    /// </summary>
    /// <param name="barsByDate">Bars held locally per date; absent means none.</param>
    /// <param name="knownEmptyDates">
    /// Dates already asked for that came back empty — a market holiday, or a
    /// contract that had not started trading yet. Without these, every holiday
    /// is re-requested on every run, forever.
    /// </param>
    public static IReadOnlyList<CoverageGap> FindGaps(
        DateOnly from,
        DateOnly to,
        string? resolution,
        IReadOnlyDictionary<DateOnly, int> barsByDate,
        ISet<DateOnly>? knownEmptyDates = null,
        CoveragePolicy policy = CoveragePolicy.Continuous,
        DateOnly? lastCompletedSession = null)
    {
        var gaps = new List<CoverageGap>();
        if (to < from) return gaps;

        int? expected = ExpectedBarsPerDay(resolution);

        foreach (var day in ExpectedTradingDays(from, to, lastCompletedSession))
        {
            if (knownEmptyDates is not null && knownEmptyDates.Contains(day)) continue;

            barsByDate.TryGetValue(day, out int bars);
            var kind = ClassifyDay(bars, expected, policy);
            if (kind == DayCoverage.Complete) continue;

            // Consecutive across a weekend still counts as consecutive: there
            // is nothing to fetch in between.
            if (gaps.Count > 0 && NextTradingDay(gaps[^1].To) == day)
            {
                var last = gaps[^1];
                gaps[^1] = last with
                {
                    To = day,
                    Kind = last.Kind == kind ? kind : DayCoverage.Missing,
                };
                continue;
            }

            gaps.Add(new CoverageGap(day, day, kind));
        }

        return gaps;
    }

    /// <summary>True when every expected trading day in the range is complete.</summary>
    public static bool IsFullyCovered(
        DateOnly from,
        DateOnly to,
        string? resolution,
        IReadOnlyDictionary<DateOnly, int> barsByDate,
        ISet<DateOnly>? knownEmptyDates = null,
        CoveragePolicy policy = CoveragePolicy.Continuous,
        DateOnly? lastCompletedSession = null)
        => FindGaps(from, to, resolution, barsByDate, knownEmptyDates, policy, lastCompletedSession).Count == 0;

    private static DateOnly NextTradingDay(DateOnly day)
    {
        var next = day.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return next;
    }
}
