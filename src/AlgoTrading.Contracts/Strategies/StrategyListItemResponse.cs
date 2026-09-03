// src/AlgoTrading.Contracts/Strategies/StrategyListItemResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// One strategy from the Python catalog, decorated with its current run state.
/// Served by GET /api/Strategy and GET /api/Strategy/{id}.
/// </summary>
public class StrategyListItemResponse
{
    /// <summary>Stable id: FNV-1a hash of the strategy name (positive 31-bit).</summary>
    public int Id { get; set; }

    /// <summary>Registry name the runner is launched with (e.g. "ShortStraddle").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Plain-English description: what it does, when it profits, what it needs.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>"Neutral", "Bullish", "Bearish", "Directional", "Adjustment", "Example"...</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Underlyings the strategy is written for (e.g. ["NIFTY","BANKNIFTY"]).</summary>
    public List<string> SupportedUnderlyings { get; set; } = new();

    /// <summary>"options" today.</summary>
    public string InstrumentKind { get; set; } = "options";

    /// <summary>Human summary of the legs, e.g. "Sell ATM CE + Sell ATM PE".</summary>
    public string LegsSummary { get; set; } = string.Empty;

    public List<StrategyDataRequirement> DataRequirements { get; set; } = new();

    /// <summary>JSON object string of the strategy's default parameters.</summary>
    public string DefaultParametersJson { get; set; } = "{}";

    public int DefaultLots { get; set; } = 1;

    /// <summary>Path relative to the strategies folder, e.g. "neutral/straddle_strategy.py".</summary>
    public string SourceFile { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    // ---- run state (from the process registry) ----

    public bool IsActive { get; set; }
    public string? StartedBy { get; set; }
    public DateTime? StartedUtc { get; set; }
    public long? RunId { get; set; }
    public string? Underlying { get; set; }
    public string? SpotSymbol { get; set; }
    public int? Lots { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? Target { get; set; }
    public int? ProcessId { get; set; }

    /// <summary>Why the most recent run of this strategy ended, when it has ended since the API started.</summary>
    public StrategyLastExit? LastExit { get; set; }
}
