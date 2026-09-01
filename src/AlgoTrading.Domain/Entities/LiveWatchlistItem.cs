using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Domain.Entities
{
    /// <summary>
    /// Represents a symbol that the system should actively subscribe to in the live data feed.
    /// The Ingestor worker periodically reads this list to maintain its active websocket subscriptions.
    /// </summary>
    public class LiveWatchlistItem
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// The symbol to subscribe to (e.g., "NSE:BANKNIFTY-INDEX").
        /// </summary>
        public String Symbol { get; set; } = string.Empty;
        /// <summary>
        /// The expected data subscription type (usually "symbolUpdate").
        /// </summary>
        public string DataType { get; set; } = "symbolUpdate";

        /// <summary>
        /// Whether the system should currently stream this symbol. If false, the ingestor will unsubscribe.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// A priority flag to ensure critical symbols (like indexes) are subscribed first.
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Creation timestamp.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last modified timestamp.
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    }
}
