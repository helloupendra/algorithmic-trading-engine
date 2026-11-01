using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services.MutualFunds;

namespace AlgoTrading.UnitTests;

/// <summary>The daily sync's decisions, its schedule, and the backfill's windows.</summary>
public class MutualFundSyncTests
{
    private static readonly Func<DateOnly, bool> Weekdays = d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>An IST wall-clock time as UTC.</summary>
    private static DateTime Ist(int year, int month, int day, int hour, int minute = 0) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static AmfiNavRow Row(int code, decimal? nav, DateOnly? date) =>
        new(code, "Fund", "AMC", "Open Ended Schemes(Test)", null, null, MutualFundPlans.Direct, MutualFundOptions.Growth, null, nav, date);

    // ------------------------------------------------------------ what to write

    [Fact]
    public void The_file_is_for_the_day_most_schemes_carry_not_the_newest_date()
    {
        // Liquid funds' Saturday and Sunday NAVs are newer, but far fewer.
        Assert.Equal(new DateOnly(2026, 10, 30), MutualFundNavSync.AsOfDate(MutualFundParserTests.Daily()));
    }

    [Fact]
    public void Only_new_days_and_corrections_are_written()
    {
        var oct30 = new DateOnly(2026, 10, 30);
        var stored = new Dictionary<int, (DateOnly? Date, decimal? Nav)>
        {
            [1] = (oct30, 10m),                           // same day, same NAV: nothing to do
            [2] = (oct30, 10m),                           // same day, AMFI corrected it
            [3] = (new DateOnly(2026, 10, 29), 10m),      // a new day
            [4] = (new DateOnly(2026, 10, 31), 10m),      // the file is older than what is stored
            [5] = (new DateOnly(2018, 7, 2), 13.0522m),   // a dormant scheme, already written once
        };
        var rows = new[]
        {
            Row(1, 10m, oct30),
            Row(2, 10.01m, oct30),
            Row(3, 10.5m, oct30),
            Row(4, 10m, oct30),
            Row(5, 13.0522m, new DateOnly(2018, 7, 2)),
            Row(6, 20m, oct30),                           // a scheme never seen
            Row(7, null, oct30),                          // no usable NAV
        };

        var written = MutualFundNavSync.NavsToWrite(rows, stored, MutualFundNavSources.AmfiDaily);

        Assert.Equal(new[] { 2, 3, 6 }, written.Select(n => n.SchemeCode));
        Assert.All(written, n => Assert.Equal(MutualFundNavSources.AmfiDaily, n.Source));
        Assert.Equal(10.01m, written[0].Nav);
    }

    [Fact]
    public void The_whole_daily_fixture_is_written_once_and_then_not_again()
    {
        var rows = MutualFundParserTests.Daily();
        var first = MutualFundNavSync.NavsToWrite(rows, new Dictionary<int, (DateOnly?, decimal?)>(), MutualFundNavSources.AmfiDaily);

        // Every scheme with a NAV, the zero-NAV segregated portfolio aside.
        Assert.Equal(36, first.Count);

        var stored = rows.Select(MutualFundNavSync.ToSchemeUpsert)
            .ToDictionary(s => s.SchemeCode, s => (s.LastNavDate, s.LastNav));
        Assert.Empty(MutualFundNavSync.NavsToWrite(rows, stored, MutualFundNavSources.AmfiDaily));
    }

    [Fact]
    public void A_history_batch_merges_each_scheme_once_with_its_date_range()
    {
        var rows = AmfiNavParser.Parse(new StringReader(MutualFundParserTests.Fixture("NAVHistory_tp1_20261029_20261030_sample.txt"))).ToList();

        var schemes = MutualFundHistory.SchemesOf(rows).ToDictionary(s => s.SchemeCode);

        Assert.Equal(rows.Select(r => r.SchemeCode).Distinct().Count(), schemes.Count);
        var uti = schemes[120716];
        Assert.Equal(new DateOnly(2026, 10, 29), uti.FirstNavDate);
        Assert.Equal(new DateOnly(2026, 10, 30), uti.LastNavDate);
        Assert.Equal(162.3751m, uti.LastNav);
    }

    [Fact]
    public void A_scheme_without_a_usable_nav_does_not_move_its_nav_dates()
    {
        var upsert = MutualFundNavSync.ToSchemeUpsert(MutualFundParserTests.Daily().Single(r => r.SchemeCode == 147956));

        Assert.Null(upsert.FirstNavDate);
        Assert.Null(upsert.LastNavDate);
        Assert.Null(upsert.LastNav);
    }

    // ------------------------------------------------------------ schedule

    [Theory]
    // Tuesday 3 Nov 07:00: Monday's NAVs.
    [InlineData(2026, 11, 3, 7, 0, 2026, 11, 2)]
    // Monday 2 Nov 23:00, before the sync: still Friday's.
    [InlineData(2026, 11, 2, 23, 0, 2026, 10, 30)]
    // Monday 2 Nov 23:35: Monday's.
    [InlineData(2026, 11, 2, 23, 35, 2026, 11, 2)]
    // Sunday 1 Nov: Friday's.
    [InlineData(2026, 11, 1, 18, 0, 2026, 10, 30)]
    public void The_expected_nav_day_is_the_last_business_day_whose_sync_has_run(int y, int m, int d, int h, int min, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), MutualFundSchedule.ExpectedNavDate(Ist(y, m, d, h, min), Weekdays));

    [Fact]
    public void A_market_holiday_is_not_a_business_day()
    {
        var holiday = new DateOnly(2026, 11, 2);
        bool IsBusinessDay(DateOnly d) => Weekdays(d) && d != holiday;

        // Tuesday morning after a Monday holiday: Friday's NAVs are the newest due.
        var friday = new DateOnly(2026, 10, 30);
        Assert.Equal(friday, MutualFundSchedule.ExpectedNavDate(Ist(2026, 11, 3, 7, 0), IsBusinessDay));
        // Wednesday morning, still on Friday's: one business day behind, not two.
        var expected = MutualFundSchedule.ExpectedNavDate(Ist(2026, 11, 4, 7, 0), IsBusinessDay);
        Assert.Equal(new DateOnly(2026, 11, 3), expected);
        Assert.Equal(1, MutualFundSchedule.BusinessDaysBehind(friday, expected, IsBusinessDay));
    }

    [Fact]
    public void Business_days_behind_skip_the_weekend()
    {
        var friday = new DateOnly(2026, 10, 30);

        Assert.Equal(0, MutualFundSchedule.BusinessDaysBehind(friday, friday, Weekdays));
        Assert.Equal(1, MutualFundSchedule.BusinessDaysBehind(friday, new DateOnly(2026, 11, 2), Weekdays));
        Assert.Equal(2, MutualFundSchedule.BusinessDaysBehind(friday, new DateOnly(2026, 11, 3), Weekdays));
        Assert.Equal(int.MaxValue, MutualFundSchedule.BusinessDaysBehind(null, friday, Weekdays));
    }

    private static MutualFundSyncResult Synced(DateTime startedUtc, bool ok, DateOnly? asOf) =>
        new() { Trigger = "evening", StartedUtc = startedUtc, FinishedUtc = startedUtc.AddSeconds(3), Ok = ok, AsOfDate = asOf };

    [Fact]
    public void A_desk_that_never_synced_syncs_at_once()
    {
        Assert.Equal("first", MutualFundSchedule.Due(Ist(2026, 11, 1, 14, 0), null, Weekdays));
    }

    [Fact]
    public void The_evening_run_is_due_at_2330_every_day_once()
    {
        var monday = new DateOnly(2026, 11, 2);
        var lastMorning = Synced(Ist(2026, 11, 2, 7, 5), ok: true, asOf: new DateOnly(2026, 10, 30));

        Assert.Null(MutualFundSchedule.Due(Ist(2026, 11, 2, 23, 29), lastMorning, Weekdays));
        Assert.Equal("evening", MutualFundSchedule.Due(Ist(2026, 11, 2, 23, 31), lastMorning, Weekdays));
        Assert.Null(MutualFundSchedule.Due(Ist(2026, 11, 2, 23, 40), Synced(Ist(2026, 11, 2, 23, 31), true, monday), Weekdays));
        // Saturday too: liquid funds publish weekend NAVs.
        Assert.Equal("evening", MutualFundSchedule.Due(Ist(2026, 10, 31, 23, 35), Synced(Ist(2026, 10, 30, 23, 30), true, new DateOnly(2026, 10, 30)), Weekdays));
    }

    [Fact]
    public void The_morning_retry_runs_only_when_the_evening_fell_short()
    {
        var monday = new DateOnly(2026, 11, 2);
        var behind = Synced(Ist(2026, 11, 2, 23, 30), ok: true, asOf: new DateOnly(2026, 10, 30));   // AMFI late with Monday
        var current = Synced(Ist(2026, 11, 2, 23, 30), ok: true, asOf: monday);
        var failed = Synced(Ist(2026, 11, 2, 23, 30), ok: false, asOf: null);

        Assert.Null(MutualFundSchedule.Due(Ist(2026, 11, 3, 6, 59), behind, Weekdays));
        Assert.Equal("morning", MutualFundSchedule.Due(Ist(2026, 11, 3, 7, 1), behind, Weekdays));
        Assert.Null(MutualFundSchedule.Due(Ist(2026, 11, 3, 7, 1), current, Weekdays));
        Assert.Equal("morning", MutualFundSchedule.Due(Ist(2026, 11, 3, 7, 1), failed, Weekdays));
        // A restart at 10:00 still makes up a missed morning.
        Assert.Equal("morning", MutualFundSchedule.Due(Ist(2026, 11, 3, 10, 0), behind, Weekdays));
    }

    [Fact]
    public void A_failed_sync_is_tried_again_after_an_hour()
    {
        var failed = Synced(Ist(2026, 11, 3, 14, 0), ok: false, asOf: null);

        Assert.Null(MutualFundSchedule.Due(Ist(2026, 11, 3, 14, 30), failed, Weekdays));
        Assert.Equal("retry", MutualFundSchedule.Due(Ist(2026, 11, 3, 15, 1), failed, Weekdays));
    }

    // ------------------------------------------------------------ backfill windows

    [Fact]
    public void Windows_run_newest_first_in_90_days_and_stop_at_april_2006()
    {
        var state = new MutualFundBackfillState { Top = new DateOnly(2026, 10, 31) };

        Assert.Equal((new DateOnly(2026, 8, 3), new DateOnly(2026, 10, 31)), state.NextWindow());

        state.Cursor = new DateOnly(2026, 8, 3);
        Assert.Equal((new DateOnly(2026, 5, 5), new DateOnly(2026, 8, 2)), state.NextWindow());

        state.Cursor = new DateOnly(2006, 6, 1);
        Assert.Equal((MutualFundHistory.Earliest, new DateOnly(2006, 5, 31)), state.NextWindow());

        state.Cursor = MutualFundHistory.Earliest;
        Assert.Null(state.NextWindow());
        Assert.Equal(0, state.WindowsLeft());
    }

    [Fact]
    public void A_backfill_from_yesterday_back_to_2006_is_84_windows()
    {
        var state = new MutualFundBackfillState { Top = new DateOnly(2026, 10, 31) };

        Assert.Equal(84, state.WindowsLeft());
    }

    [Fact]
    public void After_a_failure_the_backfill_waits_an_hour()
    {
        var now = new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);
        var state = new MutualFundBackfillState { Requested = true, State = "failed", UpdatedUtc = now.AddMinutes(-20) };

        Assert.False(MutualFundHistory.ReadyToRun(state, now));
        Assert.True(MutualFundHistory.ReadyToRun(state, now.AddMinutes(41)));
        Assert.False(MutualFundHistory.ReadyToRun(new MutualFundBackfillState { Requested = false }, now));
    }

    [Fact]
    public void The_history_url_is_amfis_own_pattern()
    {
        Assert.Equal("https://portal.amfiindia.com/DownloadNAVHistoryReport_Po.aspx?tp=1&frmdt=01-Apr-2020&todt=29-Jun-2020",
            MutualFundHistory.Url(new MutualFundsOptions().HistoryUrl, 1, new DateOnly(2020, 4, 1), new DateOnly(2020, 6, 29)));
    }

    // ------------------------------------------------------------ freshness

    [Fact]
    public void A_sunday_with_only_the_liquid_funds_is_not_a_full_nav_day()
    {
        var perDay = new[]
        {
            (new DateOnly(2026, 10, 29), 7210),
            (new DateOnly(2026, 10, 30), 7224),
            (new DateOnly(2026, 10, 31), 827),
            (new DateOnly(2026, 11, 1), 712),
        };

        Assert.Equal(new DateOnly(2026, 10, 30), MutualFundNavSync.LatestFullDate(perDay));
        Assert.Null(MutualFundNavSync.LatestFullDate([]));
    }
}
