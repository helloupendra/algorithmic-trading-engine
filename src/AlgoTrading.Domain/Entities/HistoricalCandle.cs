// src/AlgoTrading.Domain/Entities/HistoricalCandle.cs
namespace AlgoTrading.Domain.Entities;

public class HistoricalCandle
{
    public long Id { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;

    public DateTime TimestampUtc { get; set; }

    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }

    public decimal Volume { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}