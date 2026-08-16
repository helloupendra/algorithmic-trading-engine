// src/AlgoTrading.Domain/Entities/EquityGroupMember.cs
namespace AlgoTrading.Domain.Entities;

public class EquityGroupMember
{
    public long Id { get; set; }

    public long EquityGroupId { get; set; }
    public EquityGroup? EquityGroup { get; set; }

    /// <summary>
    /// Cash-market symbol from instruments table.
    /// Example: NSE:HDFCBANK-EQ
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Optional weight in the index/group.
    /// Useful later for weighted breadth / weighted mood analysis.
    /// </summary>
    public decimal? Weight { get; set; }

    /// <summary>
    /// Optional membership start date.
    /// Useful if index constituents change historically.
    /// </summary>
    public DateOnly? EffectiveFrom { get; set; }

    /// <summary>
    /// Optional membership end date.
    /// Null means still active/current.
    /// </summary>
    public DateOnly? EffectiveTo { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}