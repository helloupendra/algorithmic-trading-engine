namespace AlgoTrading.Contracts.Alerts;

public class AlertEventDto
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Underlying { get; set; } = string.Empty;
    public string? Symbol { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public bool DeliveredToTelegram { get; set; }
    public long? SimulationRunId { get; set; }
}
