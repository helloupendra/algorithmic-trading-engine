using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.LevelAlerts;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What counts as a cross, a touch and a re-arm, and which readings may be
/// compared at all: the rules every level alert is judged by.
/// </summary>
public class LevelCrossTests
{
    private static readonly DateOnly Day = new(2026, 10, 30);

    /// <summary>IST wall time on Friday 2026-10-30 → UTC.</summary>
    private static DateTime Ist(int h, int m, int s = 0) => new DateTime(2026, 10, 30, h, m, s, DateTimeKind.Utc).AddMinutes(-330);

    [Theory]
    [InlineData(24790, 24810, true)]     // below to above
    [InlineData(24800, 24800.05, true)]  // from on the level to above it
    [InlineData(24790, 24800, false)]    // reaching the level is not crossing it
    [InlineData(24810, 24820, false)]    // already above: no cross, however long it stays there
    [InlineData(24810, 24790, false)]    // the other way
    public void Above_is_from_at_or_below_the_level_to_strictly_above_it(double previous, double current, bool crossed)
    {
        Assert.Equal(crossed, LevelCross.Crossed(LevelCondition.Above, 24800m, (decimal)previous, (decimal)current));
    }

    [Theory]
    [InlineData(24810, 24790, true)]
    [InlineData(24800, 24799.95, true)]
    [InlineData(24810, 24800, false)]
    [InlineData(24790, 24780, false)]
    [InlineData(24790, 24810, false)]
    public void Below_mirrors_above(double previous, double current, bool crossed)
    {
        Assert.Equal(crossed, LevelCross.Crossed(LevelCondition.Below, 24800m, (decimal)previous, (decimal)current));
    }

    [Theory]
    [InlineData(24790, 24800, true)]   // reached from below
    [InlineData(24810, 24800, true)]   // reached from above
    [InlineData(24790, 24810, true)]   // went through it between two readings
    [InlineData(24810, 24790, true)]
    [InlineData(24800, 24800, true)]   // sat on it
    [InlineData(24790, 24799.95, false)]
    [InlineData(24810, 24805, false)]
    public void Touch_is_the_level_reached_or_passed_through_from_either_side(double previous, double current, bool touched)
    {
        Assert.Equal(touched, LevelCross.Crossed(LevelCondition.Touch, 24800m, (decimal)previous, (decimal)current));
    }

    [Fact]
    public void A_repeating_rule_fires_once_then_waits_to_move_back_by_its_gap_before_firing_again()
    {
        // 24,800 with a 0.2% gap: it re-arms at or below 24,750.40.
        decimal gap = LevelCross.GapFor(LevelAlertKinds.Price, 24800m, 0.2m);
        Assert.Equal(49.6m, gap);

        bool armed = true;
        var path = new[] { 24790m, 24810m, 24795m, 24812m, 24760m, 24750m, 24790m, 24805m };
        var fires = new List<decimal>();
        for (int i = 1; i < path.Length; i++)
        {
            var step = LevelCross.Step(LevelCondition.Above, 24800m, gap, armed, path[i - 1], path[i]);
            if (step.Fired) fires.Add(path[i]);
            armed = step.Armed;
        }

        // Fired at 24,810; the dip to 24,795 and back is chatter, not a second cross;
        // 24,750 re-armed it, and 24,805 fired it again.
        Assert.Equal(new[] { 24810m, 24805m }, fires);
        Assert.False(armed);
    }

    [Fact]
    public void The_reading_that_re_arms_a_rule_never_also_fires_it()
    {
        // Disarmed, a touch rule sees a pair that goes through the level and on past the gap.
        var step = LevelCross.Step(LevelCondition.Touch, 100m, 1m, armed: false, previous: 99.5m, current: 101.5m);

        Assert.Equal(new LevelStep(Fired: false, Armed: true), step);
        Assert.True(LevelCross.Step(LevelCondition.Touch, 100m, 1m, armed: true, previous: 101.5m, current: 100m).Fired);
    }

    [Fact]
    public void A_below_rule_re_arms_above_the_level_and_a_pcr_gap_is_in_points()
    {
        decimal gap = LevelCross.GapFor(LevelAlertKinds.Pcr, 0.8m, null);
        Assert.Equal(LevelCross.DefaultPcrRearmPoints, gap);
        Assert.False(LevelCross.Rearmed(LevelCondition.Below, 0.8m, gap, 0.84m));
        Assert.True(LevelCross.Rearmed(LevelCondition.Below, 0.8m, gap, 0.85m));
        Assert.True(LevelCross.Rearmed(LevelCondition.Touch, 0.8m, gap, 0.75m));
        Assert.Equal(24800m * LevelCross.DefaultPriceRearmPercent / 100m, LevelCross.GapFor(LevelAlertKinds.Price, 24800m, null));
    }

    [Fact]
    public void Only_two_readings_of_one_session_are_compared_and_the_later_must_be_newer()
    {
        var close = new LevelSample(24790m, Ist(15, 29, 58), Day.AddDays(-1));
        var open = new LevelSample(24900m, Ist(9, 15, 2), Day);
        var next = new LevelSample(24910m, Ist(9, 15, 7), Day);

        // An overnight gap through the level is a baseline, not a cross.
        Assert.False(LevelCross.Comparable(close, open));
        Assert.True(LevelCross.Comparable(open, next));
        Assert.False(LevelCross.Comparable(next, open));
        Assert.False(LevelCross.Comparable(open, open));
    }

    [Fact]
    public void A_quote_older_than_a_minute_or_stamped_in_the_future_is_not_fresh()
    {
        var now = Ist(10, 0);
        Assert.True(LevelCross.IsFresh(now.AddSeconds(-59), now, LevelCross.PriceFreshFor));
        Assert.False(LevelCross.IsFresh(now.AddSeconds(-61), now, LevelCross.PriceFreshFor));
        Assert.False(LevelCross.IsFresh(now.AddMinutes(2), now, LevelCross.PriceFreshFor));
        // A chain is captured once a minute; three minutes old means the recorder has stopped.
        Assert.True(LevelCross.IsFresh(now.AddSeconds(-110), now, LevelCross.PcrFreshFor));
        Assert.False(LevelCross.IsFresh(now.AddMinutes(-4), now, LevelCross.PcrFreshFor));
    }

    [Fact]
    public void A_quote_is_as_old_as_its_exchange_stamp_unless_that_stamp_is_only_a_date()
    {
        var written = Ist(10, 0, 5);
        Assert.Equal(Ist(10, 0, 4), LevelCross.QuoteTime(written, Ist(10, 0, 4)));
        // BSE's pre-open replay of the close is stamped midnight IST: a day, not a time.
        Assert.Equal(written, LevelCross.QuoteTime(written, new DateTime(2026, 10, 29, 18, 30, 0, DateTimeKind.Utc)));
        Assert.Equal(written, LevelCross.QuoteTime(written, null));
    }

    [Fact]
    public void A_pcr_is_compared_at_the_two_decimals_the_chain_page_shows()
    {
        // 1.2996 reads 1.30 on the page: it has not gone above 1.30 until the page says 1.31.
        Assert.Equal(1.30m, LevelCross.RoundPcr(1.2996m));
        Assert.False(LevelCross.Crossed(LevelCondition.Above, 1.30m, LevelCross.RoundPcr(1.28m), LevelCross.RoundPcr(1.2996m)));
        Assert.True(LevelCross.Crossed(LevelCondition.Above, 1.30m, LevelCross.RoundPcr(1.2996m), LevelCross.RoundPcr(1.3051m)));
    }

    [Fact]
    public void A_rule_is_checked_for_every_mistake_at_once_in_words_for_the_form()
    {
        var today = Day;
        Assert.Empty(LevelAlertRules.Validate("price", "nse:nifty50-index", "above", 24800m, false, null, null, null, today));
        Assert.Empty(LevelAlertRules.Validate("pcr", "banknifty", "below", 0.7m, true, 0.05m, "oversold", today, today));

        var errors = LevelAlertRules.Validate("pcr", "NIFTY", "touch", 12m, true, 9m, new string('x', 201), today.AddDays(-1), today);
        Assert.Contains(errors, e => e.Contains("touch is for prices"));
        Assert.Contains(errors, e => e.Contains("at most 10"));
        Assert.Contains(errors, e => e.Contains("PCR points"));
        Assert.Contains(errors, e => e.Contains("200 characters"));
        Assert.Contains(errors, e => e.Contains("in the past"));

        Assert.Contains("EXCHANGE:NAME", Assert.Single(LevelAlertRules.Validate("price", "NIFTY", "above", 1m, false, null, null, null, today)));
        Assert.Contains("above zero", Assert.Single(LevelAlertRules.Validate("price", "NSE:SBIN-EQ", "below", 0m, false, null, null, null, today)));
        Assert.Single(LevelAlertRules.Validate("volume", "NSE:SBIN-EQ", "below", 1m, false, null, null, null, today));
    }
}
