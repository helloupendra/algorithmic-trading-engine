namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>
/// When each market-intelligence recorder runs. Pure, so every timing rule is a
/// test. All times are IST, which has no daylight saving: the same wall-clock
/// rule holds every day of the year.
/// </summary>
public static class MarketIntelligenceSchedule
{
    /// <summary>The news recorder polls every feed this often, around the clock.</summary>
    public static readonly TimeSpan NewsEvery = TimeSpan.FromMinutes(5);

    /// <summary>NSE's corporate announcements, polled this often inside <see cref="InFilingsWindow"/>.</summary>
    public static readonly TimeSpan AnnouncementsEvery = TimeSpan.FromMinutes(10);

    /// <summary>NSE's board-meeting calendar changes a few times a day at most.</summary>
    public static readonly TimeSpan CalendarEvery = TimeSpan.FromHours(4);

    /// <summary>The news scorer runs this often inside <see cref="InFilingsWindow"/>.</summary>
    public static readonly TimeSpan ScoringEvery = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan FilingsFrom = new(6, 0, 0);
    public static readonly TimeSpan FilingsUntil = new(23, 30, 0);

    public static readonly TimeSpan SnapshotsFrom = new(6, 0, 0);
    public static readonly TimeSpan SnapshotsUntil = new(16, 0, 0);
    public static readonly TimeSpan SnapshotsEvery = TimeSpan.FromMinutes(15);

    /// <summary>The snapshot the 08:50 IST forecasts read: always taken, whatever the grid.</summary>
    public static readonly TimeSpan PreForecastSnapshot = new(8, 45, 0);

    /// <summary>A slot missed by less than this (the API was restarting) is taken late rather than skipped.</summary>
    public static readonly TimeSpan SnapshotCatchUp = TimeSpan.FromMinutes(10);

    /// <summary>Overseas daily bars are updated once a day from this time.</summary>
    public static readonly TimeSpan GlobalDailyFrom = new(7, 0, 0);

    /// <summary>
    /// How long after IST midnight the last overseas session of the previous
    /// date is surely over: the US futures close (17:00 New York, 02:30 IST in
    /// winter) and the rupee's London day (ends 05:30 IST in winter), with room.
    /// </summary>
    public static readonly TimeSpan OverseasDayOverAfter = TimeSpan.FromHours(7);

    public static readonly TimeSpan EveningFrom = new(18, 0, 0);
    public static readonly TimeSpan EveningUntil = new(23, 30, 0);

    /// <summary>
    /// The backfills stay out of the trading day (09:00–15:40 IST on NSE
    /// trading days): the server is a small box that also runs the strategies.
    /// </summary>
    public static readonly TimeSpan QuietFrom = new(9, 0, 0);
    public static readonly TimeSpan QuietUntil = new(15, 40, 0);

    private static TimeSpan IstTimeOfDay(DateTime nowUtc) => IstTime.ToIst(nowUtc).TimeOfDay;

    /// <summary>
    /// 06:00–23:30 IST, every day. Companies file on weekends and holidays too
    /// (27 Sep 2026 was a Sunday with filings until 18:47), and a filing made
    /// on a Sunday is exactly what Monday's open reacts to; overnight, NSE's
    /// page is quiet and the next morning's first poll asks for the whole day.
    /// </summary>
    public static bool InFilingsWindow(DateTime nowUtc)
    {
        var time = IstTimeOfDay(nowUtc);
        return time >= FilingsFrom && time <= FilingsUntil;
    }

    /// <summary>NSE's evening files are published 16:30–20:00 IST; 18:00–23:30 catches them the same evening.</summary>
    public static bool InEveningWindow(DateTime nowUtc)
    {
        var time = IstTimeOfDay(nowUtc);
        return time >= EveningFrom && time <= EveningUntil;
    }

    /// <summary>True while the backfills must wait: 09:00–15:40 IST on an NSE trading day.</summary>
    public static bool InQuietWindow(DateTime nowUtc, Func<DateOnly, bool> isTradingDay)
    {
        var time = IstTimeOfDay(nowUtc);
        return time >= QuietFrom && time < QuietUntil && isTradingDay(IstTime.DateOf(nowUtc));
    }

    /// <summary>The snapshot times of one IST date, as UTC; none on a weekend.</summary>
    public static IReadOnlyList<DateTime> SnapshotSlots(DateOnly istDate)
    {
        if (istDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return [];

        var times = new SortedSet<TimeSpan> { PreForecastSnapshot };
        for (var t = SnapshotsFrom; t <= SnapshotsUntil; t += SnapshotsEvery) times.Add(t);

        var midnight = istDate.ToDateTime(TimeOnly.MinValue);
        return times.Select(t => IstTime.FromIst(midnight + t)).ToList();
    }

    /// <summary>The latest slot at or before <paramref name="nowUtc"/> today, or null before the first one.</summary>
    public static DateTime? LatestSlot(DateTime nowUtc) =>
        SnapshotSlots(IstTime.DateOf(nowUtc)).Where(s => s <= nowUtc).Select(s => (DateTime?)s).LastOrDefault();

    /// <summary>The first slot after <paramref name="nowUtc"/>, looking up to a week ahead.</summary>
    public static DateTime NextSlot(DateTime nowUtc)
    {
        var day = IstTime.DateOf(nowUtc);
        for (int i = 0; i < 8; i++, day = day.AddDays(1))
        {
            foreach (var slot in SnapshotSlots(day))
            {
                if (slot > nowUtc) return slot;
            }
        }

        throw new InvalidOperationException("No snapshot slot within a week.");
    }

    /// <summary>
    /// The slot to take now, or null. A slot is taken once; one missed by less
    /// than <see cref="SnapshotCatchUp"/> is still taken, late, which is what
    /// saves 08:45 when the morning job restarts the API at that minute.
    /// </summary>
    public static DateTime? DueSlot(DateTime nowUtc, DateTime? lastTakenSlot)
    {
        var latest = LatestSlot(nowUtc);
        if (latest is null || nowUtc - latest.Value > SnapshotCatchUp) return null;
        return lastTakenSlot is null || lastTakenSlot < latest ? latest : null;
    }

    /// <summary>Whether the day's overseas update is owed: after 07:00 IST, once per IST date.</summary>
    public static bool GlobalDailyDue(DateTime nowUtc, DateOnly? lastRunIstDate) =>
        IstTimeOfDay(nowUtc) >= GlobalDailyFrom && (lastRunIstDate is null || lastRunIstDate < IstTime.DateOf(nowUtc));

    /// <summary>
    /// Whether a recorder has gone longer without a success than its schedule
    /// allows, for the status endpoint and the desk checkup that reads it.
    /// </summary>
    /// <remarks>
    /// Only inside the recorder's own window, and counted from the later of
    /// its last success, the API's start and the window's opening today: the
    /// announcements recorder at 06:05 is not late for having been quiet all
    /// night, and neither is anything in the first minutes after a restart.
    /// </remarks>
    public static bool IsOverdue(string recorder, DateTime nowUtc, DateTime startedUtc, DateTime? lastSuccessUtc, Func<DateOnly, bool> isTradingDay)
    {
        var today = IstTime.DateOf(nowUtc);
        var time = IstTimeOfDay(nowUtc);
        bool weekday = today.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

        (bool active, TimeSpan opens, TimeSpan allowed) = recorder switch
        {
            MarketIntelligenceNames.News => (true, TimeSpan.Zero, TimeSpan.FromMinutes(20)),
            MarketIntelligenceNames.Announcements => (InFilingsWindow(nowUtc), FilingsFrom, TimeSpan.FromMinutes(40)),
            MarketIntelligenceNames.Calendar => (InFilingsWindow(nowUtc), FilingsFrom, CalendarEvery + TimeSpan.FromHours(1)),
            MarketIntelligenceNames.NewsScoring => (InFilingsWindow(nowUtc), FilingsFrom, TimeSpan.FromMinutes(40)),
            MarketIntelligenceNames.QuoteSnapshots => (weekday && time >= SnapshotsFrom && time <= SnapshotsUntil + SnapshotsEvery, SnapshotsFrom, TimeSpan.FromMinutes(45)),
            MarketIntelligenceNames.GlobalDaily => (time >= GlobalDailyFrom, GlobalDailyFrom, TimeSpan.FromHours(1)),
            MarketIntelligenceNames.Breadth => (isTradingDay(today) && InEveningWindow(nowUtc), EveningFrom, TimeSpan.FromHours(3)),
            _ => (false, TimeSpan.Zero, TimeSpan.Zero),
        };
        if (!active) return false;

        var windowOpenedUtc = IstTime.FromIst(today.ToDateTime(TimeOnly.MinValue) + opens);
        var since = new[] { lastSuccessUtc ?? DateTime.MinValue, startedUtc, windowOpenedUtc }.Max();
        return nowUtc - since > allowed;
    }

    /// <summary>
    /// The newest overseas bar date that is surely final at <paramref name="nowUtc"/>:
    /// the day before the IST date of seven hours ago. At 07:00 IST that is
    /// yesterday; at 01:00 IST, while New York still trades, the day before.
    /// </summary>
    public static DateOnly LatestFinalOverseasDate(DateTime nowUtc) =>
        IstTime.DateOf(nowUtc - OverseasDayOverAfter).AddDays(-1);
}
