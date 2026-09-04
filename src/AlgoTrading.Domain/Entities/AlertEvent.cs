namespace AlgoTrading.Domain.Entities;

public class AlertEvent
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    
    /// <summary>
    /// e.g. logic-engine, e2e-test, system
    /// </summary>
    public string Source { get; set; } = string.Empty;
    
    public string Underlying { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    
    /// <summary>
    /// e.g. info, warning, critical
    /// </summary>
    public string Severity { get; set; } = string.Empty;
    
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    
    public string? MetadataJson { get; set; }
    public bool DeliveredToTelegram { get; set; }
    
    public long? SimulationRunId { get; set; }
}
