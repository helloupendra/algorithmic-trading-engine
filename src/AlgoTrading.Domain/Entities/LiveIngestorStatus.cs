using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Domain.Entities
{
    /// <summary>
    /// Tracks the health and operational status of a background market data ingestor.
    /// Used by health checks or dashboard UI to see if the websocket feed is alive.
    /// </summary>
    public class LiveIngestorStatus
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public long Id { get; set; }
        /// <summary>
        /// The name of the ingestor service or broker (e.g., "FYERS_WEBSOCKET").
        /// </summary>
        public string SourceName { get; set; } = string.Empty;

        /// <summary>
        /// The current lifecycle status (e.g., "Starting", "Running", "Stopped", "Error").
        /// </summary>
        public string Status { get; set; } = "Starting";

        /// <summary>
        /// The UTC timestamp of the last successful data packet or heartbeat received.
        /// </summary>
        public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The UTC timestamp when the ingestor last synchronized its active symbol subscriptions with the database.
        /// </summary>
        public DateTime? LastWatchlistRefreshUtc { get; set; }

        /// <summary>
        /// A JSON serialized array of symbols the ingestor is currently streaming.
        /// </summary>
        public string CurrentSubscribedSymbolsJson { get; set; } = "[]";

        /// <summary>
        /// Contains the exception message or reason if the ingestor enters an error state.
        /// </summary>
        public string LastError { get; set; } = string.Empty;

        /// <summary>
        /// When this status record was last updated.
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
