namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One futures contract's end-of-day numbers from NSE's F&amp;O bhavcopy: price,
/// open interest and the change in open interest over the day.
/// </summary>
/// <remarks>
/// Price change and OI change together are the build-up a desk reads (long
/// build-up, short build-up, short covering, long unwinding). The close OI is
/// also the baseline a live futures quote's OI is compared with during the next
/// session, since no vendor quote carries the previous day's OI.
/// </remarks>
public class MarketFuturesDaily
{
    public long Id { get; set; }

    /// <summary>The trading day, in IST.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The underlying as NSE writes it: "NIFTY", "BANKNIFTY", "RELIANCE"...</summary>
    public string Underlying { get; set; } = string.Empty;

    /// <summary>"IDF" for an index future, "STF" for a stock future.</summary>
    public string InstrumentKind { get; set; } = string.Empty;

    public DateOnly ExpiryDate { get; set; }

    public decimal Close { get; set; }
    public decimal PreviousClose { get; set; }
    public decimal SettlementPrice { get; set; }

    /// <summary>The underlying's price NSE printed next to the contract.</summary>
    public decimal? UnderlyingPrice { get; set; }

    /// <summary>Contracts open at the close (quantity, as NSE reports it).</summary>
    public long OpenInterest { get; set; }

    /// <summary>Change in open interest over the day, as NSE reports it.</summary>
    public long OpenInterestChange { get; set; }

    /// <summary>Quantity traded.</summary>
    public long Volume { get; set; }

    /// <summary>Traded value in rupees.</summary>
    public decimal TurnoverValue { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;
}
