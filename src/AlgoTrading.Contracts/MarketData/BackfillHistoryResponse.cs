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
        public int CandlesFetched { get; set; }

        /// <summary>
        /// Total number of candles now available locally for this symbol/resolution.
        /// </summary>
        public int LocalCandlesAvailable { get; set; }

        /// <summary>
        /// Trading days in the requested range that are still not fully
        /// covered after the backfill, as "yyyy-MM-dd (missing|partial)".
        /// </summary>
        /// <remarks>
        /// A caller that only reads <see cref="FullCoverageAfterBackfill"/>
        /// learns that something is wrong; this says what, so a backtest can
        /// name the days it cannot account for instead of quietly skipping
        /// them.
        /// </remarks>
        public List<string> RemainingGaps { get; set; } = new();

        /// <summary>
        /// Trading days expected in the requested range, and how many of them
        /// are fully covered.
        /// </summary>
        public int TradingDaysExpected { get; set; }

        /// <summary>See <see cref="TradingDaysExpected"/>.</summary>
        public int TradingDaysCovered { get; set; }

        /// <summary>
        /// General status or debug message.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
