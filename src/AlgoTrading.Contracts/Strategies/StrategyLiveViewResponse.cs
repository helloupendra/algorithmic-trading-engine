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
    public decimal? StopLoss { get; set; }
    public decimal? Target { get; set; }

    public string? StartedBy { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? StoppedUtc { get; set; }
    public string? StopReason { get; set; }

    public StrategyPnlSummary Pnl { get; set; } = new();

    /// <summary>Open first, then newest first.</summary>
    public List<LivePositionResponse> Positions { get; set; } = new();

    /// <summary>Newest first, at most 60 rows.</summary>
    public List<LiveActivityResponse> Activity { get; set; } = new();

    /// <summary>Present only while the Python runner process is alive.</summary>
    public StrategyRunnerInfo? Runner { get; set; }
}

/// <summary>Realized + unrealized = total, in rupees.</summary>
public class StrategyPnlSummary
{
    public decimal Realized { get; set; }
    public decimal Unrealized { get; set; }
    public decimal Total { get; set; }
}

/// <summary>Process details of a live runner.</summary>
public class StrategyRunnerInfo
{
    public int ProcessId { get; set; }
    public DateTime? LastLogUtc { get; set; }
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
}
