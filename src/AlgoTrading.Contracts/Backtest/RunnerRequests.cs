// src/AlgoTrading.Contracts/Backtest/RunnerRequests.cs
using System.Text.Json;

namespace AlgoTrading.Contracts.Backtest;

/// <summary>
/// One equity point posted by the backtest runner (bulk body of
/// POST /api/Simulator/runs/{id}/equity-snapshots). SnapshotUtc is the
/// HISTORICAL bar time, not the wall clock.
/// </summary>
public class EquitySnapshotBatchItem
{
    public DateTime SnapshotUtc { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal UnrealizedPnl { get; set; }

    /// <summary>
    /// Cumulative charges booked so far (flat rupees per lot per fill). Netted
    /// into TotalPnl / CurrentEquity so the curve, the run total and the
    /// runner's SL/target rule all agree. Defaults to 0.
    /// </summary>
    public decimal Charges { get; set; }

    public decimal UsedCapital { get; set; }
    public int OpenPositions { get; set; }
    public int ClosedPositions { get; set; }
}

/// <summary>Body of POST /api/Simulator/runs/{id}/marks: bar-close marks for the run's open positions.</summary>
public class RunMarksRequest
{
    public DateTime AtUtc { get; set; }
    public List<RunMarkItem> Marks { get; set; } = new();
}

public class RunMarkItem
{
    public string Symbol { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

/// <summary>Body of POST /api/Simulator/runs/{id}/progress (registry only, no DB write).</summary>
public class RunProgressRequest
{
    public decimal Percent { get; set; }
    public long BarsProcessed { get; set; }
    public long TotalBars { get; set; }
    public DateTime? CurrentUtc { get; set; }
    public int Trades { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Body of POST /api/Simulator/runs/{id}/complete. <c>Summary</c> is stored
/// verbatim as the BACKTEST_SUMMARY signal's metadata:
/// { totalBars, sessions, trades, skippedEntries: [{atUtc, symbol, reason}],
///   eodSquareOffs, stopReason?, dataNotes: string[] }.
/// </summary>
public class CompleteRunRequest
{
    /// <summary>"Completed" | "Failed"</summary>
    public string Status { get; set; } = string.Empty;

    public string? Error { get; set; }

    public JsonElement? Summary { get; set; }
}
