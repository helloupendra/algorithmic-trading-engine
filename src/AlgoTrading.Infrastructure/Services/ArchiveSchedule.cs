using System;
using System.Collections.Generic;
using System.Globalization;
using AlgoTrading.Application.Interfaces;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// When the daily candle archive is due. Pure, so the catch-up rule can be
/// tested without a clock or a database.
/// </summary>
public static class ArchiveSchedule
{
    public static readonly TimeSpan DefaultRunAtIst = new(23, 50, 0);
    public const int MaxCatchUpDays = 7;

    /// <summary>
    /// How long after the day's last exchange close the archive waits, so the
    /// last minute's live bar is written before it is read. The default 23:50
    /// has always given MCX's 23:30 close these twenty minutes.
    /// </summary>
    public static readonly TimeSpan AfterLastClose = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The sessions the archive covers: NSE and BSE close at 15:30 (or a special
    /// session's own close); MCX trades into the night, to 23:30, or to 23:55
    /// while New York is on standard time.
    /// </summary>
    private static readonly (string Exchange, string Segment)[] Sessions = [("NSE", "CM"), ("BSE", "CM"), ("MCX", "COM")];

    // A day is due by its own end plus AfterLastClose, so the latest due day is
    // never more than two days back; the search stops there.
    private const int LatestDueSearchDays = 3;

    private static readonly TimeSpan[] RetryWaits = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    /// <summary>
    /// How long a day whose archive failed waits before it is tried again: 5
    /// minutes after the first failure, 15 after the second, then an hour.
    /// Before, a failing day was tried every minute, and when its live_bars
    /// query was what timed out, that heavy query ran every minute on a small
    /// server.
    /// </summary>
    public static TimeSpan RetryAfter(int failures) => RetryWaits[Math.Clamp(failures, 1, RetryWaits.Length) - 1];

    public static TimeSpan ParseRunAt(string? value)
        => TimeSpan.TryParseExact(value ?? "", @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : DefaultRunAtIst;

    /// <summary>
    /// When a day's archive is due, in IST wall-clock time: <paramref name="runAt"/>
    /// on the day, or <see cref="AfterLastClose"/> after the last of its exchange
    /// sessions closes, whichever is later. The closes come from the session
    /// service, so holidays, MCX's half-day closures and its daylight-saving
    /// close are already in them.
    /// </summary>
    /// <remarks>
    /// From November to March MCX closes at 23:55, and the archive used to run at
    /// 23:50 regardless: MCX's last five minutes then stayed in <c>live_bars</c> and
    /// never reached <c>candles</c>, because the next night archives the next day.
    /// Such a day is now due at 00:15 the next morning.
    /// </remarks>
    public static DateTime DueAtIst(DateOnly day, TimeSpan runAt, IMarketSessionService sessions)
    {
        var due = day.ToDateTime(TimeOnly.MinValue).Add(runAt);
        var midday = IstTime.MiddayUtc(day);
        foreach (var (exchange, segment) in Sessions)
        {
            var session = sessions.GetSessionInfo(midday, exchange, segment);
            if (!session.IsTradingDay) continue;

            var settled = IstTime.ToIst(session.SessionCloseUtc).Add(AfterLastClose);
            if (settled > due) due = settled;
        }

        return due;
    }

    /// <summary>
    /// The IST days that are due: from the day after the last archived one
    /// (or just the latest due day, when nothing was ever archived) up to today
    /// once the clock has passed the run time — capped so a long outage does
    /// not turn into a week-long query at startup.
    /// </summary>
    public static IReadOnlyList<DateOnly> DueDays(DateOnly? lastArchived, DateTime nowIst, TimeSpan runAt, int maxDays = MaxCatchUpDays)
        => DueDays(lastArchived, nowIst, day => day.ToDateTime(TimeOnly.MinValue).Add(runAt), maxDays);

    /// <summary>
    /// The IST days that are due, each at its own time (<paramref name="dueAtIst"/>,
    /// usually <see cref="DueAtIst"/>), which may fall after midnight: at 00:05
    /// the day before is not due yet if its last session closed at 23:55.
    /// </summary>
    public static IReadOnlyList<DateOnly> DueDays(DateOnly? lastArchived, DateTime nowIst, Func<DateOnly, DateTime> dueAtIst, int maxDays = MaxCatchUpDays)
    {
        var latestDue = DateOnly.FromDateTime(nowIst);
        for (int back = 0; nowIst < dueAtIst(latestDue); back++)
        {
            if (back >= LatestDueSearchDays) return [];
            latestDue = latestDue.AddDays(-1);
        }

        var first = lastArchived is { } last ? last.AddDays(1) : latestDue;
        var floor = latestDue.AddDays(-(maxDays - 1));
        if (first < floor) first = floor;

        var days = new List<DateOnly>();
        for (var d = first; d <= latestDue; d = d.AddDays(1)) days.Add(d);
        return days;
    }
}
