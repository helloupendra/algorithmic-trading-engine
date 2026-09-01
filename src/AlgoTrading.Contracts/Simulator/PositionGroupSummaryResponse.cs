// src/AlgoTrading.Contracts/Simulator/PositionGroupSummaryResponse.cs
namespace AlgoTrading.Contracts.Simulator;

public class PositionGroupSummaryResponse
{
    public string GroupId { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;

    public int OpenPositionCount { get; set; }
    public int ClosedPositionCount { get; set; }

    public decimal UsedCapital { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }

    public string Status { get; set; } = string.Empty;
}