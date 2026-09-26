// src/AlgoTrading.Contracts/Strategies/StrategyActiveRunResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// One active run of a strategy: the same strategy may run on several
/// underlyings at once, each as its own runner process and SimulationRun.
/// Listed oldest first in <see cref="StrategyListItemResponse.ActiveRuns"/>.
/// Run-scoped routes (/api/Strategy/runs/{runId}/...) take <see cref="RunId"/>.
/// </summary>
public class StrategyActiveRunResponse
{
    public long RunId { get; set; }
    public string Underlying { get; set; } = string.Empty;
    public string SpotSymbol { get; set; } = string.Empty;
    public int Lots { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? Target { get; set; }

    /// <summary>The run's current risk rules at all three levels.</summary>
    public RiskRulesDto Risk { get; set; } = RiskRulesDto.Empty();

    public string StartedBy { get; set; } = string.Empty;

    /// <summary>
    /// Whose account the run trades in. Not always who started it: the morning
    /// job starts every account's runs as the admin, and without the owner the
    /// console showed two accounts' identical runs as one account's run twice.
    /// </summary>
    public long OwnerUserId { get; set; }

    /// <summary>The owner's user name, for the card and the Telegram title.</summary>
    public string? OwnerName { get; set; }
    public DateTime StartedUtc { get; set; }
    public int ProcessId { get; set; }

    /// <summary>True when the runner was adopted after an API restart (its output is not captured).</summary>
    public bool Adopted { get; set; }
}
