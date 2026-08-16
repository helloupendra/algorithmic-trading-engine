using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.MarketData
{
    /// <summary>
    /// Data Transfer Object to manually trigger a synchronization of historical data from the broker.
    /// </summary>
    public class SyncHistoryRequest
    {
        /// <summary>
        /// The trading symbol to sync.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The timeframe (e.g., "1D", "1m").
        /// </summary>
        public string Resolution { get; set; } = "1D";

        /// <summary>
        /// Date formatting rule (often required by specific brokers).
        /// </summary>
        public int DateFormat { get; set; } = 1;

        /// <summary>
        /// Start date for the sync.
        /// </summary>
        public DateOnly FromDate { get; set; }

        /// <summary>
        /// End date for the sync.
        /// </summary>
        public DateOnly ToDate { get; set; }

        /// <summary>
        /// Continuous data flag.
        /// </summary>
        public int ContFlag { get; set; } = 1;
    }
}
