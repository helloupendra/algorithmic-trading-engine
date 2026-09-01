// src/AlgoTrading.Worker.MarketData/Models/MarketTickStreamMessage.cs
namespace AlgoTrading.Worker.MarketData.Models;

public class MarketTickStreamMessage
{
    public string Exchange { get; set; } = string.Empty;
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
    public decimal? Close { get; set; }

    public decimal? Volume { get; set; }

    public DateTime? ReceivedUtc { get; set; }
    public string RawPayload { get; set; } = string.Empty;
    public bool IsReplay { get; set; } = false;
}
