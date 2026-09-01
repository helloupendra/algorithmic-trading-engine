// src/AlgoTrading.Contracts/Equities/AddEquityGroupToWatchlistRequest.cs
namespace AlgoTrading.Contracts.Equities;

public class AddEquityGroupToWatchlistRequest
{
    public string GroupName { get; set; } = string.Empty;

    /// <summary>
    /// Must match existing watchlist validation: "lite" or "symbolUpdate"
    /// </summary>
    public string DataType { get; set; } = "symbolUpdate";

    public bool OnlyEnabledMembers { get; set; } = true;
}
