// src/AlgoTrading.Domain/Entities/SimulationSignal.cs
namespace AlgoTrading.Domain.Entities;

/// <summary>
/// Represents a raw trading signal emitted by a strategy during a simulation or live-paper run.
/// Orders and positions are subsequently created by the simulator acting upon these signals.
/// </summary>
public class SimulationSignal
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The parent run that generated this signal.
    /// </summary>
    public long SimulationRunId { get; set; }

    /// <summary>
    /// The strategy that emitted this signal.
    /// </summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    /// The semantic type of the signal (e.g., "ENTRY", "EXIT", "ATM_SHIFT").
    /// </summary>
    public string SignalType { get; set; } = string.Empty;

    /// <summary>
    /// The UTC timestamp when the market condition triggering this signal occurred.
    /// </summary>
    public DateTime TimestampUtc { get; set; }

    /// <summary>
    /// The specific symbol related to the signal, if applicable.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// The spot price or instrument price at the time the signal fired.
    /// </summary>
    public decimal? Price { get; set; }

    /// <summary>
    /// An identifier to group multiple signals together (e.g., placing both legs of a straddle).
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Any arbitrary context (like ATM strikes or internal state) the strategy attaches to the signal for debugging.
    /// </summary>
    public string MetadataJson { get; set; } = "{}";

    /// <summary>
    /// The runner's own id for this signal, sent again with every retry of it.
    /// </summary>
    /// <remarks>
    /// A post whose answer was lost (a timeout, a dropped connection) may or may
    /// not have been booked, and only a retry can find out. Without an id the
    /// retry of an OPEN_GROUP would book the group twice; with one, the API
    /// answers with the row it already has. Unique per run; null for every
    /// signal the API writes itself and for runners that predate it.
    /// </remarks>
    public string? ClientSignalId { get; set; }

    /// <summary>
    /// When this record was persisted to the database.
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}