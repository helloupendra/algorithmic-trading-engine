using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Simulator
{
    /// <summary>
    /// Data Transfer Object representing a single historical bar fed into the strategy runner during an offline replay.
    /// </summary>
    public class ReplayBarFrame
    {
        /// <summary>
        /// The trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The timeframe (e.g., "1m").
        /// </summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>
        /// The UTC timestamp of the start of the bar.
        /// </summary>
        public DateTime TimestampUtc { get; set; }

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
        /// Close price.
        /// </summary>
        public decimal Close { get; set; }

        /// <summary>
        /// Traded volume.
        /// </summary>
        public long Volume { get; set; }
    }
}
