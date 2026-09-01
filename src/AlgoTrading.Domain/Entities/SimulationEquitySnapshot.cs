// src/AlgoTrading.Domain/Entities/SimulationEquitySnapshot.cs
namespace AlgoTrading.Domain.Entities;

public class SimulationEquitySnapshot
{
    public long Id { get; set; }

    public long SimulationRunId { get; set; }

    public DateTime SnapshotUtc { get; set; } = DateTime.UtcNow;

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
