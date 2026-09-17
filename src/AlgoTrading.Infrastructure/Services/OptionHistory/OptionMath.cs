namespace AlgoTrading.Infrastructure.Services.OptionHistory;

/// <summary>
/// The one number the stored option history does not carry: delta.
/// </summary>
/// <remarks>
/// Dhan's expired-options history gives the premium, open interest and implied
/// volatility of each strike, but no greeks. A strategy that picks its leg by
/// delta (ChainFlowBuy asks for about 0.5) therefore cannot replay without one,
/// so delta is computed from Black-Scholes on the IV that came with the bar.
/// It is the market's own IV put back through the model the market quoted it
/// from, not a guess: with the same spot, strike and time to expiry it returns
/// what a chain would show, to a rounding.
/// </remarks>
public static class OptionMath
{
    /// <summary>India's risk-free rate for this purpose; delta barely moves with it intraday.</summary>
    public const double RiskFreeRate = 0.065;

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
}
