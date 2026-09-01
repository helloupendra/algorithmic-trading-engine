// src/AlgoTrading.Contracts/Equities/EquityGroupMemberResponse.cs
namespace AlgoTrading.Contracts.Equities;

public class EquityGroupMemberResponse
{
    public long Id { get; set; }

    public long EquityGroupId { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public decimal? Weight { get; set; }

    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public bool IsEnabled { get; set; }
}