using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The parts of an option chain a trader actually acts on: what a strike is
/// doing, where the market is heaviest, and where it would hurt least to expire.
/// None of it needs a database, so none of it should need one to be trusted.
/// </summary>
public class OptionChainAnalyticsTests
{
    // --- build-up classification -----------------------------------------

    [Theory]
    [InlineData(10, 5000, BuildUp.LongBuildUp)]      // price up,   OI up   -> new buyers
    [InlineData(-10, 5000, BuildUp.ShortBuildUp)]    // price down, OI up   -> new sellers
    [InlineData(10, -5000, BuildUp.ShortCovering)]   // price up,   OI down -> shorts buying back
    [InlineData(-10, -5000, BuildUp.LongUnwinding)]  // price down, OI down -> longs leaving
    public void The_two_directions_together_name_the_behaviour(
        decimal priceChange, long oiChange, BuildUp expected)
    {
        Assert.Equal(expected, OptionChainAnalytics.Classify(priceChange, oiChange));
    }

    [Fact]
    public void Nothing_is_claimed_when_neither_side_moved()
    {
        Assert.Equal(BuildUp.Neutral, OptionChainAnalytics.Classify(0m, 0));
        Assert.Equal(BuildUp.Neutral, OptionChainAnalytics.Classify(10m, 0));
        Assert.Equal(BuildUp.Neutral, OptionChainAnalytics.Classify(0m, 5000));
    }

    [Fact]
    public void A_move_below_the_threshold_is_not_a_build_up()
    {
        // A paisa on two contracts is noise, and labelling it fills the column
        // with confidence exactly where the chain is thinnest.
        Assert.Equal(
            BuildUp.Neutral,
            OptionChainAnalytics.Classify(0.05m, 50, priceThreshold: 0.5m, openInterestThreshold: 100));

        Assert.Equal(
            BuildUp.LongBuildUp,
            OptionChainAnalytics.Classify(2m, 5000, priceThreshold: 0.5m, openInterestThreshold: 100));
    }

    // --- put-call ratio ---------------------------------------------------

    [Fact]
    public void Pcr_is_puts_over_calls()
    {
        Assert.Equal(2m, OptionChainAnalytics.PutCallRatio(200_000, 100_000));
        Assert.Equal(0.5m, OptionChainAnalytics.PutCallRatio(50_000, 100_000));
    }

    [Fact]
    public void Pcr_with_no_calls_is_unknown_rather_than_zero()
    {
        // "0" reads as an extreme signal. "No calls written here" is not a
        // signal at all, and the two must not look alike on screen.
        Assert.Null(OptionChainAnalytics.PutCallRatio(200_000, 0));
    }

    [Fact]
    public void A_change_percent_needs_something_to_have_changed_from()
    {
        Assert.Equal(50m, OptionChainAnalytics.ChangePercent(150, 100));
        Assert.Equal(-25m, OptionChainAnalytics.ChangePercent(75, 100));
        Assert.Null(OptionChainAnalytics.ChangePercent(150, 0));
    }

    // --- max pain ---------------------------------------------------------

    private static OptionChainAnalytics.StrikeInterest S(decimal strike, long calls, long puts)
        => new(strike, calls, puts);

    [Fact]
    public void Max_pain_is_the_settlement_that_costs_writers_least()
    {
        // Calls stacked above, puts stacked below: the least painful settlement
        // is the strike between them.
        var strikes = new[]
        {
            S(57000, 1_000, 500_000),
            S(57500, 100_000, 100_000),
            S(58000, 500_000, 1_000),
        };

        Assert.Equal(57500m, OptionChainAnalytics.MaxPain(strikes));
    }

    [Fact]
    public void Max_pain_moves_with_the_weight_of_open_interest()
    {
        var callHeavyLow = new[]
        {
            S(57000, 900_000, 1_000),
            S(57500, 1_000, 1_000),
            S(58000, 1_000, 900_000),
        };

        // Everything written is out of the money at the middle strike.
        Assert.Equal(57500m, OptionChainAnalytics.MaxPain(callHeavyLow));
    }

    [Fact]
    public void An_empty_chain_has_no_max_pain()
    {
        Assert.Null(OptionChainAnalytics.MaxPain(Array.Empty<OptionChainAnalytics.StrikeInterest>()));
        Assert.Null(OptionChainAnalytics.MaxPain(new[] { S(57000, 0, 0), S(57500, 0, 0) }));
    }

    // --- heaviest strike and ATM -----------------------------------------

    [Fact]
    public void The_heaviest_strike_is_read_per_side()
    {
        var strikes = new[]
        {
            S(57000, 10_000, 900_000),
            S(57500, 50_000, 20_000),
            S(58000, 800_000, 5_000),
        };

        Assert.Equal(58000m, OptionChainAnalytics.HeaviestStrike(strikes, "CE"));
        Assert.Equal(57000m, OptionChainAnalytics.HeaviestStrike(strikes, "PE"));
    }

    [Fact]
    public void The_at_the_money_strike_is_the_nearest_one()
    {
        var strikes = new[] { 57200m, 57300m, 57400m, 57500m };

        Assert.Equal(57400m, OptionChainAnalytics.AtTheMoney(strikes, 57369.65m));
        Assert.Equal(57300m, OptionChainAnalytics.AtTheMoney(strikes, 57320m));
    }

    [Fact]
    public void A_spot_exactly_between_two_strikes_picks_the_lower_one_deterministically()
    {
        // Ties must not depend on enumeration order: the chain would jump
        // between two rows on consecutive refreshes at the same price.
        var strikes = new[] { 57400m, 57300m };
        Assert.Equal(57300m, OptionChainAnalytics.AtTheMoney(strikes, 57350m));
        Assert.Equal(57300m, OptionChainAnalytics.AtTheMoney(strikes.Reverse().ToArray(), 57350m));
    }

    [Fact]
    public void Fractional_stock_strikes_work_the_same_way()
    {
        var strikes = new[] { 100m, 102.5m, 105m };
        Assert.Equal(102.5m, OptionChainAnalytics.AtTheMoney(strikes, 103m));
    }
}
