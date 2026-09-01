using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{

    /// <summary>
    /// Data Transfer Object used by the background ingestor to update the latest quote for a symbol.
    /// Overwrites the existing `LiveQuoteLatest` record in the database.
    /// </summary>
    public class UpsertLiveQuoteRequest
    {
        /// <summary>
        /// The trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The type of update.
        /// </summary>
        public string DataType { get; set; } = "symbolUpdate";

        /// <summary>
        /// Last traded price.
        /// </summary>
        public decimal? LastTradedPrice { get; set; }

        /// <summary>
        /// Daily open.
        /// </summary>
        public decimal? Open { get; set; }

        /// <summary>
        /// Daily high.
        /// </summary>
        public decimal? High { get; set; }

        /// <summary>
        /// Daily low.
        /// </summary>
        public decimal? Low { get; set; }

        /// <summary>
        /// Previous day close.
        /// </summary>
        public decimal? Close { get; set; }

        /// <summary>
        /// Total volume.
        /// </summary>
        public long? Volume { get; set; }

        /// <summary>
        /// The raw JSON from the broker for debugging.
        /// </summary>
        public string RawPayload { get; set; } = string.Empty;
    }

}
