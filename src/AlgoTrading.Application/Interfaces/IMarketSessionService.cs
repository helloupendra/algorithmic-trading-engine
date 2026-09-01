using AlgoTrading.Domain.ValueObjects;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.Interfaces
{
    /// <summary>
    /// Service interface for determining exchange trading hours, holidays, and session states.
    /// </summary>
    public interface IMarketSessionService
    {
        /// <summary>
        /// Retrieves the market session metadata (open time, close time, etc.) for a specific exchange.
        /// </summary>
        MarketSessionInfo GetSessionInfo(
                DateTime utcNow,
                string exchange,
                string segment);

        /// <summary>
        /// Evaluates if the specified market segment is currently open for active trading.
        /// </summary>
        bool IsMarketOpen(
            DateTime utcNow,
            string exchange,
            string segment);

        /// <summary>
        /// Calculates the next time the market will open (handling weekends and holidays).
        /// </summary>
        DateTime GetNextMarketOpenUtc(
            DateTime utcNow,
            string exchange,
            string segment);
    }
}
