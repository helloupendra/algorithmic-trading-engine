// src/AlgoTrading.Contracts/Strategies/StrategyLiveViewResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// Everything the Live Runner card shows for one strategy: run configuration,
/// spot, P&amp;L, position-based trade list and recent activity.
/// Served by GET /api/Strategy/{id}/live.
/// </summary>
public class StrategyLiveViewResponse
{
    public int StrategyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public long? RunId { get; set; }

    public string? Underlying { get; set; }
    public string? SpotSymbol { get; set; }
    public decimal? SpotLtp { get; set; }
    public DateTime? SpotUpdatedUtc { get; set; }

    public int? Lots { get; set; }
    public int? LotSize { get; set; }
    public string? LotSizeSource { get; set; }
    /// <summary>Overall rupee stop-loss (the legacy shorthand for <see cref="Risk"/>.overall.stopLoss).</summary>
    public decimal? StopLoss { get; set; }

    /// <summary>Overall rupee target (the legacy shorthand for <see cref="Risk"/>.overall.target).</summary>
    public decimal? Target { get; set; }

    /// <summary>The run's risk rules at all three levels; every level present, unset values null.</summary>
    public RiskRulesDto Risk { get; set; } = RiskRulesDto.Empty();

    public string? StartedBy { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? StoppedUtc { get; set; }
    public string? StopReason { get; set; }

    public StrategyPnlSummary Pnl { get; set; } = new();

    /// <summary>Open first, then newest first.</summary>
    public List<LivePositionResponse> Positions { get; set; } = new();

    /// <summary>One row per position group (OPEN_GROUP), groups with open legs first.</summary>
    public List<LiveGroupResponse> Groups { get; set; } = new();

    /// <summary>Newest first, at most 60 rows.</summary>
    public List<LiveActivityResponse> Activity { get; set; } = new();

    /// <summary>Present only while the Python runner process is alive.</summary>
    public StrategyRunnerInfo? Runner { get; set; }
}

/// <summary>Realized + unrealized = total, in rupees, plus the capital the open legs tie up.</summary>
public class StrategyPnlSummary
{
    public decimal Realized { get; set; }
    public decimal Unrealized { get; set; }
    public decimal Total { get; set; }

    /// <summary>Portfolio UsedCapital: premium paid on open BUY legs + margin heuristic on open SELL legs.</summary>
    public decimal CapitalUsed { get; set; }

    /// <summary>Σ entryValue of the open BUY legs (premium paid).</summary>
    public decimal PremiumOutlay { get; set; }

    /// <summary>Σ entryValue of the open SELL legs (premium received).</summary>
    public decimal PremiumReceived { get; set; }
}

/// <summary>P&amp;L of one position group: realized of all its legs + unrealized of its open legs.</summary>
public class LiveGroupResponse
{
    public string GroupId { get; set; } = string.Empty;
    public decimal Pnl { get; set; }
    public int OpenLegs { get; set; }
    public int ClosedLegs { get; set; }
}

/// <summary>Process details of a live runner.</summary>
public class StrategyRunnerInfo
{
    public int ProcessId { get; set; }
    public DateTime? LastLogUtc { get; set; }

    /// <summary>True when the runner was adopted after an API restart (its output is not captured).</summary>
    public bool Adopted { get; set; }
}

/// <summary>
/// One paper position. Closed rows keep their realized P&amp;L and show lots 0 / quantity 0.
/// </summary>
public class LivePositionResponse
{
    public long Id { get; set; }
    public string GroupId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public ContractInfo Contract { get; set; } = new();

    /// <summary>"BUY" (long) or "SELL" (short).</summary>
    public string Side { get; set; } = string.Empty;

    public int Lots { get; set; }
    public int LotSize { get; set; }

    /// <summary>lots x lotSize.</summary>
    public int Quantity { get; set; }

    /// <summary>"Open" or "Closed".</summary>
    public string Status { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }
    public decimal? Ltp { get; set; }
    public DateTime? LtpUpdatedUtc { get; set; }

    /// <summary>Unrealized while open, realized once closed.</summary>
    public decimal Pnl { get; set; }

    /// <summary>
    /// entry × quantity (lots × lot size). For a closed row, the quantity that
    /// was opened; null when that cannot be reconstructed from the run's orders.
    /// </summary>
    public decimal? EntryValue { get; set; }

    /// <summary>ltp × quantity for open rows; null once closed or when no mark is known.</summary>
    public decimal? CurrentValue { get; set; }

    /// <summary>Signed premium points from entry (sign = profit): BUY ltp − entry, SELL entry − ltp.</summary>
    public decimal? PnlPoints { get; set; }

    /// <summary>pnlPoints / entry × 100.</summary>
    public decimal? PnlPercent { get; set; }

    public DateTime OpenedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }
}

/// <summary>Decoded option contract for display.</summary>
public class ContractInfo
{
    public string Underlying { get; set; } = string.Empty;
    public decimal? Strike { get; set; }
    public string OptionType { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>"BANKNIFTY 57600 CE · 29 Sep"</summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>One signal of the run, newest first.</summary>
public class LiveActivityResponse
{
    public DateTime AtUtc { get; set; }

    /// <summary>The signal type: OPEN_GROUP, CLOSE_GROUP, RUN_STOPPED...</summary>
    public string Type { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// The signal's raw metadata for rows the client renders itself
    /// (RISK_UPDATED carries <c>{ risk, by }</c>); null for every other row.
    /// </summary>
    public string? MetadataJson { get; set; }
}
