using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Contracts.MarketData
{
    /// <summary>
    /// Data Transfer Object representing the result of a historical backfill operation.
    /// Provides details on missing slices fetched and total candles processed.
    /// </summary>
    public class BackfillHistoryResponse
    {
        /// <summary>
        /// The trading symbol that was backfilled.
        /// </summary>
        public string Symbol { get; set; } =  string.Empty;

        /// <summary>
        /// The timeframe resolution.
        /// </summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>
        /// The requested start boundary.
        /// </summary>
        public DateOnly RequestedFromDate { get; set; }

        /// <summary>
        /// The requested end boundary.
        /// </summary>
        public DateOnly RequestedToDate { get; set; }

        /// <summary>
        /// Whether the instrument exists in the database.
        /// </summary>
        public bool InstrumentExists { get; set; }

        /// <summary>
        /// Whether the database now holds full coverage for the requested range.
        /// </summary>
        public bool FullCoverageAfterBackfill { get; set; }

        /// <summary>
        /// A list of time boundaries that were missing locally and had to be fetched from the broker.
        /// </summary>
        public List<string> MissingSlicesFetched { get; set; } = new();

        /// <summary>
        /// Total number of candles actually downloaded from the broker API.
        /// </summary>
        public int CandelsFetchedFromFyers { get; set; }

        /// <summary>
        /// Total number of candles now available locally for this symbol/resolution.
        /// </summary>
        public int LocalCandlesAvailable { get; set; }

        /// <summary>
        /// General status or debug message.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
