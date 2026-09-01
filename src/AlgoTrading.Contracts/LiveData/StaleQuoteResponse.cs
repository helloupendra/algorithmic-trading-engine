using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{

    /// <summary>
    /// Data Transfer Object used to report symbols whose live quotes have not updated recently.
    /// Helps identify dropped subscriptions or inactive markets.
    /// </summary>
    public class StaleQuoteResponse
    {
        /// <summary>
        /// The trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Type of the quote.
        /// </summary>
        public string DataType { get; set; } = string.Empty;

        /// <summary>
        /// The last known traded price before the quote went stale.
        /// </summary>
        public decimal? LastTradedPrice { get; set; }

        /// <summary>
        /// The UTC timestamp of when it was last successfully updated.
        /// </summary>
        public DateTime UpdatedUtc { get; set; }

        /// <summary>
        /// How many seconds have passed since the last update.
        /// </summary>
        public int AgeSeconds { get; set; }
    }

}
