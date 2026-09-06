namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One strike's state at one moment: what it cost, how much traded, and how
/// much open interest stood behind it.
/// </summary>
/// <remarks>
/// This table is the keystone of the option-chain work, and it exists because
/// nothing in the platform recorded open interest. The broker's tick feed does
/// not carry OI at all — a live <c>SymbolUpdate</c> message has ltp, bid, ask,
/// volume and the day's OHLC, and no open interest of any kind — and neither
/// <see cref="Candle"/> nor <see cref="LiveBar"/> has a column for it.
/// <para>
/// Three separate things become possible once a row lands here every few
/// seconds, and none of them is possible without it:
/// </para>
/// <list type="bullet">
/// <item>the chain itself, which is the newest row per strike;</item>
/// <item>the intraday OI-change curves, which are a day of rows for one strike;</item>
/// <item>replaying either of those inside a backtest, which is the same query
/// with the clock moved back.</item>
/// </list>
/// <para>
/// The last one is why this is written eagerly rather than derived later:
/// history cannot be reconstructed after the fact. A strike's open interest at
/// 10:15 this morning is knowable only if something wrote it down at 10:15.
/// </para>
/// </remarks>
public class OptionChainSnapshot
{
    public long Id { get; set; }

    /// <summary>"BANKNIFTY", "NIFTY", "SENSEX"…</summary>
    public string Underlying { get; set; } = string.Empty;

    /// <summary>The contract's expiry, so several expiries can be tracked at once.</summary>
    public DateOnly ExpiryDate { get; set; }

    /// <summary>Fractional on stock grids, so not an integer.</summary>
    public decimal StrikePrice { get; set; }

    /// <summary>"CE" or "PE".</summary>
    public string OptionType { get; set; } = string.Empty;

    /// <summary>The broker symbol this row was read from.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>When the snapshot was taken, to the second.</summary>
    public DateTime CapturedUtc { get; set; }

    /// <summary>The underlying's spot at that moment — what makes a row readable later.</summary>
    public decimal SpotPrice { get; set; }

    public decimal? LastTradedPrice { get; set; }
    public decimal? BidPrice { get; set; }
    public decimal? AskPrice { get; set; }

    /// <summary>Cumulative volume for the session, as the exchange reports it.</summary>
    public long? Volume { get; set; }

    /// <summary>Open interest outstanding. The number none of the other tables held.</summary>
    public long? OpenInterest { get; set; }

    /// <summary>
    /// Open interest at the session's first snapshot for this contract.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed: "OI change" means change since the day
    /// opened, and finding the day's first row for every strike on every read
    /// would turn a cheap query into a scan. Written once per contract per day.
    /// </remarks>
    public long? OpenInterestAtOpen { get; set; }

    /// <summary>Implied volatility as a fraction (0.185 is 18.5%).</summary>
    public decimal? ImpliedVolatility { get; set; }

    public decimal? Delta { get; set; }
    public decimal? Gamma { get; set; }
    public decimal? Theta { get; set; }
    public decimal? Vega { get; set; }

    /// <summary>Which connector produced this, e.g. "fyers".</summary>
    public string SourceKey { get; set; } = string.Empty;
}
