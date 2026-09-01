using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.ValueObjects
{
    /// <summary>
    /// A value object representing the current operational state of a specific market exchange.
    /// Used to govern when background workers should actively poll or pause based on market hours.
    /// </summary>
    public class MarketSessionInfo
    {
        /// <summary>
        /// The specific exchange (e.g., "NSE").
        /// </summary>
        public string Exchange { get; set; } = string.Empty; 

        /// <summary>
        /// The market segment (e.g., "FO" for Derivatives).
        /// </summary>
        public string Segment { get; set; } = string.Empty; 
        
        /// <summary>
        /// The current universal time used for these calculations.
        /// </summary>
        public DateTime UtcNow { get; set; }

        /// <summary>
        /// The current time in the local timezone of the exchange.
        /// </summary>
        public DateTime LocalNow { get; set; }

        /// <summary>
        /// True if today is a valid trading day (not a weekend or known holiday).
        /// </summary>
        public bool IsTradingDay { get; set; }

        /// <summary>
        /// True if the current time falls exactly between the session's open and close times.
        /// </summary>
        public bool IsMarketOpen { get; set; }

        /// <summary>
        /// The UTC timestamp when the market opens today.
        /// </summary>
        public DateTime SessionOpenUtc { get; set; }

        /// <summary>
        /// The UTC timestamp when the market closes today.
        /// </summary>
        public DateTime SessionCloseUtc { get; set; }

        /// <summary>
        /// The UTC timestamp of the very next time the market will open (useful for sleeping threads).
        /// </summary>
        public DateTime NextMarketOpenUtc { get; set; }

        /// <summary>
        /// The IANA timezone identifier of the exchange (e.g., "Asia/Kolkata").
        /// </summary>
        public string TimeZoneId { get; set; } = string.Empty;
    }
}
