namespace AlgoTrading.Domain.Entities;

/// <summary>
/// What foreign (FII/FPI) and domestic (DII) institutions bought and sold in the
/// cash market on one trading day, in rupees crore, as NSE reports it each evening.
/// </summary>
/// <remarks>
/// NSE's endpoint answers only the latest day, so history exists from the day
/// the platform started collecting it; a day that was missed cannot be fetched
/// later from the same place.
/// </remarks>
public class MarketCashFlow
{
    public long Id { get; set; }

    /// <summary>The trading day, in IST, as the report dates it.</summary>
    public DateOnly Date { get; set; }

    /// <summary>"FII/FPI" or "DII", as NSE names them.</summary>
    public string Category { get; set; } = string.Empty;

    public decimal BuyValueCrore { get; set; }
    public decimal SellValueCrore { get; set; }

    /// <summary>Buy minus sell, as reported.</summary>
    public decimal NetValueCrore { get; set; }

    public string Source { get; set; } = string.Empty;

    public DateTime FetchedUtc { get; set; } = DateTime.UtcNow;
}
