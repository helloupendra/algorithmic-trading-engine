using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{
    /// <summary>
    /// Data Transfer Object to add or update a symbol in the live data feed watchlist.
    /// </summary>
    public class UpsertWatchlistItemRequest
    {
        /// <summary>
        /// The trading symbol to subscribe to.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The type of data to subscribe to (e.g., "symbolUpdate" or "depthUpdate").
        /// </summary>
        public string DataType { get; set; } = "symbolUpdate";

        /// <summary>
        /// Whether the stream should be active right now.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Sorting priority to prioritize important symbols during bulk subscriptions.
        /// </summary>
        public int Priority { get; set; } = 0;
    }
}
