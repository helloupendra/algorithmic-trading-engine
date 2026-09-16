namespace AlgoTrading.Contracts.Risk;

public class RiskLimitsDto
{
    public int MaxOrdersPerMinute { get; set; }
    public decimal MaxDailyLoss { get; set; }
    /// <summary>Open runs allowed on the whole platform; 0 means no cap.</summary>
    public int MaxConcurrentRuns { get; set; }

    /// <summary>Open runs allowed per user; 0 means no cap.</summary>
    public int MaxRunsPerUser { get; set; }
    
    /// <summary>
    /// "database" or "config"
    /// </summary>
    public string Source { get; set; } = string.Empty;
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}
