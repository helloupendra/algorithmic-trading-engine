// src/AlgoTrading.Domain/Entities/PaperOrder.cs
namespace AlgoTrading.Domain.Entities;

/// <summary>
/// Represents an order placed by a strategy in a simulated (paper trading) environment.
/// Used to track the lifecycle from "Pending" to "Filled".
/// </summary>
public class PaperOrder
{
    /// <summary>
    /// Primary key.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The unique identifier of the simulation or paper-trading run this order belongs to.
    /// </summary>
    public long SimulationRunId { get; set; }

    /// <summary>
    /// The ID of the specific StrategySignal that triggered this order, providing an audit trail back to the strategy logic.
    /// </summary>
    public long? SimulationSignalId { get; set; }

    /// <summary>
    /// The name of the strategy that generated this order.
    /// </summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    /// An optional identifier to group multiple related orders together (e.g., entering a multi-leg spread simultaneously).
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// The specific instrument symbol being ordered.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// The side of the order: "BUY" or "SELL".
    /// </summary>
    public string Side { get; set; } = string.Empty;

    /// <summary>
    /// The number of shares or contracts to transact.
    /// </summary>
    public int Quantity { get; set; }

    /// <summary>
    /// The type of simulation execution: "MARKET_SIM" (fill immediately at current price) or "LIMIT_SIM" (fill when price reaches limit).
    /// </summary>
    public string OrderType { get; set; } = "MARKET_SIM";

    /// <summary>
    /// The current state of the order: "Pending", "Filled", or "Cancelled".
    /// </summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// The specific price at which the order was requested (for limit orders) or the snapshot price at creation.
    /// </summary>
    public decimal? RequestedPrice { get; set; }

    /// <summary>
    /// The actual price at which the simulator filled the order.
    /// </summary>
    public decimal? FillPrice { get; set; }

    /// <summary>
    /// When the order was initially created by the strategy.
    /// </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the simulator successfully executed the order.
    /// </summary>
    public DateTime? FilledUtc { get; set; }
}
