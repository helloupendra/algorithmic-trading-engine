using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities;

/// <summary>
/// Represents a historical price bar (OHLCV) for a specific financial instrument over a defined timeframe.
/// Used extensively in backtesting and historical data analysis.
/// </summary>
public class Candle
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
    /// The timeframe resolution of the candle (e.g., "1m", "5m", "1D").
    /// </summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>
    /// The UTC timestamp indicating the start time of the candle.
    /// </summary>
    public DateTime TimeStampUtc { get; set; }

    /// <summary>
    /// The opening price at the start of the timeframe.
    /// </summary>
    public decimal Open { get; set; }

    /// <summary>
    /// The highest price reached during the timeframe.
    /// </summary>
    public decimal High { get; set; }

    /// <summary>
    /// The lowest price reached during the timeframe.
    /// </summary>
    public decimal Low { get; set; }

    /// <summary>
    /// The closing price at the end of the timeframe.
    /// </summary>
    public decimal Close { get; set; }

    /// <summary>
    /// The total volume of shares or contracts traded during this timeframe.
    /// </summary>
    public long Volume { get; set; }
}

