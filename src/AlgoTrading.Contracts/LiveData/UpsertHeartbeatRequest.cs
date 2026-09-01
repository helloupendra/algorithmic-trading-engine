using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{
    /// <summary>
    /// Data Transfer Object used by the background ingestor worker to ping the API 
    /// and report its health and active subscriptions.
    /// </summary>
    public class UpsertHeartbeatRequest
    {
        /// <summary>
        /// The name of the reporting ingestor (e.g., "FYERS_WEBSOCKET").
        /// </summary>
        public string SourceName { get; set; } = string.Empty;

        /// <summary>
        /// Its current operational status.
        /// </summary>
        public string Status { get; set; } = "Running";

        /// <summary>
        /// When it last successfully received data.
        /// </summary>
        public DateTime LastHeartbeatUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When it last synced its watchlist with the database.
        /// </summary>
        public DateTime? LastWatchlistRefreshUtc { get; set; }

        /// <summary>
        /// The list of symbols it currently holds active websocket streams for.
        /// </summary>
        public List<string> CurrentSubscribedSymbols { get; set; } = new();

        /// <summary>
        /// Any recent fatal exceptions encountered by the worker.
        /// </summary>
        public string LastError { get; set; } = string.Empty;
    }
}
