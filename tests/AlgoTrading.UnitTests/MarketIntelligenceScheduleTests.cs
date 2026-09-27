using AlgoTrading.Api.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using static AlgoTrading.UnitTests.MarketIntelligenceTestKit;
using S = AlgoTrading.Infrastructure.Services.MarketIntelligence.MarketIntelligenceSchedule;

namespace AlgoTrading.UnitTests;

/// <summary>
/// When each recorder runs, in IST. Every edge is tested on both sides,
/// because the costly mistakes here are an off-by-one at a boundary: a
/// backfill still fetching at 09:00 on a trading day, or the 08:45 snapshot
/// skipped because the API restarted at that minute.
/// </summary>
public class MarketIntelligenceScheduleTests
{
    // Mon 28 Sep 2026 is a trading day; Sat 3 Oct is not; Fri 2 Oct 2026 is Gandhi Jayanti.
    private static readonly DateOnly GandhiJayanti = new(2026, 10, 2);
    private static bool Trading(DateOnly d) =>
        d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d != GandhiJayanti;

    [Theory]
    [InlineData(5, 59, false)]
    [InlineData(6, 0, true)]
    [InlineData(23, 30, true)]
    [InlineData(23, 31, false)]
    public void Filings_are_polled_from_06_00_to_23_30_ist(int hour, int minute, bool expected) =>
        Assert.Equal(expected, S.InFilingsWindow(Ist(2026, 9, 28, hour, minute)));

    [Fact]
    public void Filings_are_polled_on_weekends_too()
    {
        // 27 Sep 2026 was a Sunday with filings until 18:47.
        Assert.True(S.InFilingsWindow(Ist(2026, 9, 27, 18, 47)));
    }

    [Theory]
    [InlineData(28, 8, 59, false)]
    [InlineData(28, 9, 0, true)]
    [InlineData(28, 15, 39, true)]
    [InlineData(28, 15, 40, false)]
    [InlineData(3, 11, 0, false)]    // Saturday 3 Oct
    public void Backfills_stay_out_of_09_00_to_15_40_on_trading_days(int day, int hour, int minute, bool quiet)
    {
        int month = day == 3 ? 10 : 9;
        Assert.Equal(quiet, S.InQuietWindow(Ist(2026, month, day, hour, minute), Trading));
    }

    [Fact]
    public void A_holiday_is_not_a_quiet_day() =>
        Assert.False(S.InQuietWindow(Ist(2026, 10, 2, 11, 0), Trading));

    [Fact]
    public void A_weekday_has_41_snapshot_slots_from_06_00_to_16_00_including_08_45()
    {
        var slots = S.SnapshotSlots(new DateOnly(2026, 9, 28));

        Assert.Equal(41, slots.Count);
        Assert.Equal(Ist(2026, 9, 28, 6, 0), slots[0]);
        Assert.Equal(Ist(2026, 9, 28, 16, 0), slots[^1]);
        Assert.Contains(Ist(2026, 9, 28, 8, 45), slots);
        Assert.Empty(S.SnapshotSlots(new DateOnly(2026, 10, 3)));
    }

    [Fact]
    public void Ist_has_no_daylight_saving_so_08_45_is_03_15_utc_all_year()
    {
        Assert.Contains(new DateTime(2026, 1, 15, 3, 15, 0, DateTimeKind.Utc), S.SnapshotSlots(new DateOnly(2026, 1, 15)));
        Assert.Contains(new DateTime(2026, 7, 15, 3, 15, 0, DateTimeKind.Utc), S.SnapshotSlots(new DateOnly(2026, 7, 15)));
    }

    [Fact]
    public void The_08_45_slot_is_taken_on_its_minute_and_once()
    {
        var at = Ist(2026, 9, 28, 8, 45);
        Assert.Equal(at, S.DueSlot(at, lastTakenSlot: Ist(2026, 9, 28, 8, 30)));
        Assert.Null(S.DueSlot(at.AddSeconds(20), lastTakenSlot: at));
        Assert.Null(S.DueSlot(Ist(2026, 9, 28, 8, 44, 59), lastTakenSlot: Ist(2026, 9, 28, 8, 30)));
    }

    [Fact]
    public void A_restart_across_08_45_still_takes_the_slot_within_ten_minutes()
    {
        // The morning job restarts the API at about 08:45.
        Assert.Equal(Ist(2026, 9, 28, 8, 45), S.DueSlot(Ist(2026, 9, 28, 8, 52), lastTakenSlot: null));
        Assert.Equal(Ist(2026, 9, 28, 8, 45), S.DueSlot(Ist(2026, 9, 28, 8, 55), lastTakenSlot: Ist(2026, 9, 28, 8, 30)));

        // Later than that, the price is no longer the 08:45 price: wait for 09:00.
        Assert.Null(S.DueSlot(Ist(2026, 9, 28, 8, 56), lastTakenSlot: null));
    }

    [Fact]
    public void No_slot_before_06_00_after_16_10_or_at_a_weekend()
    {
        Assert.Null(S.DueSlot(Ist(2026, 9, 28, 5, 59), null));
        Assert.Equal(Ist(2026, 9, 28, 16, 0), S.DueSlot(Ist(2026, 9, 28, 16, 5), Ist(2026, 9, 28, 15, 45)));
        Assert.Null(S.DueSlot(Ist(2026, 9, 28, 16, 11), Ist(2026, 9, 28, 15, 45)));
        Assert.Null(S.DueSlot(Ist(2026, 10, 3, 9, 0), null));
    }

    [Fact]
    public void After_friday_s_last_slot_the_next_is_monday_06_00()
    {
        Assert.Equal(Ist(2026, 10, 5, 6, 0), S.NextSlot(Ist(2026, 10, 2, 16, 1)));
        Assert.Equal(Ist(2026, 9, 28, 8, 45), S.NextSlot(Ist(2026, 9, 28, 8, 44, 59)));
    }

    [Fact]
    public void The_overseas_update_is_owed_once_a_day_from_07_00()
    {
        var today = new DateOnly(2026, 9, 28);
        Assert.False(S.GlobalDailyDue(Ist(2026, 9, 28, 6, 59), null));
        Assert.True(S.GlobalDailyDue(Ist(2026, 9, 28, 7, 0), null));
        Assert.True(S.GlobalDailyDue(Ist(2026, 9, 28, 7, 0), today.AddDays(-1)));
        Assert.False(S.GlobalDailyDue(Ist(2026, 9, 28, 22, 0), today));
    }

    [Theory]
    [InlineData(8, 0, 21)]    // 07:00 onwards: Monday in New York is over
    [InlineData(6, 59, 20)]   // before: the futures' Monday might still be trading
    [InlineData(3, 0, 20)]    // New York has not closed at all
    public void An_overseas_bar_is_stored_only_once_its_day_is_surely_over(int hour, int minute, int finalDay) =>
        Assert.Equal(new DateOnly(2026, 9, finalDay), S.LatestFinalOverseasDate(Ist(2026, 9, 22, hour, minute)));

    [Fact]
    public void A_recorder_is_overdue_only_inside_its_window_and_counting_from_its_opening()
    {
        var started = Ist(2026, 9, 27, 0, 0);

        Assert.True(S.IsOverdue(MarketIntelligenceNames.News, Ist(2026, 9, 28, 10, 0), started, Ist(2026, 9, 28, 9, 35), Trading));
        Assert.False(S.IsOverdue(MarketIntelligenceNames.News, Ist(2026, 9, 28, 10, 0), started, Ist(2026, 9, 28, 9, 50), Trading));

        // Quiet all night is not late at 06:05; still quiet at 07:00 is.
        var lastNight = Ist(2026, 9, 27, 23, 30);
        Assert.False(S.IsOverdue(MarketIntelligenceNames.Announcements, Ist(2026, 9, 28, 6, 5), started, lastNight, Trading));
        Assert.True(S.IsOverdue(MarketIntelligenceNames.Announcements, Ist(2026, 9, 28, 7, 0), started, lastNight, Trading));

        // Nothing is overdue in the minutes after a restart.
        Assert.False(S.IsOverdue(MarketIntelligenceNames.QuoteSnapshots, Ist(2026, 9, 28, 9, 0), Ist(2026, 9, 28, 8, 45), null, Trading));
        Assert.False(S.IsOverdue(MarketIntelligenceNames.QuoteSnapshots, Ist(2026, 10, 3, 9, 0), started, null, Trading));

        // Breadth only on trading evenings, three hours after 18:00.
        Assert.True(S.IsOverdue(MarketIntelligenceNames.Breadth, Ist(2026, 9, 28, 21, 30), started, Ist(2026, 9, 25, 18, 30), Trading));
        Assert.False(S.IsOverdue(MarketIntelligenceNames.Breadth, Ist(2026, 10, 2, 21, 30), started, null, Trading));
    }

    [Fact]
    public void News_scoring_runs_every_ten_minutes_inside_06_00_to_23_30_but_not_from_08_40_to_15_40()
    {
        static bool Weekday(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.True(NewsScoringScheduler.Due(Ist(2026, 9, 28, 6, 0), null, Weekday));
        Assert.False(NewsScoringScheduler.Due(Ist(2026, 9, 28, 5, 59), null, Weekday));
        Assert.False(NewsScoringScheduler.Due(Ist(2026, 9, 28, 23, 31), null, Weekday));
        // The session belongs to the strategies on a trading day; a Sunday has none.
        Assert.False(NewsScoringScheduler.Due(Ist(2026, 9, 28, 10, 10), Ist(2026, 9, 28, 8, 50), Weekday));
        Assert.True(NewsScoringScheduler.Due(Ist(2026, 9, 28, 15, 40), Ist(2026, 9, 28, 8, 50), Weekday));
        Assert.True(NewsScoringScheduler.Due(Ist(2026, 9, 28, 8, 39), Ist(2026, 9, 28, 8, 20), Weekday));
        Assert.False(NewsScoringScheduler.Due(Ist(2026, 9, 28, 8, 40), Ist(2026, 9, 28, 8, 20), Weekday));
        Assert.False(NewsScoringScheduler.Due(Ist(2026, 9, 27, 10, 9), Ist(2026, 9, 27, 10, 0), Weekday));
        Assert.True(NewsScoringScheduler.Due(Ist(2026, 9, 27, 10, 10), Ist(2026, 9, 27, 10, 0), Weekday));
        Assert.Equal(new[] { "-m", "analysis", "news-score" }, NewsScoringScheduler.Arguments);
    }

    [Fact]
    public void A_failing_scorer_is_reported_once_per_ist_day()
    {
        Assert.True(NewsScoringScheduler.ShouldNotify(Ist(2026, 9, 28, 10, 0), null));
        Assert.False(NewsScoringScheduler.ShouldNotify(Ist(2026, 9, 28, 23, 0), "2026-09-28"));
        // 00:30 IST on the 29th is still the 28th in UTC; the day is IST's.
        Assert.True(NewsScoringScheduler.ShouldNotify(Ist(2026, 9, 29, 0, 30), "2026-09-28"));
    }
}
