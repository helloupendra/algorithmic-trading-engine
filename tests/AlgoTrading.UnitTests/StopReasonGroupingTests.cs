using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The cut that decides how a strategy's track record groups its runs. A stop
/// reason carries its numbers with it ("Stop loss hit: P&amp;L −₹5,120 ≤ −₹5,000"),
/// so grouping on the whole string would put every stop-loss in a bucket of its
/// own and the "how the runs ended" table would say nothing at all.
/// </summary>
public class StopReasonGroupingTests
{
    [Theory]
    [InlineData("Stop loss hit: P&L −₹5,120 ≤ −₹5,000", "Stop loss hit")]
    [InlineData("Stop loss hit: P&L −₹80 ≤ −₹50", "Stop loss hit")]
    [InlineData("Target hit: P&L ₹4,000 ≥ ₹4,000", "Target hit")]
    [InlineData("Market closed (15:30 IST)", "Market closed")]
    [InlineData("Runner exited (code 1)", "Runner exited")]
    [InlineData("API restarted; runner not found", "API restarted")]
    [InlineData("Stopped by admin", "Stopped by admin")]
    public void A_reason_is_cut_at_its_detail(string reason, string expected)
    {
        Assert.Equal(expected, LiveRunHistoryBuilder.ShortStopReason(reason));
    }

    [Fact]
    public void Two_stop_losses_with_different_numbers_group_together()
    {
        var first = LiveRunHistoryBuilder.ShortStopReason("Stop loss hit: P&L −₹5,120 ≤ −₹5,000");
        var second = LiveRunHistoryBuilder.ShortStopReason("Stop loss hit: P&L −₹12 ≤ −₹10");
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_cut_reads_as_no_reason(string? reason)
    {
        // The caller buckets these under "Not recorded" — a null here must not
        // become an empty row in the table.
        Assert.Null(LiveRunHistoryBuilder.ShortStopReason(reason));
    }

    [Theory]
    [InlineData(":", ":")]
    [InlineData(": nothing before the colon", ": nothing before the colon")]
    public void A_reason_that_opens_on_a_separator_is_kept_whole(string reason, string expected)
    {
        // A separator at position 0 is not a cut: cutting there would label the
        // bucket with an empty string. The console's shortStopReason() guards
        // the same way (`i > 0`), and the two must agree — a run that reads
        // "Stop loss hit" on the history page cannot read anything else in the
        // record. The API writes no reason shaped like this; the rule exists so
        // that if one ever arrives it survives intact instead of vanishing.
        Assert.Equal(expected, LiveRunHistoryBuilder.ShortStopReason(reason));
    }

    [Fact]
    public void The_earliest_separator_wins()
    {
        // Both ":" and " (" appear; the reason ends at whichever comes first.
        Assert.Equal(
            "Stop loss hit",
            LiveRunHistoryBuilder.ShortStopReason("Stop loss hit: P&L −₹5,120 (overall rule)"));
    }
}
