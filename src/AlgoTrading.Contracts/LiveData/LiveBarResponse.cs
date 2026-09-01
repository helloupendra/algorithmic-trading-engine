using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{
    /// <summary>
    /// Data Transfer Object representing a single real-time candle (bar).
    /// Used by strategies to evaluate entry/exit signals over recent intervals.
    /// </summary>
    public class LiveBarResponse
    {
        /// <summary>
        /// The trading symbol (e.g., "NSE:BANKNIFTY-INDEX").
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Timeframe of this bar (e.g., "1m").
        /// </summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>
        /// The timestamp when this specific bar interval started.
        /// </summary>
        public DateTime BarStartUtc { get; set; }

        /// <summary>
        /// Open price.
        /// </summary>
        public decimal Open { get; set; }

        /// <summary>
        /// High price.
        /// </summary>
        public decimal High { get; set; }

        /// <summary>
        /// Low price.
        /// </summary>
        public decimal Low { get; set; }

        /// <summary>
        /// Close price (or last traded price if the bar is incomplete).
        /// </summary>
        public decimal Close { get; set; }

        /// <summary>
        /// The volume traded during this specific bar's timeframe.
        /// </summary>
        public long VolumeDelta { get; set; }

        /// <summary>
        /// Number of individual ticks that formed this bar.
        /// </summary>
        public int TickCount { get; set; }

        /// <summary>
        /// The timestamp when this bar was last updated with a new tick.
        /// </summary>
        public DateTime UpdatedUtc { get; set; }
    }
}
