// src/AlgoTrading.Contracts/LiveData/MarketTickArchiveRequest.cs
namespace AlgoTrading.Contracts.LiveData;

public class MarketTickArchiveRequest
{
    public string Symbol { get; set; } = string.Empty;
    public string DataType { get; set; } = "symbolUpdate";

    public DateTime? ExchangeTimestampUtc { get; set; }

    public decimal? LastTradedPrice { get; set; }
    public decimal? BidPrice { get; set; }
    public decimal? AskPrice { get; set; }

    public decimal? BidSize { get; set; }
    public decimal? AskSize { get; set; }

    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal? PrevClose { get; set; }

    public decimal? Volume { get; set; }

    public string RawPayload { get; set; } = string.Empty;

    /// <summary>Which connector produced this tick, e.g. "fyers".</summary>
    public string SourceKey { get; set; } = string.Empty;
}