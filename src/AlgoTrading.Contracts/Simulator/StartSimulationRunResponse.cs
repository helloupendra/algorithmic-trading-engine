using System;
using System.Collections.Generic;
using System.Text;


namespace AlgoTrading.Contracts.Simulator;

/// <summary>
/// Data Transfer Object representing the result of starting an offline simulation run.
/// </summary>
public class StartSimulationRunResponse
{
    /// <summary>
    /// The ID of the run that was processed.
    /// </summary>
    public long RunId { get; set; }

    /// <summary>
    /// Final status (e.g., Completed).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Total number of historical bars injected into the runner.
    /// </summary>
    public int FramesProcessed { get; set; }

    /// <summary>
    /// UTC timestamp of the earliest bar.
    /// </summary>
    public DateTime? FirstFrameUtc { get; set; }

    /// <summary>
    /// UTC timestamp of the latest bar.
    /// </summary>
    public DateTime? LastFrameUtc { get; set; }

    /// <summary>
    /// Execution status or error details.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
