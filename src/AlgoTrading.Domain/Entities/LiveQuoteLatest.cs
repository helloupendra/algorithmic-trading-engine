using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{

    /// <summary>
    /// Represents the absolute latest snapshot of a single instrument's price data.
    /// This is an "upsert-only" table optimized for rapid reads by strategies.
    /// </summary>
    public class LiveQuoteLatest
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
        /// The type of data update received from the broker (e.g., "symbolUpdate").
        /// </summary>
        public string DataType { get; set; } = "symbolUpdate";

        /// <summary>
        /// The most recent price the instrument traded at.
        /// </summary>
        public decimal? LastTradedPrice { get; set; }

        /// <summary>
        /// The daily opening price.
        /// </summary>
        public decimal? Open { get; set; }

        /// <summary>
        /// The highest price reached today.
        /// </summary>
        public decimal? High { get; set; }

        /// <summary>
        /// The lowest price reached today.
        /// </summary>
        public decimal? Low { get; set; }

        /// <summary>
        /// The previous day's closing price.
        /// </summary>
        public decimal? Close { get; set; }

        /// <summary>
        /// The total volume traded today.
        /// </summary>
        public long? Volume { get; set; }

        /// <summary>
        /// Optional raw JSON payload received from the broker for debugging or extending data extraction later.
        /// </summary>
        public string RawPayload { get; set; } = string.Empty;

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
        /// The timestamp of the last quote update for this symbol.
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Which connector produced this quote, e.g. "fyers".</summary>
        public string SourceKey { get; set; } = string.Empty;
    }

}
