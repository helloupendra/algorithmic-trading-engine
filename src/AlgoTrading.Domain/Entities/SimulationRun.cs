using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{

    /// <summary>
    /// Represents a single execution instance of a strategy, either running live ("LivePaper") 
    /// or backtesting against historical data ("OfflineReplay").
    /// </summary>
    public class SimulationRun
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
        /// The primary instrument symbol the strategy is focused on.
        /// </summary>
        public string Symbol { get; set; } = string.Empty;

        /// <summary>
        /// The primary timeframe resolution the simulation operates on.
        /// </summary>
        public string Resolution { get; set; } = "1m";

        /// <summary>
        /// For offline replays, the start boundary of historical data to process.
        /// </summary>
        public DateTime? FromUtc { get; set; }

        /// <summary>
        /// For offline replays, the end boundary of historical data to process.
        /// </summary>
        public DateTime? ToUtc { get; set; }

        /// <summary>
        /// Dictates how fast historical data is fed to the strategy (e.g., "1x", "10x", "candle").
        /// </summary>
        public string ReplaySpeed { get; set; } = string.Empty;

        /// <summary>
        /// The execution state of this run (e.g., "Pending", "Running", "Completed", "Failed").
        /// </summary>
        public string Status { get; set; } = "Pending";

        /// <summary>
        /// The name of the specific strategy implementation being executed (e.g., "Titli").
        /// </summary>
        public string StrategyName { get; set; } = string.Empty;

        /// <summary>
        /// JSON serialized dynamic parameters supplied to the strategy instance.
        /// </summary>
        public string ParametersJson { get; set; } = "{}";

        /// <summary>
        /// When the run request was created.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the background worker actually started processing this run.
        /// </summary>
        public DateTime? StartedUtc { get; set; }

        /// <summary>
        /// When the run gracefully finished.
        /// </summary>
        public DateTime? CompletedUtc { get; set; }

        /// <summary>
        /// Holds exception details if the run enters a "Failed" status.
        /// </summary>
        public string LastError { get; set; } = string.Empty;

        public decimal InitialCapital { get; set; } = 1000000m;
    }

}
