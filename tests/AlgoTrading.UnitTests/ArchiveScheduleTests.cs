using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

public class ArchiveScheduleTests
{
    private static readonly TimeSpan RunAt = new(23, 50, 0);

    [Fact]
    public void Nothing_is_due_before_the_run_time_when_yesterday_is_done()
    {
        var now = new DateTime(2026, 9, 10, 14, 0, 0);
        Assert.Empty(ArchiveSchedule.DueDays(new DateOnly(2026, 9, 9), now, RunAt));
    }

    [Fact]
    public void Today_becomes_due_at_the_run_time()
    {
        var now = new DateTime(2026, 9, 10, 23, 50, 0);
        Assert.Equal(new[] { new DateOnly(2026, 9, 10) }, ArchiveSchedule.DueDays(new DateOnly(2026, 9, 9), now, RunAt));
    }

    [Fact]
    public void A_missed_night_is_caught_up_the_next_morning()
    {
        // The API was down at 23:50 on the 10th; at 06:12 on the 11th the 10th is still owed.
        var now = new DateTime(2026, 9, 11, 6, 12, 0);
        Assert.Equal(new[] { new DateOnly(2026, 9, 10) }, ArchiveSchedule.DueDays(new DateOnly(2026, 9, 9), now, RunAt));
    }

    [Fact]
    public void Several_missed_days_come_back_in_order_and_are_capped()
    {
        var now = new DateTime(2026, 9, 20, 0, 0, 0);
        var days = ArchiveSchedule.DueDays(new DateOnly(2026, 9, 1), now, RunAt, maxDays: 3);
        Assert.Equal(new[] { new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 19) }, days);
    }

    [Fact]
    public void A_fresh_install_archives_only_the_latest_due_day()
    {
        var now = new DateTime(2026, 9, 10, 9, 0, 0);
        Assert.Equal(new[] { new DateOnly(2026, 9, 9) }, ArchiveSchedule.DueDays(null, now, RunAt));
    }

    [Fact]
    public void Run_time_parses_or_falls_back()
    {
        Assert.Equal(new TimeSpan(22, 5, 0), ArchiveSchedule.ParseRunAt("22:05"));
        Assert.Equal(ArchiveSchedule.DefaultRunAtIst, ArchiveSchedule.ParseRunAt("late"));
        Assert.Equal(ArchiveSchedule.DefaultRunAtIst, ArchiveSchedule.ParseRunAt(null));
    }
}
