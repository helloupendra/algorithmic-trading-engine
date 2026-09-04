// src/AlgoTrading.Contracts/Backtest/BacktestRunViewResponse.cs
using AlgoTrading.Contracts.Strategies;

namespace AlgoTrading.Contracts.Backtest;

/// <summary>
/// Everything the backtest results page shows for one run.
/// Served by GET /api/Backtest/runs/{id}.
/// </summary>
public class BacktestRunViewResponse
{
    public long RunId { get; set; }
    public int StrategyId { get; set; }
    public string StrategyName { get; set; } = string.Empty;
    public string Underlying { get; set; } = string.Empty;
    public string SpotSymbol { get; set; } = string.Empty;

    /// <summary>Canonical resolution ("5").</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>IST calendar day, "yyyy-MM-dd".</summary>
    public string FromDate { get; set; } = string.Empty;
    public string ToDate { get; set; } = string.Empty;

    public int Lots { get; set; }
    public int LotSize { get; set; }
    public string LotSizeSource { get; set; } = string.Empty;
    public decimal? StopLoss { get; set; }
    public decimal? Target { get; set; }

    /// <summary>The run's risk rules at all three levels (enforced by the backtest engine).</summary>
    public RiskRulesDto Risk { get; set; } = RiskRulesDto.Empty();

    /// <summary>"HH:MM" IST, or empty when no end-of-day square-off.</summary>
    public string EodSquareOffIst { get; set; } = string.Empty;
    public decimal ChargesPerLot { get; set; }
    public decimal InitialCapital { get; set; }
    public string ParametersJson { get; set; } = "{}";

    /// <summary>Pending | Running | Completed | Failed | Stopped.</summary>
    public string Status { get; set; } = string.Empty;
    public string? LastError { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? StartedBy { get; set; }
    public string? StopReason { get; set; }

    /// <summary>Live progress while running; reconstructed from the summary afterwards; null when unknown.</summary>
    public BacktestProgress? Progress { get; set; }

    public BacktestPnl Pnl { get; set; } = new();
    public BacktestMetrics Metrics { get; set; } = new();

    /// <summary>Per IST calendar day, oldest first.</summary>
    public List<BacktestDailyPnl> Daily { get; set; } = new();

    /// <summary>Open first, then newest first. A closed leg is the same row with quantity 0.</summary>
    public List<BacktestPosition> Positions { get; set; } = new();

    /// <summary>Newest first, at most 400.</summary>
    public List<LiveActivityResponse> Activity { get; set; } = new();

    /// <summary>Skipped entries and other data caveats. Never empty when something was skipped.</summary>
    public List<string> DataNotes { get; set; } = new();

    /// <summary>Oldest first, one point per equity snapshot.</summary>
    public List<BacktestEquityPoint> EquityCurve { get; set; } = new();
}

/// <summary>Runner progress as reported through POST /api/Simulator/runs/{id}/progress.</summary>
public class BacktestProgress
{
    public decimal Percent { get; set; }
    public long BarsProcessed { get; set; }
    public long TotalBars { get; set; }
    public DateTime? CurrentUtc { get; set; }
    public int Trades { get; set; }
    public string? Message { get; set; }
}

/// <summary>Rupee P&amp;L of the run. total = realized + unrealized − charges.</summary>
public class BacktestPnl
{
    public decimal Realized { get; set; }
    public decimal Unrealized { get; set; }
    public decimal Total { get; set; }
    public decimal Charges { get; set; }
    public decimal ReturnPercent { get; set; }

    /// <summary>Peak UsedCapital over the run's equity snapshots (0 when none).</summary>
    public decimal CapitalUsed { get; set; }

    /// <summary>Always 0 for a finished replay (no open legs); kept for shape parity with the live view.</summary>
    public decimal PremiumOutlay { get; set; }

    /// <summary>Always 0 for a finished replay; kept for shape parity with the live view.</summary>
    public decimal PremiumReceived { get; set; }
}

/// <summary>Position-based performance metrics (a "trade" is a closed position).</summary>
public class BacktestMetrics
{
    public int ClosedPositions { get; set; }
    public int Winning { get; set; }
    public int Losing { get; set; }
    public decimal WinRatePercent { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal GrossLoss { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal Expectancy { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public decimal MaxDrawdownAmount { get; set; }
    public decimal LargestWin { get; set; }
    public decimal LargestLoss { get; set; }
    public int TradingDays { get; set; }
    public int ProfitableDays { get; set; }
}

/// <summary>Realized P&amp;L of the positions closed on one IST calendar day.</summary>
public class BacktestDailyPnl
{
    /// <summary>"yyyy-MM-dd" IST.</summary>
    public string Date { get; set; } = string.Empty;
    public decimal Pnl { get; set; }
    public int Trades { get; set; }
}

/// <summary>
/// One paper position of a backtest: the live position shape plus the exit
/// fill and the reason text of the CLOSE_GROUP signal that closed it.
/// </summary>
public class BacktestPosition : LivePositionResponse
{
    /// <summary>Fill price of the closing order; null while open.</summary>
    public decimal? ExitPrice { get; set; }

    /// <summary>Reason carried by the CLOSE_GROUP signal that closed the position, if any.</summary>
    public string? ExitReason { get; set; }
}

/// <summary>One point of the equity curve (historical timestamp).</summary>
public class BacktestEquityPoint
{
    public DateTime AtUtc { get; set; }
    public decimal Equity { get; set; }
    public decimal Realized { get; set; }
    public decimal Unrealized { get; set; }
}
