using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{

    /// <summary>
    /// Data Transfer Object representing the health and status of a market data ingestor.
    /// Used by dashboard APIs to display feed connectivity.
    /// </summary>
    public class IngestorStatusResponse
    {
        /// <summary>
        /// The name of the ingestor (e.g., "FYERS_WEBSOCKET").
        /// </summary>
        public string SourceName { get; set; } = string.Empty;

        /// <summary>
        /// The current status (e.g., "Running").
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// When the last heartbeat or message was received from the exchange.
        /// </summary>
        public DateTime LastHeartbeatUtc { get; set; }

        /// <summary>
        /// When the ingestor last verified its active subscriptions against the database.
        /// </summary>
        public DateTime? LastWatchlistRefreshUtc { get; set; }

        /// <summary>
        /// The list of symbols currently being streamed.
        /// </summary>
        public List<string> CurrentSubscribedSymbols { get; set; } = new();

        /// <summary>
        /// Exception text if the ingestor is in a faulted state.
        /// </summary>
        public string LastError { get; set; } = string.Empty;

        /// <summary>
        /// When this status was retrieved.
        /// </summary>
        public DateTime UpdatedUtc { get; set; }

        /// <summary>
        /// Computed property indicating if the feed is active and healthy based on heartbeats.
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// The ingestor's OS process id as last recorded (heartbeat or launch),
        /// or null when unknown.
        /// </summary>
        public int? ProcessId { get; set; }
    }

}
