using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{

    /// <summary>
    /// Data Transfer Object representing a raw tick received from the websocket.
    /// Used for returning tick history or streaming tick events to the frontend.
    /// </summary>
    public class LiveTickResponse
    {
        /// <summary>
        /// The trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The type of tick data.
        /// </summary>
        public string DataType { get; set; } = string.Empty;

        /// <summary>
        /// System timestamp when tick was received.
        /// </summary>
        public DateTime ReceivedUtc { get; set; }

        /// <summary>
        /// Exchange generated timestamp, if available.
        /// </summary>
        public DateTime? ExchangeTimestampUtc { get; set; }

        /// <summary>
        /// Last traded price.
        /// </summary>
        public decimal? LastTradedPrice { get; set; }

        /// <summary>
        /// Highest bid price.
        /// </summary>
        public decimal? BidPrice { get; set; }

        /// <summary>
        /// Lowest ask price.
        /// </summary>
        public decimal? AskPrice { get; set; }

        /// <summary>
        /// Bid quantity.
        /// </summary>
        public long? BidSize { get; set; }

        /// <summary>
        /// Ask quantity.
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
        /// Total volume for the day.
        /// </summary>
        public long? Volume { get; set; }
    }

}
