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

        /// <summary>
        /// The ingestor's OS process id, so the API can find and stop it after
        /// an API restart. Optional; older ingestors do not send it.
        /// </summary>
        public int? ProcessId { get; set; }

        /// <summary>
        /// The connector key of the feed sending this ("fyers", "truedata"), so its
        /// process id is recorded under that feed and nowhere else. Absent from an
        /// ingestor older than per-vendor feeds, which was always FYERS.
        /// </summary>
        public string? FeedKey { get; set; }

        // What the feed could not deliver, as counted by the feed since it
        // started. The feed sent the first two from 2026-09-07 ("backlog, made
        // visible") and model binding dropped both without a word, because a
        // property that does not exist here is not an error — the same trap that
        // once lost every greek on the tick DTO. Optional: older feeds omit them.

        /// <summary>Ticks buffered in the feed and not yet posted to the API.</summary>
        public int? QueueDepth { get; set; }

        /// <summary>Ticks the feed's full buffer shed (the oldest go first).</summary>
        public long? TicksDropped { get; set; }

        /// <summary>Ticks in batches the API refused or never answered: not in the tables.</summary>
        public long? TicksNotStored { get; set; }

        /// <summary>Ticks the feed could not handle at all: neither published to strategies nor stored.</summary>
        public long? TicksRejected { get; set; }

        /// <summary>
        /// Why option ticks carry no IV or greeks, when the pricing library cannot
        /// be loaded; empty when greeks are being computed.
        /// </summary>
        public string? GreeksUnavailable { get; set; }
    }
}
