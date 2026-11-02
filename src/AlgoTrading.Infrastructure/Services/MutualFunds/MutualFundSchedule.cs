using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>
/// When AMFI's NAVs are due and when the sync runs. Pure functions of the
/// clock and the business-day rule, so the schedule is tested without a host.
/// </summary>
/// <remarks>
/// AMFI publishes each business day's NAVs by about 23:00 IST. The sync runs
/// at 23:30 IST every day, weekends and holidays included: liquid and overnight
/// funds publish a NAV for every calendar day, and the daily file holds only
/// each scheme's latest one. The 07:00 IST retry runs only when the last sync
/// failed or did not reach the newest business day's NAVs. A business day is a
/// weekday the NSE calendar does not close, the closest public rule to AMFI's
/// own (AMFI publishes no holiday list).
/// </remarks>
public static class MutualFundSchedule
{
    public static readonly TimeSpan EveningAt = new(23, 30, 0);
    public static readonly TimeSpan MorningRetryAt = new(7, 0, 0);

    /// <summary>Within this many days of the newest NAV day, a scheme counts as active.</summary>
    public const int ActiveWithinDays = 30;

    /// <summary>
    /// The newest business day whose NAVs should be stored by <paramref name="nowUtc"/>:
    /// the last business day on or before the IST date 23:30 hours ago. At 07:00
    /// on a Tuesday that is Monday; at 23:00 on a Monday it is still Friday.
    /// </summary>
    public static DateOnly ExpectedNavDate(DateTime nowUtc, Func<DateOnly, bool> isBusinessDay)
    {
        var day = IstTime.DateOf(nowUtc - EveningAt);
        for (int guard = 0; guard < 30 && !isBusinessDay(day); guard++) day = day.AddDays(-1);
        return day;
    }

    /// <summary>Business days after <paramref name="latest"/> up to and including <paramref name="expected"/>: how far the stored NAVs are behind.</summary>
    public static int BusinessDaysBehind(DateOnly? latest, DateOnly expected, Func<DateOnly, bool> isBusinessDay)
    {
        if (latest is null) return int.MaxValue;
        int behind = 0;
        for (var day = latest.Value.AddDays(1); day <= expected && behind < 400; day = day.AddDays(1))
        {
            if (isBusinessDay(day)) behind++;
        }

        return behind;
    }

    /// <summary>
    /// The history backfill keeps out from here on an NSE trading day, ahead of the
    /// 08:45 morning job (master refresh, feeds, runners), as the news scoring and the
    /// desk's deploy gate do.
    /// </summary>
    public static readonly TimeSpan BackfillQuietFrom = new(8, 40, 0);

    /// <summary>
    /// Too little time for a request to be worth starting: AMFI takes about two minutes
    /// to begin answering, and a request cut short is repeated anyway.
    /// </summary>
    public static readonly TimeSpan BackfillMinRunway = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True while the backfill must be out of the way: on an NSE trading day from
    /// <see cref="BackfillQuietFrom"/> to the end of the backfills' quiet
    /// (<see cref="MarketIntelligenceSchedule.QuietUntilOn"/>), and whenever MCX is in session.
    /// </summary>
    /// <remarks>
    /// Stopped outright in the MCX session, not slowed: each request streams 40–80 MB and
    /// upserts up to 500k NAVs on the box that runs the crude strategies live until 23:30
    /// or 23:55, and a saturated CPU has made the feed stop draining before (28 Sep). The
    /// whole backfill is 2–4 hours; from the MCX close to 08:40 leaves more than 8 a night.
    /// </remarks>
    public static bool BackfillMustWait(DateTime nowUtc, Func<DateOnly, bool> isNseTradingDay, Func<DateTime, bool> isMcxOpen)
    {
        var day = IstTime.DateOf(nowUtc);
        var time = IstTime.ToIst(nowUtc).TimeOfDay;
        return (time >= BackfillQuietFrom && time < MarketIntelligenceSchedule.QuietUntilOn(day) && isNseTradingDay(day))
               || isMcxOpen(nowUtc);
    }

    /// <summary><see cref="BackfillMustWait(DateTime, Func{DateOnly, bool}, Func{DateTime, bool})"/> by the exchange calendar.</summary>
    public static bool BackfillMustWait(DateTime nowUtc, IMarketSessionService sessions) =>
        BackfillMustWait(nowUtc,
            d => sessions.GetSessionInfo(IstTime.MiddayUtc(d), "NSE", "CM").IsTradingDay,
            t => sessions.GetSessionInfo(t, "MCX", "COM").IsMarketOpen);

    /// <summary>Whether a backfill request may start now: not inside the quiet, nor within <see cref="BackfillMinRunway"/> of it.</summary>
    public static bool BackfillMayStart(DateTime nowUtc, IMarketSessionService sessions) =>
        !BackfillMustWait(nowUtc, sessions) && !BackfillMustWait(nowUtc + BackfillMinRunway, sessions);

    /// <summary>
    /// How long from <paramref name="nowUtc"/> a backfill request may run: to the last whole
    /// minute before it must be out of the way, and never more than <paramref name="longest"/>.
    /// </summary>
    public static TimeSpan BackfillRunway(DateTime nowUtc, TimeSpan longest, Func<DateTime, bool> mustWait)
    {
        var step = TimeSpan.FromMinutes(1);
        for (var ahead = TimeSpan.Zero; ahead <= longest + step; ahead += step)
        {
            if (mustWait(nowUtc + ahead)) return ahead <= step ? TimeSpan.Zero : Min(ahead - step, longest);
        }

        return longest;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>A sync that failed (AMFI or the network down) is tried again after this long.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Which run is due now, if any: "first" when nothing has ever run,
    /// "evening" once today's 23:30 IST has passed without a run since,
    /// "morning" once 07:00 IST has passed without a run since, when the last
    /// one failed or is behind <see cref="ExpectedNavDate"/>, and "retry" an
    /// hour after a failed run. A restart catches up the same way: a missed
    /// morning still runs at 10:00.
    /// </summary>
    public static string? Due(DateTime nowUtc, MutualFundSyncResult? last, Func<DateOnly, bool> isBusinessDay)
    {
        if (last is null) return "first";

        var today = IstTime.DateOf(nowUtc);
        var evening = IstTime.StartOfDayUtc(today) + EveningAt;
        var morning = IstTime.StartOfDayUtc(today) + MorningRetryAt;
        var lastRun = last.StartedUtc;

        if (nowUtc >= evening && lastRun < evening) return "evening";

        bool behind = !last.Ok || last.AsOfDate is null || last.AsOfDate < ExpectedNavDate(nowUtc, isBusinessDay);
        if (nowUtc >= morning && lastRun < morning && behind) return "morning";

        if (!last.Ok && nowUtc - lastRun >= RetryFailedAfter) return "retry";

        return null;
    }
}
