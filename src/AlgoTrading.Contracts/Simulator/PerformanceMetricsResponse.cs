// src/AlgoTrading.Contracts/Simulator/PerformanceMetricsResponse.cs//
using AlgoTrading.Contracts.Simulator;

public class PerformanceMetricsResponse
{
    public long SimulationRunId { get; set; }

    public decimal InitialCapital { get; set; }
    public decimal CurrentEquity { get; set; }

    public decimal TotalReturnPercent { get; set; }

    public decimal MaxDrawdownPercent { get; set; }

    public int TotalClosedPositions { get; set; }
    public int WinningPositions { get; set; }
    public int LosingPositions { get; set; }

    public decimal WinRatePercent { get; set; }

    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }

    public decimal GrossProfit { get; set; }
    public decimal GrossLoss { get; set; }

    public decimal ProfitFactor { get; set; }
    public decimal Expectancy { get; set; }
}
