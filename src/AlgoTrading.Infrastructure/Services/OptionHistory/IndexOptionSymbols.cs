using System.Globalization;

namespace AlgoTrading.Infrastructure.Services.OptionHistory;

/// <summary>
/// Writes index option symbols in the FYERS grammar the instrument master uses, for
/// contracts that expired before the master was ever imported.
/// </summary>
/// <remarks>
/// <para>Weekly: <c>NSE:NIFTY2691519050CE</c> (yy, month 1–9 or O/N/D, dd, strike, side).</para>
/// <para>Monthly, the month's last expiry: <c>NSE:NIFTY26SEP19050CE</c>.</para>
/// <para>SENSEX and BANKEX trade on BSE, everything else on NSE. <see cref="UnderlyingCatalog.ParseOptionSymbol"/>
/// reads both forms back.</para>
/// </remarks>
public static class IndexOptionSymbols
{
    private static readonly string[] Months =
        { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

    public static string Exchange(string underlying) =>
        underlying.Trim().ToUpperInvariant() is "SENSEX" or "BANKEX" or "SENSEX50" ? "BSE" : "NSE";

    public static string Format(string underlying, DateOnly expiry, decimal strike, string optionType, bool monthly)
    {
        string name = underlying.Trim().ToUpperInvariant();
        string side = optionType.Trim().ToUpperInvariant();
        string strikeText = strike == decimal.Truncate(strike)
            ? decimal.Truncate(strike).ToString(CultureInfo.InvariantCulture)
            : strike.ToString("0.##", CultureInfo.InvariantCulture);
        string yy = (expiry.Year % 100).ToString("00", CultureInfo.InvariantCulture);
        string code = monthly
            ? Months[expiry.Month - 1]
            : (expiry.Month switch { 10 => "O", 11 => "N", 12 => "D", var m => m.ToString(CultureInfo.InvariantCulture) })
              + expiry.Day.ToString("00", CultureInfo.InvariantCulture);
        return $"{Exchange(name)}:{name}{yy}{code}{strikeText}{side}";
    }
}
