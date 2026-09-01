// src/AlgoTrading.Contracts/Simulator/SimulationEquitySnapshotResponse.cs
namespace AlgoTrading.Contracts.Simulator;

public class SimulationEquitySnapshotResponse
{
    public DateTime SnapshotUtc { get; set; }

    public decimal InitialCapital { get; set; }
    public decimal UsedCapital { get; set; }
    public decimal AvailableCapital { get; set; }

    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public decimal TotalPnl { get; set; }

    public decimal CurrentEquity { get; set; }

    public int OpenPositions { get; set; }
    public int ClosedPositions { get; set; }
}