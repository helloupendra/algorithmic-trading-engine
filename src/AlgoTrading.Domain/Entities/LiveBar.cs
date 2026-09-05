// src/AlgoTrading.Domain/Entities/LiveBar.cs
namespace AlgoTrading.Domain.Entities;

/// <summary>
/// Represents a consolidated candlestick built in real-time by the Live Data Ingestion worker.
/// Strategies consume these bars to execute logic without processing raw tick data.
/// </summary>
public class LiveBar
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The unique trading symbol (e.g., "NSE:BANKNIFTY-INDEX").
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// The timeframe resolution (e.g., "1m"). Currently, the system primarily builds 1-minute bars.
    /// </summary>
    public string Resolution { get; set; } = "1m";

    /// <summary>
    /// The UTC timestamp representing the start of the timeframe.
    /// </summary>
    public DateTime BarStartUtc { get; set; }

    /// <summary>
    /// The opening price of the current bar.
    /// </summary>
    public decimal Open { get; set; }

    /// <summary>
    /// The highest price traded during the current bar.
    /// </summary>
    public decimal High { get; set; }

    /// <summary>
    /// The lowest price traded during the current bar.
    /// </summary>
    public decimal Low { get; set; }

    /// <summary>
    /// The most recent closing/last-traded price within the current bar.
    /// </summary>
    public decimal Close { get; set; }

    /// <summary>
    /// The incremental volume traded during this specific bar 
    /// (derived by subtracting the previous cumulative volume from the current cumulative volume).
    /// </summary>
    public long VolumeDelta { get; set; }

    /// <summary>
    /// The number of raw ticks that have been aggregated into this bar so far.
    /// </summary>
    public int TickCount { get; set; }

    /// <summary>
    /// The timestamp of the last tick that updated this bar.
    /// </summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Which connector's ticks this bar was aggregated from, e.g. "fyers".</summary>
    public string SourceKey { get; set; } = string.Empty;
}