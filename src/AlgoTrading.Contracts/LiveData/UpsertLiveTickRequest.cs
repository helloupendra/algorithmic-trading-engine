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
        public long? OpenInterest { get; set; }

        /// <summary>
        /// Implied volatility and greeks, when the producer computed them.
        /// </summary>
        /// <remarks>
        /// These were missing from this DTO entirely. The ingestor computed
        /// them, put them in the tick payload and posted it — and model binding
        /// dropped every one without a word, because a property that does not
        /// exist is not an error. That is why every stored quote had a null IV
        /// while the calculator upstream was working perfectly.
        /// </remarks>
        public decimal? ImpliedVolatility { get; set; }

        public decimal? Delta { get; set; }

        public decimal? Gamma { get; set; }

        public decimal? Theta { get; set; }

        public decimal? Vega { get; set; }

        public string RawPayload { get; set; } = string.Empty;

        /// <summary>
        /// Which connector produced this tick, e.g. "fyers". Optional: when the
        /// ingestor does not say, the API stamps the only connector that claims
        /// a live feed, and leaves it blank rather than guess when several do.
        /// </summary>
        public string? SourceKey { get; set; }
    }

}
