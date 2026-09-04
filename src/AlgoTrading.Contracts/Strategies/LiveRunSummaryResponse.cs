// src/AlgoTrading.Contracts/Strategies/LiveRunSummaryResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// One row of GET /api/Strategy/runs — a LivePaper run in the per-user run
/// history, active or finished, with its headline numbers (one grouped query
/// over its paper positions). Nothing in the history is ever deleted: every
/// run a user started stays here with how and why it ended.
/// </summary>
public class LiveRunSummaryResponse
{
    public long RunId { get; set; }

    /// <summary>The user who started the run (SimulationRun.UserId).</summary>
    public long UserId { get; set; }

    public string? UserName { get; set; }

    /// <summary>Catalog id of the strategy (stable hash of its name when it is no longer in the catalog).</summary>
    public int StrategyId { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    /// <summary>Catalog category (empty when the strategy is no longer in the catalog).</summary>
    public string Category { get; set; } = string.Empty;

    public string Underlying { get; set; } = string.Empty;
    public string SpotSymbol { get; set; } = string.Empty;

    public int Lots { get; set; }

    /// <summary>Lot size of the underlying today (the run's positions carry their own).</summary>
    public int LotSize { get; set; }

    /// <summary>The run's risk rules (the current ones while running; the last persisted ones once stopped).</summary>
    public RiskRulesDto Risk { get; set; } = RiskRulesDto.Empty();

    /// <summary>Running | Stopping | Stopped | Failed | Completed (SimulationRun.Status).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>True while a runner process is behind the run (registry entry present).</summary>
    public bool IsActive { get; set; }

    public DateTime StartedUtc { get; set; }
    public DateTime? StoppedUtc { get; set; }

    /// <summary>
    /// Why the run ended: the RUN_STOPPED reason ("Stop loss hit: …", "Market
    /// closed (15:30 IST)", "Stopped by admin", "Runner exited (code 1)"…), the
    /// run's last error for a run that failed to start, or null while running.
    /// </summary>
    public string? StopReason { get; set; }

    /// <summary>Who ended it: a user name, "risk-guard", "market-hours", "runner", "api"; null while running.</summary>
    public string? StoppedBy { get; set; }

    /// <summary>Seconds from start to stop (to now while active).</summary>
    public long DurationSeconds { get; set; }

    /// <summary>Σ realized P&amp;L of every position of the run (open ones included), minus charges (none for live runs today).</summary>
    public decimal NetPnl { get; set; }

    public decimal RealizedPnl { get; set; }

    /// <summary>Σ unrealized of the open positions, marked to the latest live quote; 0 once the run is not active.</summary>
    public decimal UnrealizedPnl { get; set; }

    /// <summary>Closed positions.</summary>
    public int Trades { get; set; }

    public int OpenPositions { get; set; }

    /// <summary>Distinct position groups (OPEN_GROUP) the run created.</summary>
    public int Groups { get; set; }

    /// <summary>Flat rupees per lot per fill netted into <see cref="NetPnl"/>; live runs carry no charges (0).</summary>
    public decimal ChargesPerLot { get; set; }

    /// <summary>Capital the open legs tie up while active; null when unknown (finished runs).</summary>
    public decimal? CapitalUsed { get; set; }
}

/// <summary>
/// One row of GET /api/Strategy/runs/summary — a user's live-run rollup for the
/// history page header (admins see every user, a trader only themselves).
/// </summary>
public class LiveRunUserSummaryResponse
{
    public long UserId { get; set; }
    public string? UserName { get; set; }

    /// <summary>Every LivePaper run the user started.</summary>
    public int Runs { get; set; }

    /// <summary>Runs with a runner process behind them right now.</summary>
    public int Active { get; set; }

    /// <summary>Σ realized P&amp;L over all the user's live runs.</summary>
    public decimal NetPnl { get; set; }

    /// <summary>When the user's newest run started; null when they never started one.</summary>
    public DateTime? LastRunUtc { get; set; }
}
