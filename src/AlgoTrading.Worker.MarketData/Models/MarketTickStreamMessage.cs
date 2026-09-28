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

    // The feed publishes these on the stream (messaging/redis_publisher.py
    // normalize_tick). A property missing here is dropped without a word by the
    // deserializer — the same way the API's tick DTO once lost every greek.
    public decimal? OpenInterest { get; set; }
    public decimal? ImpliedVolatility { get; set; }
    public decimal? Delta { get; set; }
    public decimal? Gamma { get; set; }
    public decimal? Theta { get; set; }
    public decimal? Vega { get; set; }

    public string? SourceKey { get; set; }

    public DateTime? ReceivedUtc { get; set; }
    public string RawPayload { get; set; } = string.Empty;
    public bool IsReplay { get; set; } = false;
}
