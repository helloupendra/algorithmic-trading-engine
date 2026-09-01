// src/AlgoTrading.Contracts/Equities/EquityGroupResponse.cs
namespace AlgoTrading.Contracts.Equities;

public class EquityGroupResponse
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public int MemberCount { get; set; }
}