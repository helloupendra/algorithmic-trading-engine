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
