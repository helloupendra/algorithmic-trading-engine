namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A price of GIFT Nifty or an overseas market as it stood at one moment of
/// the Indian morning, so a forecast can be replayed with exactly what was
/// known at 08:50 IST.
/// </summary>
/// <remarks>
/// Taken every 15 minutes from 06:00 to 16:00 IST on weekdays, including
/// 08:45. <see cref="FetchedUtc"/> is the point-in-time stamp; a model at
/// 08:50 IST reads, per key, the latest row fetched before 08:50 that day.
/// <see cref="AsOfUtc"/> is the source's own quote time, which can be hours
/// old for a market that is closed; it says how fresh the price was, not when
/// the desk knew it.
/// </remarks>
public class MarketQuoteSnapshot
{
    public long Id { get; set; }

    /// <summary>"GIFTNIFTY", or one of the global daily symbol keys (SPX, ES, BRENT...).</summary>
    public string Key { get; set; } = string.Empty;

    public decimal Price { get; set; }

    public decimal? PreviousClose { get; set; }

    /// <summary>Change from the previous close, in percent; null when the source gave no previous close.</summary>
    public decimal? ChangePct { get; set; }

    /// <summary>The source's quote time, in UTC; null when it did not say.</summary>
    public DateTime? AsOfUtc { get; set; }

    /// <summary>When the desk took the snapshot.</summary>
    public DateTime FetchedUtc { get; set; }

    /// <summary>Where the price came from, e.g. "nseix" or "yahoo:^GSPC".</summary>
    public string Source { get; set; } = string.Empty;
}
