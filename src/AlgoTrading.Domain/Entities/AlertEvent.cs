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

    /// <summary>
    /// Identity of an event that must be recorded at most once, whoever writes it
    /// and however often it restarts. Unique when set; null for everything that
    /// is free to repeat. The candle-pattern scanner uses
    /// "patterns:{symbol}:{timeframe}m:{candle start}:{pattern}".
    /// </summary>
    public string? DedupeKey { get; set; }

    public long? SimulationRunId { get; set; }
}
