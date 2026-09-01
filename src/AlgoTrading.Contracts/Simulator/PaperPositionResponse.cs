// src/AlgoTrading.Contracts/Simulator/PaperPositionResponse.cs
namespace AlgoTrading.Contracts.Simulator;

/// <summary>
/// Data Transfer Object representing a simulated open or closed position.
/// </summary>
public class PaperPositionResponse
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
    /// Strategy name.
    /// </summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    /// Multi-leg group ID.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Trading symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Long or Short.
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>
    /// Current open quantity.
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Volume weighted average entry price.
    /// </summary>
    public decimal AveragePrice { get; set; }

    /// <summary>
    /// Last known live price for this symbol to calculate unrealized PnL.
    /// </summary>
    public decimal? LastMarkPrice { get; set; }

    /// <summary>
    /// Profit/Loss secured by closed trades.
    /// </summary>
    public decimal RealizedPnl { get; set; }

    /// <summary>
    /// Paper profit/loss currently floating.
    /// </summary>
    public decimal UnrealizedPnl { get; set; }

    /// <summary>
    /// Open or Closed.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when position was created.
    /// </summary>
    public DateTime OpenedUtc { get; set; }

    /// <summary>
    /// Timestamp when position was fully closed.
    /// </summary>
    public DateTime? ClosedUtc { get; set; }

    /// <summary>
    /// Timestamp of last PnL update.
    /// </summary>
    public DateTime UpdatedUtc { get; set; }
}