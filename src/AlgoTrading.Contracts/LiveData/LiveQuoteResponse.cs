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
        /// When this row was written by the API. Feed health, not price age.
        /// </summary>
        public DateTime UpdatedUtc { get; set; }

        /// <summary>
        /// When the exchange says this price was made - i.e. the last trade.
        /// </summary>
        /// <remarks>
        /// Carried because the two answer different questions and a screen that
        /// shows one while meaning the other misleads. On a quiet contract the
        /// gap is large and real: MCX gold futures wrote at 1s and last traded
        /// 211s earlier on 2026-09-07, while gold mini traded 4s earlier. A
        /// dashboard reading "211s" off the exchange stamp is telling the truth
        /// about the price; the same number read as feed lag is a false alarm.
        ///
        /// Null when the vendor sends no stamp, in which case the arrival time
        /// is the only answer there is.
        /// </remarks>
        public DateTime? ExchangeTimestampUtc { get; set; }
    }

}
