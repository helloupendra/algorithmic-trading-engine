// src/AlgoTrading.Contracts/Simulator/PaperOrderResponse.cs
namespace AlgoTrading.Contracts.Simulator;

/// <summary>
/// Data Transfer Object representing a simulated trading order.
/// </summary>
public class PaperOrderResponse
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The parent simulation run.
    /// </summary>
    public long SimulationRunId { get; set; }

    /// <summary>
    /// The strategy signal that triggered this order, if any.
    /// </summary>
    public long? SimulationSignalId { get; set; }

    /// <summary>
    /// The strategy name.
    /// </summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    /// Identifier for grouping multi-leg orders.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Trading symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Buy or Sell.
    /// </summary>
    public string Side { get; set; } = string.Empty;

    /// <summary>
    /// Number of shares/contracts.
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    /// Market, Limit, etc.
    /// </summary>
    public string OrderType { get; set; } = string.Empty;

    /// <summary>
    /// Order status (e.g., Filled, Pending).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Desired execution price.
    /// </summary>
    public decimal? RequestedPrice { get; set; }

    /// <summary>
    /// Actual execution price.
    /// </summary>
    public decimal? FillPrice { get; set; }

    /// <summary>
    /// Order creation timestamp.
    /// </summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Order execution timestamp.
    /// </summary>
    public DateTime? FilledUtc { get; set; }
}
