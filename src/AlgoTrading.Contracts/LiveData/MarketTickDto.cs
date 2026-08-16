// src/AlgoTrading.Contracts/LiveData/MarketTickDto.cs
namespace AlgoTrading.Contracts.LiveData;

public class MarketTickDto
{
    public long Id { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;

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

    public DateTime ReceivedUtc { get; set; }
}