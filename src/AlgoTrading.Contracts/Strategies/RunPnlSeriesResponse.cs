// src/AlgoTrading.Contracts/Strategies/RunPnlSeriesResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// One IST day of live runs' P&amp;L, minute by minute: each run's own series and
/// each account's total. Served by GET /api/Strategy/runs/pnl-series.
/// </summary>
/// <remarks>
/// Series are parallel arrays: point i of a run is <c>Minutes[i]</c>,
/// <c>Net[i]</c> and so on. A minute is counted from 00:00 IST of
/// <see cref="Date"/> (555 is 09:15), so the instant of point i is
/// <see cref="DayStartUtc"/> + <c>Minutes[i]</c> minutes. Rupees throughout,
/// net of charges where it says net.
/// </remarks>
public class RunPnlSeriesResponse
{
    /// <summary>The IST day, yyyy-MM-dd.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>00:00 IST of <see cref="Date"/>, as UTC.</summary>
    public DateTime DayStartUtc { get; set; }

    /// <summary>Every run in scope with at least one point that day, in the order they started.</summary>
    public List<RunPnlSeries> Runs { get; set; } = new();

    /// <summary>One per account with a run in the totals, by user name.</summary>
    public List<AccountPnlSeries> Accounts { get; set; } = new();
}

/// <summary>One run's minutes.</summary>
/// <remarks>
/// A strategy run has a point every minute it was live, and a last one at
/// the minute it ended; a missing minute while it was live means the
/// recorder was not running then. A manual book has a point only when its
/// figures moved.
/// </remarks>
public class RunPnlSeries
{
    public long RunId { get; set; }

    /// <summary>The account the run trades in.</summary>
    public long UserId { get; set; }

    public string? UserName { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    public string Underlying { get; set; } = string.Empty;

    /// <summary>The owner's manual book rather than a strategy run.</summary>
    public bool IsManualBook { get; set; }

    /// <summary>Running | Stopping | Stopped | Failed | Completed, now.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime StartedUtc { get; set; }

    /// <summary>
    /// Whether the account totals include this run: a trading run started on
    /// <see cref="RunPnlSeriesResponse.Date"/> — the runs GET /api/Strategy/runs
    /// lists for that day, which the Desk's day figures are summed from. A
    /// manual book opened on an earlier day is not (its net is its whole life,
    /// not the day), nor is an alert-only run.
    /// </summary>
    public bool InAccountTotals { get; set; }

    /// <summary>Minutes from 00:00 IST of the day, ascending.</summary>
    public List<int> Minutes { get; set; } = new();

    public List<decimal> Realized { get; set; } = new();
    public List<decimal> Unrealized { get; set; } = new();
    public List<decimal> Charges { get; set; } = new();

    /// <summary>Realized + unrealized − charges: what the run card showed at that minute.</summary>
    public List<decimal> Net { get; set; } = new();
}

/// <summary>
/// One account's day, summed over its runs in the totals at every minute any
/// of them has a point. A run counts from its first point, holds its last
/// value between points, and keeps its final value after it ends — a run that
/// stopped at 11:02 still owns what it made.
/// </summary>
public class AccountPnlSeries
{
    public long UserId { get; set; }

    public string? UserName { get; set; }

    /// <summary>How many runs are summed.</summary>
    public int Runs { get; set; }

    public List<int> Minutes { get; set; } = new();
    public List<decimal> Realized { get; set; } = new();
    public List<decimal> Unrealized { get; set; } = new();
    public List<decimal> Charges { get; set; } = new();
    public List<decimal> Net { get; set; } = new();
}
