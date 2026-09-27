using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// When the forecast scheduler issues, scores, or reports a missed morning.
/// </summary>
/// <remarks>
/// The week of Monday 28 Sep 2026, with Friday 2 Oct (Gandhi Jayanti) as an
/// exchange holiday. What matters: issuing only between 08:50 and the 09:15
/// open on a trading day, never made up after the open but reported; scoring
/// at 15:50 and again whenever a run is owed after a start-up; each at most
/// once per session; and the exact command line the Python CLI answers to.
/// </remarks>
public class ForecastScheduleTests
{
    private static readonly DateOnly Friday = new(2026, 9, 25);
    private static readonly DateOnly Monday = new(2026, 9, 28);
    private static readonly DateOnly Tuesday = new(2026, 9, 29);
    private static readonly DateOnly Thursday = new(2026, 10, 1);
    private static readonly DateOnly GandhiJayanti = new(2026, 10, 2);

    private static bool TradingDay(DateOnly date)
        => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && date != GandhiJayanti;

    private static ForecastJob? Next(DateTime nowUtc, DateOnly? lastIssued, DateOnly? lastScored, bool scoreAllowed = true)
        => ForecastSchedule.Next(nowUtc, TradingDay, lastIssued, lastScored, scoreAllowed);

    private static DateTime Ist(DateOnly day, int h, int mi, int s = 0)
        => new DateTime(day.Year, day.Month, day.Day, h, mi, s, DateTimeKind.Utc).AddMinutes(-330);

    [Fact]
    public void Nothing_is_due_before_08_50_when_yesterday_was_scored()
    {
        Assert.Null(Next(Ist(Monday, 8, 49, 59), lastIssued: Friday, lastScored: Friday));
    }

    [Theory]
    [InlineData(8, 50, 0)]
    [InlineData(9, 0, 0)]
    [InlineData(9, 14, 59)]
    public void The_morning_job_is_due_from_08_50_until_the_open(int h, int mi, int s)
    {
        Assert.Equal(new ForecastJob(ForecastJobKind.Issue, Monday), Next(Ist(Monday, h, mi, s), lastIssued: Friday, lastScored: Friday));
    }

    [Theory]
    [InlineData(9, 15)]
    [InlineData(12, 0)]
    [InlineData(22, 0)]
    public void A_morning_missed_is_reported_not_made_up_after_the_open(int h, int mi)
    {
        Assert.Equal(new ForecastJob(ForecastJobKind.MissedIssue, Monday), Next(Ist(Monday, h, mi), lastIssued: Friday, lastScored: Friday));
    }

    [Fact]
    public void The_morning_job_runs_once_per_session_even_across_restarts()
    {
        // The scheduler records the session before it starts the job, so a
        // restart at 09:05 finds it done.
        Assert.Null(Next(Ist(Monday, 9, 5), lastIssued: Monday, lastScored: Friday));
        Assert.Null(Next(Ist(Monday, 12, 0), lastIssued: Monday, lastScored: Friday));
    }

    [Fact]
    public void Nothing_is_issued_on_a_weekend_or_a_holiday()
    {
        var saturday = new DateOnly(2026, 9, 26);

        Assert.Null(Next(Ist(saturday, 8, 55), lastIssued: Friday, lastScored: Friday));
        Assert.Null(Next(Ist(GandhiJayanti, 8, 55), lastIssued: Thursday, lastScored: Thursday));
        Assert.Null(Next(Ist(GandhiJayanti, 10, 0), lastIssued: Thursday, lastScored: Thursday));
    }

    [Fact]
    public void Scoring_is_due_at_15_50_and_not_a_second_before()
    {
        Assert.Null(Next(Ist(Monday, 15, 49, 59), lastIssued: Monday, lastScored: Friday));
        Assert.Equal(new ForecastJob(ForecastJobKind.Score, Monday), Next(Ist(Monday, 15, 50), lastIssued: Monday, lastScored: Friday));
        Assert.Null(Next(Ist(Monday, 18, 0), lastIssued: Monday, lastScored: Monday));
    }

    [Fact]
    public void A_start_up_after_a_missed_evening_catches_up_once_it_is_allowed_to()
    {
        // Down at 15:50 on Monday, back on Tuesday morning: Monday is owed.
        var tuesdayMorning = Ist(Tuesday, 10, 0);

        Assert.Null(Next(tuesdayMorning, lastIssued: Tuesday, lastScored: Friday, scoreAllowed: false));
        Assert.Equal(new ForecastJob(ForecastJobKind.Score, Monday), Next(tuesdayMorning, lastIssued: Tuesday, lastScored: Friday));
    }

    [Fact]
    public void A_start_up_with_nothing_owed_runs_nothing()
    {
        // The desk restarts the API at 08:45 every morning; yesterday was
        // scored at 15:50, so there is nothing to catch up.
        Assert.Null(Next(Ist(Tuesday, 8, 47), lastIssued: Monday, lastScored: Monday));
    }

    [Fact]
    public void A_catch_up_on_a_weekend_scores_the_last_trading_day()
    {
        var saturday = new DateOnly(2026, 9, 26);

        Assert.Equal(new ForecastJob(ForecastJobKind.Score, Friday), Next(Ist(saturday, 11, 0), lastIssued: Friday, lastScored: new DateOnly(2026, 9, 24)));
        Assert.Null(Next(Ist(saturday, 11, 0), lastIssued: Friday, lastScored: Friday));
    }

    [Fact]
    public void The_first_start_ever_scores_whatever_is_there()
    {
        Assert.Equal(new ForecastJob(ForecastJobKind.Score, Friday), Next(Ist(Monday, 8, 0), lastIssued: null, lastScored: null));
    }

    [Fact]
    public void Issuing_goes_first_when_both_are_due_and_scoring_follows()
    {
        var at = Ist(Tuesday, 8, 55);

        Assert.Equal(new ForecastJob(ForecastJobKind.Issue, Tuesday), Next(at, lastIssued: Monday, lastScored: Friday));
        Assert.Equal(new ForecastJob(ForecastJobKind.Score, Monday), Next(at, lastIssued: Tuesday, lastScored: Friday));
    }

    [Fact]
    public void The_scorable_session_skips_weekends_and_holidays()
    {
        Assert.Equal(Thursday, ForecastSchedule.LatestScorableSession(Ist(GandhiJayanti, 16, 0), TradingDay));
        Assert.Equal(Friday, ForecastSchedule.LatestScorableSession(Ist(Monday, 15, 49), TradingDay));
        Assert.Equal(Monday, ForecastSchedule.LatestScorableSession(Ist(Monday, 15, 50), TradingDay));
        Assert.Null(ForecastSchedule.LatestScorableSession(Ist(Monday, 16, 0), _ => false));
    }

    [Fact]
    public void The_command_lines_are_the_Python_CLI_s()
    {
        Assert.Equal(
            new[] { "-m", "analysis", "issue", "--session", "2026-09-28" },
            ForecastSchedule.Arguments(new ForecastJob(ForecastJobKind.Issue, Monday)));
        Assert.Equal(
            new[] { "-m", "analysis", "score" },
            ForecastSchedule.Arguments(new ForecastJob(ForecastJobKind.Score, Monday)));
        Assert.Equal("python -m analysis issue --session 2026-09-28", ForecastSchedule.Describe(new ForecastJob(ForecastJobKind.Issue, Monday)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ForecastSchedule.Arguments(new ForecastJob(ForecastJobKind.MissedIssue, Monday)));
    }
}
