namespace AlgoTrading.Infrastructure.Services.OptionHistory;

/// <summary>
/// Black-Scholes for the platform's option numbers: the delta the stored
/// history lacks, and the full set of greeks an open position shows.
/// </summary>
/// <remarks>
/// Dhan's expired-options history gives the premium, open interest and implied
/// volatility of each strike, but no greeks. A strategy that picks its leg by
/// delta (ChainFlowBuy asks for about 0.5) therefore cannot replay without one,
/// so delta is computed from Black-Scholes on the IV that came with the bar.
/// It is the market's own IV put back through the model the market quoted it
/// from, not a guess: with the same spot, strike and time to expiry it returns
/// what a chain would show, to a rounding.
/// <para>
/// The live position view uses the same model the other way round (27 Sep:
/// "if I have bought an option, the Greeks' effect should show too — like
/// theta"): when neither the feed nor the chain recorder priced a contract,
/// its IV is solved from the option's own last price and the greeks follow
/// from that IV. Conventions are the ones the feed's enricher and the chain
/// poller already publish (py_vollib's analytical greeks), so a figure reads
/// the same whichever of the three produced it: theta per CALENDAR day
/// (the annual figure / 365), vega per one point of IV (per 1%), both in
/// premium points.
/// </para>
/// </remarks>
public static class OptionMath
{
    /// <summary>India's risk-free rate for this purpose; delta barely moves with it intraday.</summary>
    public const double RiskFreeRate = 0.065;

    /// <summary>
    /// The rate the LIVE greeks are priced at: 5%, the default of
    /// <c>core/greeks_calculator.py</c>, which both the feed's enricher and the
    /// chain poller call without overriding it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="RiskFreeRate"/>. A position's greeks can come from the
    /// feed, from the chain recorder or from here, and one leg showing a
    /// different theta depending on which of them answered would read as the
    /// market moving when only the source changed. The history's 6.5% is left
    /// alone: backtests have been replayed with it.
    /// </remarks>
    public const double LiveRiskFreeRate = 0.05;

    /// <summary>The widest IV the solver will report, as the poller does (500%). Past it the price is noise.</summary>
    public const double MaxVolatility = 5.0;

    /// <summary>The narrowest IV the solver searches from (0.1%).</summary>
    private const double MinVolatility = 0.001;

    /// <summary>
    /// Black-Scholes delta of a call (+) or a put (−). Null when the inputs
    /// cannot define one: no volatility, no time left, or a nonsense price.
    /// </summary>
    public static decimal? Delta(bool isCall, double spot, double strike, double ivPercent, double yearsToExpiry)
    {
        if (spot <= 0 || strike <= 0 || ivPercent <= 0 || yearsToExpiry <= 0) return null;
        double sigma = ivPercent / 100.0;
        double d1 = (Math.Log(spot / strike) + (RiskFreeRate + 0.5 * sigma * sigma) * yearsToExpiry)
                    / (sigma * Math.Sqrt(yearsToExpiry));
        double call = NormalCdf(d1);
        return (decimal)Math.Round(isCall ? call : call - 1.0, 4);
    }

    /// <summary>Years between two instants, by the 365-day count options are quoted on.</summary>
    public static double YearsBetween(DateTime fromUtc, DateTime toUtc) =>
        Math.Max(0.0, (toUtc - fromUtc).TotalDays / 365.0);

    /// <summary>
    /// Price and greeks of one option under Black-Scholes-Merton, per unit of
    /// the underlying. Null when the inputs cannot define them.
    /// </summary>
    /// <param name="isCall">True for a CE, false for a PE.</param>
    /// <param name="underlying">The underlying's price: an index's spot, or the future an MCX option is written on.</param>
    /// <param name="strike">The strike.</param>
    /// <param name="sigma">Volatility as a fraction (0.20 is 20%).</param>
    /// <param name="years">Time to expiry in years, calendar 365-day count (<see cref="YearsBetween"/>).</param>
    /// <param name="rate">Risk-free rate as a fraction.</param>
    /// <param name="carry">
    /// Continuous yield of the underlying. 0 for an index priced off its spot;
    /// equal to <paramref name="rate"/> for an option on a FUTURE, which turns
    /// this into Black-76 — a future costs nothing to hold, and pricing an MCX
    /// option as if its future were a spot overstates every call.
    /// </param>
    public static OptionGreeks? Greeks(bool isCall, double underlying, double strike, double sigma, double years, double rate, double carry = 0.0)
    {
        if (!(underlying > 0) || !(strike > 0) || !(sigma > 0) || !(years > 0)) return null;
        if (double.IsNaN(rate) || double.IsNaN(carry)) return null;

        double sqrtT = Math.Sqrt(years);
        double d1 = (Math.Log(underlying / strike) + (rate - carry + 0.5 * sigma * sigma) * years) / (sigma * sqrtT);
        double d2 = d1 - sigma * sqrtT;
        double carryDiscount = Math.Exp(-carry * years);
        double rateDiscount = Math.Exp(-rate * years);
        double pdf = NormalPdf(d1);

        double price, delta, thetaAnnual;
        if (isCall)
        {
            price = underlying * carryDiscount * NormalCdf(d1) - strike * rateDiscount * NormalCdf(d2);
            delta = carryDiscount * NormalCdf(d1);
            thetaAnnual = -underlying * carryDiscount * pdf * sigma / (2 * sqrtT)
                          - rate * strike * rateDiscount * NormalCdf(d2)
                          + carry * underlying * carryDiscount * NormalCdf(d1);
        }
        else
        {
            price = strike * rateDiscount * NormalCdf(-d2) - underlying * carryDiscount * NormalCdf(-d1);
            delta = carryDiscount * (NormalCdf(d1) - 1.0);
            thetaAnnual = -underlying * carryDiscount * pdf * sigma / (2 * sqrtT)
                          + rate * strike * rateDiscount * NormalCdf(-d2)
                          - carry * underlying * carryDiscount * NormalCdf(-d1);
        }

        double gamma = carryDiscount * pdf / (underlying * sigma * sqrtT);
        double vega = underlying * carryDiscount * pdf * sqrtT;

        return new OptionGreeks(
            Price: price,
            Delta: delta,
            Gamma: gamma,
            ThetaPerDay: thetaAnnual / 365.0,
            VegaPerVolPoint: vega / 100.0);
    }

    /// <summary>
    /// The volatility (a fraction) at which <see cref="Greeks"/> prices the
    /// option at <paramref name="price"/>. Null when no volatility can: the
    /// price is at or below what the option is worth with no time value at all,
    /// above what it could ever be worth, or the answer is past
    /// <see cref="MaxVolatility"/>.
    /// </summary>
    /// <remarks>
    /// Bisection, not Newton: an option price is monotonic in volatility, so
    /// halving a bracket always converges, where Newton's step divides by vega
    /// and runs away on a deep in- or out-of-the-money strike whose vega is
    /// nearly zero. Sixty halvings of [0.001, 5] is far below a paisa.
    /// </remarks>
    public static double? ImpliedVolatility(bool isCall, double price, double underlying, double strike, double years, double rate, double carry = 0.0)
    {
        if (!(price > 0) || !(underlying > 0) || !(strike > 0) || !(years > 0)) return null;

        double low = MinVolatility, high = MaxVolatility;
        var atLow = Greeks(isCall, underlying, strike, low, years, rate, carry);
        var atHigh = Greeks(isCall, underlying, strike, high, years, rate, carry);
        if (atLow is null || atHigh is null) return null;

        // At the floor the price is (almost) all intrinsic. A trade at or under
        // it has no time value to explain, so any IV would be a fiction.
        if (price <= atLow.Price || price >= atHigh.Price) return null;

        for (int i = 0; i < 60; i++)
        {
            double mid = 0.5 * (low + high);
            var atMid = Greeks(isCall, underlying, strike, mid, years, rate, carry);
            if (atMid is null) return null;
            if (atMid.Price > price) high = mid; else low = mid;
        }

        return 0.5 * (low + high);
    }

    /// <summary>The standard normal CDF (Abramowitz &amp; Stegun 7.1.26 on erf).</summary>
    private static double NormalCdf(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        double z = Math.Abs(x) / Math.Sqrt(2.0);
        double t = 1.0 / (1.0 + 0.3275911 * z);
        double y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t
                          + 0.254829592) * t * Math.Exp(-z * z);
        return 0.5 * (1.0 + sign * y);
    }

    private static double NormalPdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2.0 * Math.PI);
}

/// <summary>
/// One option's Black-Scholes value and greeks, per unit of the underlying.
/// Theta is per calendar day and vega per one point of IV, both in premium
/// points — the conventions every greek on the platform is published in.
/// </summary>
public sealed record OptionGreeks(double Price, double Delta, double Gamma, double ThetaPerDay, double VegaPerVolPoint);
