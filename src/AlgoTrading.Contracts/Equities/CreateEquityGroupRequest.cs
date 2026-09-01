// src/AlgoTrading.Contracts/Equities/CreateEquityGroupRequest.cs
using System.ComponentModel.DataAnnotations;

namespace AlgoTrading.Contracts.Equities;

public class CreateEquityGroupRequest
{
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Exchange { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? DisplayName { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }
}
