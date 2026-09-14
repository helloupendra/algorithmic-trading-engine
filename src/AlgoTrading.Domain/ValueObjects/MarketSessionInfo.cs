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

        /// <summary>
        /// True when today is on the exchange's holiday list — closed all day, or
        /// (MCX) closed for one of its two sessions.
        /// </summary>
        public bool IsHoliday { get; set; }

        /// <summary>The occasion, e.g. "Ganesh Chaturthi"; null on an ordinary day.</summary>
        public string? HolidayName { get; set; }

        /// <summary>"FullDay", "MorningSession" or "EveningSession" when <see cref="IsHoliday"/>.</summary>
        public string? HolidayClosure { get; set; }

        /// <summary>Set when today runs special hours: Muhurat trading, a special Saturday session.</summary>
        public string? SpecialSessionName { get; set; }

        /// <summary>
        /// Set when the holiday calendar for this date is not loaded, so the answer
        /// rests on weekends alone. Not knowing about a holiday is not the same as
        /// knowing there is none, and callers must be able to tell.
        /// </summary>
        public string? CalendarWarning { get; set; }
    }
}
