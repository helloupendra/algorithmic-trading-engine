namespace AlgoTrading.Domain.Entities;

/// <summary>
/// How broad a session's move was across NSE's cash market: how many stocks
/// rose, fell and made new 52-week highs and lows.
/// </summary>
/// <remarks>
/// <para>
/// Counted over the EQ series of NSE's cash-market bhavcopy for the day, which
/// is what NSE counts as the equity segment (it includes the ETFs listed in
/// that series; SME, trade-for-trade and bond series are left out). A stock
/// advanced when its close is above its previous close, as the bhavcopy gives
/// both.
/// </para>
/// <para>
/// The 52-week counts compare the day's high and low with NSE's own
/// corporate-action-adjusted 52-week high and low file effective for that
/// day, which covers the 52 weeks before it. Raw bhavcopy prices are not
/// adjusted, so a stock after a 1:10 split would read as a new 52-week low;
/// NSE's file avoids that. Null when that file was not available.
/// </para>
/// <para>
/// A day's row describes its session, published after the 15:30 close: a
/// forecast may use only rows with <see cref="Date"/> before its session.
/// </para>
/// </remarks>
public class MarketBreadthDaily
{
    public long Id { get; set; }

    /// <summary>"NSE".</summary>
    public string Exchange { get; set; } = "NSE";

    /// <summary>The session, IST.</summary>
    public DateOnly Date { get; set; }

    public int Advances { get; set; }
    public int Declines { get; set; }
    public int Unchanged { get; set; }

    /// <summary>EQ-series securities that traded.</summary>
    public int Traded { get; set; }

    /// <summary>Their total traded value, in rupees crore.</summary>
    public decimal? TurnoverCr { get; set; }

    /// <summary>Stocks whose high beat their adjusted 52-week high; null when NSE's 52-week file was not available.</summary>
    public int? Highs52w { get; set; }

    /// <summary>Stocks whose low went under their adjusted 52-week low; null when NSE's 52-week file was not available.</summary>
    public int? Lows52w { get; set; }

    /// <summary>Which bhavcopy format the row was read from: "nse-cm-udiff" or "nse-cm-legacy".</summary>
    public string Source { get; set; } = string.Empty;
}
