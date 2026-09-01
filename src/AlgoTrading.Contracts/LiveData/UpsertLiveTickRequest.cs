using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{
    /// <summary>
    /// Data Transfer Object used by the background ingestor to append a new raw tick event to the database.
    /// </summary>
    public class UpsertLiveTickRequest
    {
        /// <summary>
        /// The trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Type of tick data.
        /// </summary>
        public string DataType { get; set; } = "symbolUpdate";

        /// <summary>
        /// Exchange timestamp, if parsed.
        /// </summary>
        public DateTime? ExchangeTimestampUtc { get; set; }

        /// <summary>
        /// Last traded price.
        /// </summary>
        public decimal? LastTradedPrice { get; set; }

        /// <summary>
        /// Top of book bid price.
        /// </summary>
        public decimal? BidPrice { get; set; }

        /// <summary>
        /// Top of book ask price.
        /// </summary>
        public decimal? AskPrice { get; set; }

        /// <summary>
        /// Quantity at the best bid.
        /// </summary>
        public long? BidSize { get; set; }

        /// <summary>
        /// Quantity at the best ask.
        /// </summary>
        public long? AskSize { get; set; }

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
        /// Previous close.
        /// </summary>
        public decimal? PrevClose { get; set; }

        /// <summary>
        /// Cumulative volume.
        /// </summary>
        public long? Volume { get; set; }

        /// <summary>
        /// Original broker JSON payload.
        /// </summary>
        public string RawPayload { get; set; } = string.Empty;
    }

}
