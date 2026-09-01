using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.LiveData
{

    /// <summary>
    /// Data Transfer Object representing the most recent price snapshot for a symbol.
    /// Used by dashboard APIs to show live prices.
    /// </summary>
    public class LiveQuoteResponse
    {
        /// <summary>
        /// The trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Type of market data update.
        /// </summary>
        public string DataType { get; set; } = string.Empty;

        /// <summary>
        /// The latest traded price.
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
        /// Cumulative daily volume.
        /// </summary>
        public long? Volume { get; set; }

        /// <summary>
        /// Total Open Interest.
        /// </summary>
        public long? OpenInterest { get; set; }

        /// <summary>
        /// Implied Volatility (IV).
        /// </summary>
        public decimal? ImpliedVolatility { get; set; }

        /// <summary>
        /// Delta.
        /// </summary>
        public decimal? Delta { get; set; }

        /// <summary>
        /// Gamma.
        /// </summary>
        public decimal? Gamma { get; set; }

        /// <summary>
        /// Theta.
        /// </summary>
        public decimal? Theta { get; set; }

        /// <summary>
        /// Vega.
        /// </summary>
        public decimal? Vega { get; set; }

        /// <summary>
        /// Timestamp of the last received quote.
        /// </summary>
        public DateTime UpdatedUtc { get; set; }
    }

}
