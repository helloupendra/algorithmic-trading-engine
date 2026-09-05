using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{
    /// <summary>
    /// Represents a raw, single-event tick received directly from the market data feed (e.g., via websocket).
    /// Typically appended in an append-only log fashion for building bars or extreme high-frequency analysis.
    /// </summary>
    public class LiveTick
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// The trading symbol (e.g., "NSE:RELIANCE-EQ").
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Identifies the schema or type of data in this tick (e.g., "symbolUpdate", "depthUpdate").
        /// </summary>
        public string DataType { get; set; } = "symbolUpdate";

        /// <summary>
        /// When this tick was received by the application server.
        /// </summary>
        public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The timestamp assigned to this tick by the exchange itself, if available.
        /// </summary>
        public DateTime? ExchangeTimestampUtc { get; set; }

        /// <summary>
        /// The last traded price in this tick event.
        /// </summary>
        public decimal? LastTradedPrice { get; set; }

        /// <summary>
        /// Best bid price available.
        /// </summary>
        public decimal? BidPrice { get; set; }

        /// <summary>
        /// Best ask price available.
        /// </summary>
        public decimal? AskPrice { get; set; }

        /// <summary>
        /// Volume available at the best bid.
        /// </summary>
        public long? BidSize { get; set; }

        /// <summary>
        /// Volume available at the best ask.
        /// </summary>
        public long? AskSize { get; set; }

        /// <summary>
        /// The day's open price.
        /// </summary>
        public decimal? Open { get; set; }

        /// <summary>
        /// The day's highest price.
        /// </summary>
        public decimal? High { get; set; }

        /// <summary>
        /// The day's lowest price.
        /// </summary>
        public decimal? Low { get; set; }

        /// <summary>
        /// The previous day's close.
        /// </summary>
        public decimal? PrevClose { get; set; }

        /// <summary>
        /// Cumulative volume for the day up to this tick.
        /// </summary>
        public long? Volume { get; set; }

        /// <summary>
        /// Unparsed raw payload from the broker. Useful for replay or extracting missing fields later.
        /// </summary>
        public string RawPayload { get; set; } = string.Empty;

        /// <summary>
        /// Which connector produced this tick, e.g. "fyers". Data lineage: without
        /// it a second feed cannot run beside the first and a suspicious print
        /// cannot be attributed to a source.
        /// </summary>
        public string SourceKey { get; set; } = string.Empty;
    }
}
