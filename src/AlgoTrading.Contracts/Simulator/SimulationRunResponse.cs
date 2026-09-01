using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Simulator
{

    /// <summary>
    /// Data Transfer Object representing the metadata and status of a simulation or paper trading run.
    /// </summary>
    public class SimulationRunResponse
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// The user who owns this simulation run.
        /// </summary>
        public long UserId { get; set; }

        /// <summary>
        /// The mode of operation: "LivePaper" or "OfflineReplay".
        /// </summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>
        /// The main trading symbol.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// Timeframe resolution.
        /// </summary>
        public string Resolution { get; set; } = string.Empty;

        /// <summary>
        /// Historical start boundary (if OfflineReplay).
        /// </summary>
        public DateTime? FromUtc { get; set; }

        /// <summary>
        /// Historical end boundary (if OfflineReplay).
        /// </summary>
        public DateTime? ToUtc { get; set; }

        /// <summary>
        /// Speed multiplier for backtests.
        /// </summary>
        public string ReplaySpeed { get; set; } = string.Empty;

        /// <summary>
        /// Current status (e.g., Pending, Running, Completed, Failed).
        /// </summary>
        public string Status { get; set; } = string.Empty;

        /// <summary>
        /// The deployed strategy name.
        /// </summary>
        public string StrategyName { get; set; } = string.Empty;

        /// <summary>
        /// JSON snapshot of the strategy parameters used.
        /// </summary>
        public string ParametersJson { get; set; } = string.Empty;

        /// <summary>
        /// When the run was requested.
        /// </summary>
        public DateTime CreatedUtc { get; set; }

        /// <summary>
        /// When the runner actually started processing.
        /// </summary>
        public DateTime? StartedUtc { get; set; }

        /// <summary>
        /// When the run finished.
        /// </summary>
        public DateTime? CompletedUtc { get; set; }

        /// <summary>
        /// Any fatal errors that caused termination.
        /// </summary>
        public string LastError { get; set; } = string.Empty;

        /// <summary>
        /// Starting virtual balance.
        /// </summary>
        public decimal InitialCapital { get; set; }
    }

}
