using AlgoTrading.Infrastructure.Services.OptionHistory;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Black-Scholes greeks an open option leg shows when neither the feed nor
/// the chain recorder priced it (27 Sep: "the Greeks' effect should show too —
/// like theta shows when you buy").
///
/// Pinned against the textbook case (Hull): S = 100, K = 100, T = 1 year,
/// r = 5%, σ = 20%, no dividend. Day count: CALENDAR, 365 days a year — theta
/// is the annual figure / 365, which is what py_vollib (the feed's enricher and
/// the chain poller) publishes, so a leg's theta reads the same whichever
/// source answered. Vega is per one point of IV (per 1%).
/// </summary>
public class OptionGreeksTests
{
    private const double S = 100, K = 100, T = 1.0, R = 0.05, Sigma = 0.20;

    [Fact]
    public void A_call_matches_the_textbook_values()
    {
        var call = OptionMath.Greeks(isCall: true, S, K, Sigma, T, R)!;

        Assert.Equal(10.4506, call.Price, 4);
        Assert.Equal(0.6368, call.Delta, 4);
        Assert.Equal(0.01876, call.Gamma, 5);
        Assert.Equal(0.3752, call.VegaPerVolPoint, 4);
        // −6.4140 a year / 365 calendar days.
        Assert.Equal(-0.01757, call.ThetaPerDay, 5);
    }

    [Fact]
    public void A_put_matches_the_textbook_values()
    {
        var put = OptionMath.Greeks(isCall: false, S, K, Sigma, T, R)!;

        Assert.Equal(5.5735, put.Price, 4);
        Assert.Equal(-0.3632, put.Delta, 4);
        // Gamma and vega do not depend on the side.
        Assert.Equal(0.01876, put.Gamma, 5);
        Assert.Equal(0.3752, put.VegaPerVolPoint, 4);
        // −1.6579 a year / 365: a put's carry offsets part of its decay.
        Assert.Equal(-0.00454, put.ThetaPerDay, 5);
    }

    [Fact]
    public void Put_call_parity_holds()
    {
        var call = OptionMath.Greeks(true, S, K, Sigma, T, R)!;
        var put = OptionMath.Greeks(false, S, K, Sigma, T, R)!;

        Assert.Equal(S - K * Math.Exp(-R * T), call.Price - put.Price, 6);
    }

    [Fact]
    public void An_option_on_a_future_is_priced_as_Black_76()
    {
        // An MCX option is written on a future, which costs nothing to hold:
        // carry = r. Parity is then C − P = e^(−rT)(F − K), and a call's delta
        // is discounted — pricing the future as a spot would overstate both.
        const double future = 6500, strike = 6400, years = 20.0 / 365.0, sigma = 0.35;
        var call = OptionMath.Greeks(true, future, strike, sigma, years, R, carry: R)!;
        var put = OptionMath.Greeks(false, future, strike, sigma, years, R, carry: R)!;

        Assert.Equal(Math.Exp(-R * years) * (future - strike), call.Price - put.Price, 6);
        Assert.Equal(Math.Exp(-R * years), call.Delta - put.Delta, 6);

        var asIfSpot = OptionMath.Greeks(true, future, strike, sigma, years, R)!;
        Assert.True(asIfSpot.Price > call.Price);
    }

    [Theory]
    [InlineData(true, 0.20)]
    [InlineData(false, 0.20)]
    [InlineData(true, 0.65)]
    [InlineData(false, 0.08)]
    public void The_implied_volatility_of_a_price_gives_the_price_back(bool isCall, double sigma)
    {
        var priced = OptionMath.Greeks(isCall, S, K, sigma, T, R)!;
        var solved = OptionMath.ImpliedVolatility(isCall, priced.Price, S, K, T, R);

        Assert.NotNull(solved);
        Assert.Equal(sigma, solved!.Value, 6);
    }

    [Fact]
    public void A_short_dated_index_option_round_trips_too()
    {
        // NIFTY two days out: the case a carried weekly is in on Monday.
        const double spot = 24650, strike = 24700, years = 2.0 / 365.0, sigma = 0.13;
        var priced = OptionMath.Greeks(false, spot, strike, sigma, years, OptionMath.LiveRiskFreeRate)!;
        var solved = OptionMath.ImpliedVolatility(false, priced.Price, spot, strike, years, OptionMath.LiveRiskFreeRate);

        Assert.Equal(sigma, solved!.Value, 5);
    }

    [Fact]
    public void No_volatility_explains_a_price_with_no_time_value()
    {
        // S 110, K 100: worth at least 10 today. A trade at 9.50 (or at the
        // intrinsic itself) has no time value for any IV to explain.
        Assert.Null(OptionMath.ImpliedVolatility(true, 9.50, 110, 100, 0.1, R));
        Assert.Null(OptionMath.ImpliedVolatility(true, 10.0, 110, 100, 0.1, R));
        // Above the underlying itself: no call can cost that.
        Assert.Null(OptionMath.ImpliedVolatility(true, 120, 110, 100, 0.1, R));
        // No time left, no price, no underlying.
        Assert.Null(OptionMath.ImpliedVolatility(true, 5, 100, 100, 0, R));
        Assert.Null(OptionMath.ImpliedVolatility(true, 0, 100, 100, 0.1, R));
        Assert.Null(OptionMath.ImpliedVolatility(true, 5, 0, 100, 0.1, R));
    }

    [Fact]
    public void Inputs_that_define_no_option_give_no_greeks()
    {
        Assert.Null(OptionMath.Greeks(true, 100, 100, 0, 1, R));
        Assert.Null(OptionMath.Greeks(true, 100, 100, 0.2, 0, R));
        Assert.Null(OptionMath.Greeks(true, 0, 100, 0.2, 1, R));
        Assert.Null(OptionMath.Greeks(true, 100, 0, 0.2, 1, R));
    }

    [Fact]
    public void The_live_rate_is_the_one_the_poller_prices_at()
    {
        // core/greeks_calculator.py's default, used by the enricher and the
        // chain poller. A different rate here would make the same leg's theta
        // jump when its source changed.
        Assert.Equal(0.05, OptionMath.LiveRiskFreeRate);
    }
}
