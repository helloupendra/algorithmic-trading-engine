using AlgoTrading.Contracts.Simulator;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.Interfaces
{

    /// <summary>
    /// Service interface for extracting local historical data in a format suitable for the strategy runner.
    /// </summary>
    public interface IReplayFeedProvider
    {
        /// <summary>
        /// Loads historical candles ordered by time to be sequentially fed into a backtesting strategy.
        /// </summary>
        Task<IReadOnlyList<ReplayBarFrame>> LoadBarsAsync(
            string symbol,
            string resolution,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken cancellationToken = default);
    }

}
