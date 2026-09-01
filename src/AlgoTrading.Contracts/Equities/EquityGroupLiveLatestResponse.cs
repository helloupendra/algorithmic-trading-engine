// src/AlgoTrading.Contracts/Equities/EquityGroupLiveLatestResponse.cs
namespace AlgoTrading.Contracts.Equities;

public class EquityGroupLiveLatestResponse
{
    public string GroupName { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public int TotalMembers { get; set; }
    public int MembersWithLiveData { get; set; }

    public List<EquityLatestQuoteResponse> Members { get; set; } = new();
}