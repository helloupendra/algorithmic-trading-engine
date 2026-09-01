// src/AlgoTrading.Domain/Entities/EquityGroup.cs
namespace AlgoTrading.Domain.Entities;

public class EquityGroup
{
    public long Id { get; set; }

    /// <summary>
    /// Unique code-safe group name.
    /// Example: BANKNIFTY_CONSTITUENTS
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Exchange this group mainly belongs to.
    /// Example: NSE / BSE
    /// </summary>
    public string Exchange { get; set; } = string.Empty;

    /// <summary>
    /// Display name for frontend/UI.
    /// Example: Bank Nifty Constituents
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Optional description for admins/devs.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<EquityGroupMember> Members { get; set; } = new List<EquityGroupMember>();
}