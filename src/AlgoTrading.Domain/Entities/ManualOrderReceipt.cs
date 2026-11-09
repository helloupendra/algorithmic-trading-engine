namespace AlgoTrading.Domain.Entities;

/// <summary>A successful ticket outcome, committed with its fill and retained for safe retries.</summary>
public sealed class ManualOrderReceipt
{
    public long UserId { get; set; }
    public Guid ClientOrderId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string FillJson { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
}
