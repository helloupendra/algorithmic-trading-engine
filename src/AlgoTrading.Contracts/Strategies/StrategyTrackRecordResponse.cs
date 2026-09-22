// src/AlgoTrading.Contracts/Strategies/StrategyTrackRecordResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// GET /api/Strategy/{id}/track-record — what one strategy has actually done
/// across every live run it has ever had, rather than the trailing window the
/// library card shows. The runs themselves are still read from
/// GET /api/Strategy/runs?strategyId=…; this is the rollup over all of them,
/// so a page can state the record without first paging the whole history.
/// </summary>
/// <remarks>
/// Backtests are deliberately absent. A backtest is a hypothesis and a live run
/// is a result, and adding the two would make the record say something neither
/// number supports. Backtest history lives under /api/Backtest/runs.
/// </remarks>
public class StrategyTrackRecordResponse
{
    /// <summary>Catalog id asked for (a stable hash of the name for a strategy the catalog has since lost).</summary>
    public int StrategyId { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    /// <summary>
    /// "own" when these numbers cover a single user's runs — always so for a
    /// trader, whose history is their own by construction — and "all" when they
    /// cover every user's. The console says which, because the same strategy
    /// reads differently to its one user than to the desk.
    /// </summary>
    public string Scope { get; set; } = ScopeAll;

    /// <summary>The user the numbers are scoped to; null when they cover everyone.</summary>
    public long? ScopeUserId { get; set; }

    public const string ScopeOwn = "own";
    public const string ScopeAll = "all";

    // ---------------------------------------------------------------- counts

    /// <summary>Every live run of this strategy in scope, however it ended.</summary>
    public int Runs { get; set; }

    /// <summary>Runs with a runner process behind them right now.</summary>
    public int ActiveRuns { get; set; }

    /// <summary>Runs that could place orders (everything that is not an alerter).</summary>
    public int TradingRuns { get; set; }

    /// <summary>Alerter runs — they place no orders, so they carry no P&amp;L and are kept out of every figure below.</summary>
    public int AlertRuns { get; set; }

    /// <summary>
    /// Finished trading runs: the only ones whose P&amp;L is settled, and so the
    /// only ones a win or a loss is counted over. A run still going is not yet
    /// either.
    /// </summary>
    public int DecidedRuns { get; set; }

    public int Wins { get; set; }
    public int Losses { get; set; }

    /// <summary>Finished runs that ended exactly flat — usually ones that never opened a position.</summary>
    public int Flat { get; set; }

    /// <summary>Wins as a percentage of <see cref="DecidedRuns"/>; null until one run has finished.</summary>
    public double? WinRate { get; set; }

    // ------------------------------------------------------------------ P&amp;L

    /// <summary>Σ realized P&amp;L over every trading run in scope, finished or not.</summary>
    public decimal NetPnl { get; set; }

    /// <summary>Σ unrealized P&amp;L of the runs still going, at the latest mark; 0 when none are.</summary>
    public decimal OpenPnl { get; set; }

    /// <summary>Σ P&amp;L of the finished runs that made money.</summary>
    public decimal GrossProfit { get; set; }

    /// <summary>Σ P&amp;L of the finished runs that lost money — negative.</summary>
    public decimal GrossLoss { get; set; }

    /// <summary>Realized P&amp;L per finished run; 0 until one has finished.</summary>
    public decimal AveragePnlPerRun { get; set; }

    /// <summary>Closed positions over every run in scope.</summary>
    public int Trades { get; set; }

    /// <summary>The finished run that made the most, and the one that lost the most; null until one has finished.</summary>
    public StrategyTrackRecordRunRef? BestRun { get; set; }

    public StrategyTrackRecordRunRef? WorstRun { get; set; }

    /// <summary>
    /// True while the P&amp;L above is gross of brokerage, STT and slippage —
    /// live runs carry no charges today (LiveRunSummaryResponse.ChargesPerLot
    /// is 0). The console shows its caveat from this flag alone, so the day
    /// charges are netted in, the caveat goes away with them.
    /// </summary>
    public bool PnlIsGrossOfCharges { get; set; } = true;

    // ----------------------------------------------------------------- when

    /// <summary>When the first and the newest run of this strategy started; null when it has never run.</summary>
    public DateTime? FirstRunUtc { get; set; }

    public DateTime? LastRunUtc { get; set; }

    /// <summary>Distinct IST calendar days a run started on — how many days this strategy has actually been out.</summary>
    public int TradingDays { get; set; }

    /// <summary>Seconds every run in scope has been alive, added up.</summary>
    public long TotalRuntimeSeconds { get; set; }

    /// <summary>Mean seconds a run stayed alive; null when none has.</summary>
    public long? AverageRuntimeSeconds { get; set; }

    // ------------------------------------------------------------ breakdowns

    /// <summary>The record per underlying, biggest P&amp;L first — a strategy that pays on one index and not another says so here.</summary>
    public List<StrategyTrackRecordUnderlying> ByUnderlying { get; set; } = new();

    /// <summary>How the finished runs ended, most common first. The reasons that repeat are the ones worth reading.</summary>
    public List<StrategyTrackRecordStopReason> StopReasons { get; set; } = new();
}

/// <summary>One run named by a rollup — enough to label it and link to it.</summary>
public class StrategyTrackRecordRunRef
{
    public long RunId { get; set; }
    public string Underlying { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public decimal NetPnl { get; set; }
}

/// <summary>The strategy's record on one underlying.</summary>
public class StrategyTrackRecordUnderlying
{
    public string Underlying { get; set; } = string.Empty;
    public int Runs { get; set; }

    /// <summary>Finished trading runs on this underlying, and how many of them made money.</summary>
    public int DecidedRuns { get; set; }

    public int Wins { get; set; }
    public decimal NetPnl { get; set; }
}

/// <summary>How often the strategy's runs ended one way, and what those runs came to.</summary>
public class StrategyTrackRecordStopReason
{
    /// <summary>The reason's short form — "Stop loss hit", "Target hit", "Market closed", "Runner exited".</summary>
    public string Reason { get; set; } = string.Empty;

    public int Runs { get; set; }

    /// <summary>Σ realized P&amp;L of the runs that ended this way.</summary>
    public decimal NetPnl { get; set; }
}
