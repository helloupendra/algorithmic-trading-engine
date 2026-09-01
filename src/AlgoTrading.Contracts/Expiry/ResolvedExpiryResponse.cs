// src/AlgoTrading.Contracts/Expiry/ResolvedExpiryResponse.cs
namespace AlgoTrading.Contracts.Expiry;

public class ResolvedExpiryResponse
{
    public string Exchange { get; set; } = string.Empty;
    public string Underlying { get; set; } = string.Empty;

    public string ExpiryType { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }

    public bool IsHolidayAdjusted { get; set; }
    public string ResolutionSource { get; set; } = string.Empty; // RuleOnly / Instruments / Cache
}