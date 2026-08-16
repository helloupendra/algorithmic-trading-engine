using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Simulator
{
    /// <summary>
    /// Data Transfer Object representing a client request to start a new simulation or live-paper run.
    /// </summary>
    public class CreateSimulationRunRequest
    {
        /// <summary>
        /// The user who owns this simulation run.
        /// </summary>
        public long UserId { get; set; }

        /// <summary>
        /// "LivePaper" or "OfflineReplay".
        /// </summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>
        /// The main trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Timeframe resolution (e.g., "1m").
        /// </summary>
        public string Resolution { get; set; } = "1m";

        /// <summary>
        /// Backtest start date boundary.
        /// </summary>
        public DateTime? FromUtc { get; set; }

        /// <summary>
        /// Backtest end date boundary.
        /// </summary>
        public DateTime? ToUtc { get; set; }

        /// <summary>
        /// Speed multiplier for backtests.
        /// </summary>
        public string ReplaySpeed { get; set; } = string.Empty;

        /// <summary>
        /// The name of the strategy to instantiate.
        /// </summary>
        public string StrategyName { get; set; } = string.Empty;

        /// <summary>
        /// JSON string containing custom strategy parameters.
        /// </summary>
        public string ParametersJson { get; set; } = "{}";

        /// <summary>
        /// Starting paper capital.
        /// </summary>
        public decimal InitialCapital { get; set; } = 1000000m;
    }

}
