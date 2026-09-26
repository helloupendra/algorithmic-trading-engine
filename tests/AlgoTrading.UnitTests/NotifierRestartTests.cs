using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The notifier is adopted across API restarts, so it only moves onto new code
/// when its script is newer than the process (on 27 Sep it still ran 17 Sep's).
/// </summary>
public class NotifierRestartTests
{
    private static readonly DateTime Started = new(2026, 9, 17, 15, 48, 46, DateTimeKind.Utc);

    [Fact]
    public void A_script_changed_after_the_process_started_means_older_code()
    {
        Assert.True(NotifierStartupService.RunsOlderCode(new DateTime(2026, 9, 26, 20, 0, 0, DateTimeKind.Utc), Started));
    }

    [Fact]
    public void A_script_from_before_the_start_is_what_it_runs()
    {
        Assert.False(NotifierStartupService.RunsOlderCode(Started.AddDays(-2), Started));
        Assert.False(NotifierStartupService.RunsOlderCode(Started, Started));
    }

    [Fact]
    public void A_missing_script_never_restarts_it()
    {
        // File.GetLastWriteTimeUtc answers 1601-01-01 for a path that does not exist.
        Assert.False(NotifierStartupService.RunsOlderCode(DateTime.FromFileTimeUtc(0), Started));
    }
}
