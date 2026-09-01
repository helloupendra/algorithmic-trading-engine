using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{
    /// <summary>
    /// Tracks the progress and state of historical data synchronization for a given symbol and resolution.
    /// Ensures we don't unnecessarily download the same historical candles repeatedly.
    /// </summary>
    public class SymbolSyncState
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// The trading symbol (e.g., "NSE:BANKNIFTY-INDEX").
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The candle timeframe resolution (e.g., "1m").
        /// </summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>
        /// The oldest historical candle UTC timestamp successfully synced and stored locally.
        /// </summary>
        public DateTime? EarliestLocalCandleUtc { get; set; }

        /// <summary>
        /// The most recent historical candle UTC timestamp successfully synced and stored locally.
        /// </summary>
        public DateTime? LatestLocalCandleUtc { get; set; }

        /// <summary>
        /// When the last background sync operation occurred.
        /// </summary>
        public DateTime? LastHistoricalSyncUtc { get; set; }

        /// <summary>
        /// Current lifecycle status of the sync process (e.g., "NeverSynced", "Syncing", "Synced", "Failed").
        /// </summary>
        public string SyncStatus { get; set; } = "NeverSynced";

        /// <summary>
        /// Contains exception details if the last sync operation failed.
        /// </summary>
        public string LastError { get; set; } = string.Empty;

        /// <summary>
        /// When this record was last updated.
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
