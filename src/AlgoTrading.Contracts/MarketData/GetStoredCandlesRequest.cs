using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.MarketData
{
    /// <summary>
    /// Data Transfer Object representing a request to fetch historical candles directly from the local database.
    /// </summary>
    public class GetStoredCandlesRequest
    {
        /// <summary>
        /// The trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The required timeframe resolution (e.g., "D" or "1").
        /// </summary>
        public string Resolution { get; set; } = "D";

        /// <summary>
        /// The start date constraint (optional).
        /// </summary>
        public DateOnly? FromDate { get; set; }

        /// <summary>
        /// The end date constraint (optional).
        /// </summary>
        public DateOnly? ToDate { get; set; }
    }
}
