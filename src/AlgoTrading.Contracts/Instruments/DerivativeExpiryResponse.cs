using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Instruments
{

    /// <summary>
    /// Data Transfer Object representing a single expiration date for a given derivative underlying.
    /// Used by the API layer to return available option expiries to the client or strategy runner.
    /// </summary>
    public class DerivativeExpiryResponse
    {
        /// <summary>
        /// The underlying symbol (e.g., "BANKNIFTY").
        /// </summary>
        public string Underlying { get; set; } = string.Empty;

        /// <summary>
        /// The specific date on which derivative contracts for this underlying expire.
        /// </summary>
        public DateOnly ExpiryDate { get; set; }
    }

}
