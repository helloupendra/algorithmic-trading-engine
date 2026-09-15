namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One bar of an option series named by its distance from the money rather than
/// by contract: "NIFTY, nearest weekly expiry, one strike above ATM, call".
/// </summary>
/// <remarks>
/// Imported from Dhan's expired options endpoint, which is the only source of
/// past option premiums, OI and IV this platform has for dates before its own
/// chain recording began (2026-09-14). The contract behind a series changes as
/// the market moves: <see cref="Strike"/> is the strike that was at this offset
/// during this bar, and it can differ from the bar before.
/// <para>
/// The research harness reads this table directly. Its columns are a contract
/// with that code; change them only together with it.
/// </para>
/// </remarks>
public class OptionHistoryBar
{
    public long Id { get; set; }

    /// <summary>"NIFTY", "BANKNIFTY", "SENSEX"…</summary>
    public string Underlying { get; set; } = string.Empty;

    /// <summary>"WEEK" or "MONTH", as asked of the vendor.</summary>
    /// <remarks>
    /// Where no weekly contract exists (BANKNIFTY after November 2024), Dhan
    /// answers "WEEK" with the monthly series; the label is the request, not a
    /// promise about the contract.
    /// </remarks>
    public string ExpiryFlag { get; set; } = string.Empty;

    /// <summary>1 is the nearest expiry, 2 the next, 3 the one after.</summary>
    public int ExpiryCode { get; set; }

    /// <summary>The contract's expiry when it can be established; null otherwise.</summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>0 at the money, +1 one strike above, −1 one below.</summary>
    public int StrikeOffset { get; set; }

    /// <summary>The strike at this offset during this bar.</summary>
    public decimal Strike { get; set; }

    /// <summary>"CE" or "PE".</summary>
    public string OptionType { get; set; } = string.Empty;

    /// <summary>"1m", "5m", "15m", "60m".</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>When the bar opened, UTC.</summary>
    public DateTime BarStartUtc { get; set; }

    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }

    /// <summary>Contracts traded in the bar, in units (not lots).</summary>
    public long? Volume { get; set; }

    /// <summary>Open interest at the bar's end, in units; null when the vendor reported none.</summary>
    public long? OpenInterest { get; set; }

    /// <summary>Implied volatility in percent (12.96 is 12.96%); null when the vendor reported none.</summary>
    public decimal? ImpliedVolatility { get; set; }

    /// <summary>The underlying index at the bar's end.</summary>
    public decimal? SpotPrice { get; set; }

    /// <summary>Which connector produced this, e.g. "dhan".</summary>
    public string SourceKey { get; set; } = string.Empty;
}
