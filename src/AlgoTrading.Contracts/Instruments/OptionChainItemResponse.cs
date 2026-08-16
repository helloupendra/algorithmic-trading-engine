using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Instruments
{

    /// <summary>
    /// Data Transfer Object representing a single contract in an option chain (either a Put or Call).
    /// Used by the API to serve filtered option chains to a client or strategy.
    /// </summary>
    public class OptionChainItemResponse
    {
        /// <summary>
        /// The specific contract symbol (e.g., "NSE:BANKNIFTY24JUN50000CE").
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The underlying asset (e.g., "BANKNIFTY").
        /// </summary>
        public string Underlying { get; set; } = string.Empty;

        /// <summary>
        /// The expiration date of this contract.
        /// </summary>
        public DateOnly? ExpiryDate { get; set; }

        /// <summary>
        /// The strike price.
        /// </summary>
        public decimal? StrikePrice { get; set; }

        /// <summary>
        /// "CE" for Call Option, "PE" for Put Option.
        /// </summary>
        public string OptionType { get; set; } = string.Empty;

        /// <summary>
        /// Typically "OPTIDX" or "OPTSTK".
        /// </summary>
        public string InstrumentType { get; set; } = string.Empty;

        /// <summary>
        /// Human-readable description.
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

}
