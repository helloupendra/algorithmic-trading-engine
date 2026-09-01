// src/AlgoTrading.Contracts/Simulator/SimulationPortfolioResponse.cs
namespace AlgoTrading.Contracts.Simulator;

public class SimulationPortfolioResponse
{
    public long SimulationRunId { get; set; }

    public string StrategyName { get; set; } = string.Empty;
    public string RunStatus { get; set; } = string.Empty;

    public decimal InitialCapital { get; set; }
    public decimal UsedCapital { get; set; }
    public decimal AvailableCapital { get; set; }

    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal TotalPnl { get; set; }

    public decimal CurrentEquity { get; set; }
    public decimal ReturnPercent { get; set; }

    public int TotalOrders { get; set; }
    public int FilledOrders { get; set; }

    public int OpenPositions { get; set; }
    public int ClosedPositions { get; set; }

    public List<PositionGroupSummaryResponse> Groups { get; set; } = new();
}
