// src/AlgoTrading.Contracts/Equities/EquityLatestQuoteResponse.cs
namespace AlgoTrading.Contracts.Equities;

public class EquityLatestQuoteResponse
{
    public string Symbol { get; set; } = string.Empty;

    public decimal? Weight { get; set; }

    public decimal? LastTradedPrice { get; set; }
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal? Close { get; set; }
    public decimal? Volume { get; set; }

    public DateTime? UpdatedUtc { get; set; }

    /// <summary>
    /// Helpful for frontend to know if live quote exists.
    /// </summary>
    public bool HasLiveData { get; set; }
}