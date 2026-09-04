namespace AlgoTrading.Contracts.Risk;

public class RiskLimitsDto
{
    public int MaxOrdersPerMinute { get; set; }
    public decimal MaxDailyLoss { get; set; }
    public int MaxConcurrentRuns { get; set; }
    public int MaxRunsPerUser { get; set; }
    
    /// <summary>
    /// "database" or "config"
    /// </summary>
    public string Source { get; set; } = string.Empty;
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
