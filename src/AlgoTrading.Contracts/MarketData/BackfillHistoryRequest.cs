using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Contracts.MarketData
{
    /// <summary>
    /// Data Transfer Object representing a request to manually backfill historical candles from the broker.
    /// </summary>
    public class BackfillHistoryRequest
    {
        /// <summary>
        /// The symbol to fetch data for.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The timeframe (e.g., "1", "5", "1D").
        /// </summary>
        public string Resolution { get; set; } = "D";

        /// <summary>
        /// Start date of the backfill.
        /// </summary>
        public DateOnly FromDate { get; set; }

        /// <summary>
        /// End date of the backfill.
        /// </summary>
        public DateOnly ToDate { get; set; }

        /// <summary>
        /// Date format flag expected by the Fyers API (0 or 1).
        /// </summary>
        public int DateFormat { get; set; } = 1;

        /// <summary>
        /// Continuous flag expected by the Fyers API (0 or 1).
        /// </summary>
        public int ContFlag { get; set; } = 1;
    }
}
