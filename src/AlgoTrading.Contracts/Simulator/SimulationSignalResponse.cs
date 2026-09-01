// src/AlgoTrading.Contracts/Simulator/SimulationSignalResponse.cs
namespace AlgoTrading.Contracts.Simulator;

/// <summary>
/// Data Transfer Object representing a single recorded strategy signal.
/// Used to audit or display the exact events that led to trades.
/// </summary>
public class SimulationSignalResponse
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Parent simulation run.
    /// </summary>
    public long SimulationRunId { get; set; }

    /// <summary>
    /// Emitting strategy.
    /// </summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    /// Signal type (e.g., ENTRY).
    /// </summary>
    public string SignalType { get; set; } = string.Empty;

    /// <summary>
    /// Market timestamp of the signal.
    /// </summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// Contract symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Price at the time of signal generation.
    /// </summary>
    public decimal? Price { get; set; }

    /// <summary>
    /// Multi-leg group identifier.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Additional strategy-specific debug info.
    /// </summary>
    public string MetadataJson { get; set; } = string.Empty;

    /// <summary>
    /// System creation time.
    /// </summary>
    public DateTime CreatedUtc { get; set; }
}