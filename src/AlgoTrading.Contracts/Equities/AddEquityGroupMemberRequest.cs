// src/AlgoTrading.Contracts/Equities/AddEquityGroupMemberRequest.cs
using System.ComponentModel.DataAnnotations;

namespace AlgoTrading.Contracts.Equities;

public class AddEquityGroupMemberRequest
{
    [Required]
    [MaxLength(50)]
    public string Symbol { get; set; } = string.Empty;

    public decimal Weight { get; set; } = 1.0m;
}
