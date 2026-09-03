// src/AlgoTrading.Contracts/Backtest/BacktestRunSummaryResponse.cs
namespace AlgoTrading.Contracts.Backtest;

/// <summary>
/// One row of GET /api/Backtest/runs: an OfflineReplay run with its headline
/// numbers (from one grouped query over its paper positions).
/// </summary>
public class BacktestRunSummaryResponse
{
    public long RunId { get; set; }
    public string StrategyName { get; set; } = string.Empty;
    public int StrategyId { get; set; }
    public string Underlying { get; set; } = string.Empty;
    public string SpotSymbol { get; set; } = string.Empty;

    /// <summary>Canonical resolution ("5").</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>IST calendar day, "yyyy-MM-dd".</summary>
    public string FromDate { get; set; } = string.Empty;
    public string ToDate { get; set; } = string.Empty;

    public int Lots { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? Target { get; set; }

    /// <summary>Pending | Running | Completed | Failed | Stopped.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>From the process registry while running; 100 once completed, else 0.</summary>
    public decimal ProgressPercent { get; set; }

    /// <summary>
    /// Sum of realized P&amp;L over the run's positions minus charges
    /// (chargesPerLot x filled lots) — the same net figure as the detail view's pnl.total for a finished run.
    /// </summary>
    public decimal NetPnl { get; set; }

    /// <summary>Closed positions.</summary>
    public int Trades { get; set; }

    public decimal WinRatePercent { get; set; }

    /// <summary>
    /// Why the replay ended before the range did (stop-loss / target trip, a
    /// user stop, a runner exit); null for a run that replayed every bar.
    /// </summary>
    public string? StopReason { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? StartedBy { get; set; }
    public string? LastError { get; set; }
}
