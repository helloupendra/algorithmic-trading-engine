using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{
    /// <summary>
    /// Represents a tradable financial instrument (e.g., Equity, Future, Option) available on the platform.
    /// Used universally to validate symbols, resolve option chains, and format broker requests.
    /// </summary>
    public class Instrument
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public long Id { get; set; }
        /// <summary>
        /// The unique broker symbol (e.g., "NSE:BANKNIFTY24JUN50000CE").
        /// </summary>
        public string Symbol { get; set; }  = string.Empty;

        /// <summary>
        /// The exchange on which the instrument trades (e.g., "NSE", "BSE", "MCX").
        /// </summary>
        public string Exchange { get; set; } = string.Empty;

        /// <summary>
        /// The market segment (e.g., "CM" for Cash, "FO" for Derivatives/Futures & Options).
        /// </summary>
        public string Segment { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable description or name of the instrument.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// The type of the instrument (e.g., "EQ", "OPTIDX", "FUTIDX").
        /// </summary>
        public string InstrumentType { get; set; } = string.Empty;

        /// <summary>
        /// The International Securities Identification Number.
        /// </summary>
        public string Isin { get; set; } = string.Empty;

        /// <summary>
        /// The minimum number of shares/contracts that can be traded in a single order.
        /// </summary>
        public int? LotSize { get; set; }

        /// <summary>
        /// The minimum price movement allowed for the instrument.
        /// </summary>
        public decimal? TickSize { get; set; }

        /// <summary>
        /// The date on which the derivative contract expires (null for equity).
        /// </summary>
        public DateOnly? ExpiryDate { get; set; }

        /// <summary>
        /// Whether the system is actively tracking or allowing trading for this instrument.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// A sorting priority for displaying or syncing this instrument.
        /// </summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// Record creation time.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Last time the instrument metadata was updated from the broker's master list.
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// For derivatives: the underlying asset's symbol (e.g., "BANKNIFTY").
        /// </summary>
        public string Underlying { get; set; } = string.Empty;

        /// <summary>
        /// For options: the strike price of the contract.
        /// </summary>
        public decimal? StrikePrice { get; set; }

        /// <summary>
        /// For options: "CE" (Call) or "PE" (Put). Empty for non-options.
        /// </summary>
        public string OptionType { get; set; } = string.Empty;

    }
}
