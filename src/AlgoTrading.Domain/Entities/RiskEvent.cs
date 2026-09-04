namespace AlgoTrading.Domain.Entities;

public class RiskEvent
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    
    /// <summary>
    /// e.g. KillSwitchActivated, KillSwitchDeactivated, LimitsChanged, OrderRejected
    /// </summary>
    public string Kind { get; set; } = string.Empty;
    
    public long? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    
    public string? Reason { get; set; }
    public string? DetailsJson { get; set; }
    
    public long? SimulationRunId { get; set; }
    public string? Symbol { get; set; }
}
