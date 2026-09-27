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

    /// <summary>Whose account the run trades in (see StrategyActiveRunResponse.OwnerUserId).</summary>
    public long? OwnerUserId { get; set; }

    public string? OwnerName { get; set; }

    /// <summary>
    /// Whether the person reading this may stop the run, edit its risk rules or
    /// close a leg — the same rule the API enforces. The page used to compare
    /// "started by" with the viewer, so a trader could not control the runs the
    /// morning job had started in their own account.
    /// </summary>
    public bool CanControl { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? StoppedUtc { get; set; }
    public string? StopReason { get; set; }

    /// <summary>
    /// "live", or "recap" for a run trading an evening replay. A recap's
    /// positions, orders and activity are timed by the replayed session, so
    /// they read against that day's chart; StartedUtc and StoppedUtc stay the
    /// real moments the run started and stopped.
    /// </summary>
    public string Session { get; set; } = "live";

    /// <summary>The replayed day (yyyy-MM-dd) of a recap run; null otherwise.</summary>
    public string? RecapDate { get; set; }

    public StrategyPnlSummary Pnl { get; set; } = new();

    /// <summary>Open first, then newest first.</summary>
    public List<LivePositionResponse> Positions { get; set; } = new();

    /// <summary>One row per position group (OPEN_GROUP), groups with open legs first.</summary>
    public List<LiveGroupResponse> Groups { get; set; } = new();

    /// <summary>
    /// What the open legs add up to — theta ₹/day, vega ₹ per 1% IV and net
    /// delta per underlying. Null when nothing open carries a greek (no open
    /// leg, or only legs no source could price).
    /// </summary>
    public RunGreeksTotals? Greeks { get; set; }

    /// <summary>Newest first, at most 60 rows.</summary>
    public List<LiveActivityResponse> Activity { get; set; } = new();

    /// <summary>Present only while the Python runner process is alive.</summary>
    public StrategyRunnerInfo? Runner { get; set; }
}

/// <summary>
/// Realized + unrealized = total (gross), charges, and total − charges = net, in
/// rupees, plus the capital the open legs tie up.
/// </summary>
public class StrategyPnlSummary
{
    public decimal Realized { get; set; }
    public decimal Unrealized { get; set; }

    /// <summary>Realized + unrealized, before charges.</summary>
    public decimal Total { get; set; }

    /// <summary>
    /// Statutory charges of the run's fills so far, the figure the run history
    /// takes off (RunCharges). The charges of closing the open legs are not in
    /// it until they close.
    /// </summary>
    public decimal Charges { get; set; }

    /// <summary>
    /// <see cref="Total"/> − <see cref="Charges"/>. Until 28 Sep the run page and
    /// the live tiles showed the gross beside a history that showed net: one run
    /// read +₹3,000 on its page and +₹2,912 in the history.
    /// </summary>
    public decimal Net { get; set; }

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

    /// <summary>
    /// Lots held while open; for a closed row, the lots that were opened
    /// (replayed from the run's fills; 0 when nothing can be replayed).
    /// </summary>
    public int Lots { get; set; }
    public int LotSize { get; set; }

    /// <summary>lots x lotSize.</summary>
    public int Quantity { get; set; }

    /// <summary>"Open" or "Closed".</summary>
    public string Status { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }

    /// <summary>Fill price of the closing order; null while open.</summary>
    public decimal? ExitPrice { get; set; }

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
    /// <summary>
    /// This position's own stop / target, when the order that opened it carried
    /// them. Null means the run's rules are the only thing watching it.
    /// </summary>
    public decimal? StopLossPrice { get; set; }

    /// <inheritdoc cref="StopLossPrice"/>
    public decimal? TargetPrice { get; set; }

    public decimal? PnlPoints { get; set; }

    /// <summary>pnlPoints / entry × 100.</summary>
    public decimal? PnlPercent { get; set; }

    public DateTime OpenedUtc { get; set; }
    public DateTime? ClosedUtc { get; set; }

    /// <summary>
    /// IV and greeks of an OPEN leg and what they mean for this position in
    /// rupees. Null for a closed leg, in a backtest, and for an open option no
    /// source could price (no quote for it or its underlying).
    /// </summary>
    public PositionGreeksResponse? Greeks { get; set; }
}

/// <summary>
/// The greeks of one open leg, per unit and for the position.
/// </summary>
/// <remarks>
/// Per unit, in premium points, in the conventions every source on the
/// platform already publishes: theta per calendar day, vega per one point of
/// IV (per 1%). The position figures multiply by the quantity (lots × lot
/// size) and by the side: a long option pays its theta, a short one collects
/// it, so ThetaRupeesPerDay is negative for a buyer and positive for a writer.
/// </remarks>
public class PositionGreeksResponse
{
    /// <summary>
    /// Where the figures came from: "feed" (carried by the contract's live
    /// quote), "chain" (the option-chain recorder's latest snapshot),
    /// "computed" (Black-Scholes, IV solved from the option's own price), or
    /// "delta-one" (a future or a share: delta 1, nothing else).
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The moment the figures describe: the quote, the snapshot, or the older of the two prices a computation used.</summary>
    public DateTime? AsOfUtc { get; set; }

    /// <summary>True when <see cref="AsOfUtc"/> is too old to be read as now (see PositionGreeks.FreshFor).</summary>
    public bool Stale { get; set; }

    /// <summary>Implied volatility in percent (13.6 is 13.6%).</summary>
    public decimal? IvPercent { get; set; }

    public decimal Delta { get; set; }
    public decimal Gamma { get; set; }

    /// <summary>Premium points per calendar day, per unit (negative: time costs the holder).</summary>
    public decimal Theta { get; set; }

    /// <summary>Premium points per one point of IV, per unit.</summary>
    public decimal Vega { get; set; }

    /// <summary>The underlying price a computation used (source "computed" only).</summary>
    public decimal? UnderlyingPrice { get; set; }

    /// <summary>delta × quantity × side: the position's size in units of the underlying.</summary>
    public decimal DeltaQuantity { get; set; }

    /// <summary>Rupees the position makes on a one-point rise in the underlying (numerically the same as DeltaQuantity).</summary>
    public decimal DeltaRupeesPerPoint { get; set; }

    /// <summary>theta × quantity × side: rupees a day of time is worth to this position.</summary>
    public decimal ThetaRupeesPerDay { get; set; }

    /// <summary>vega × quantity × side: rupees one point of IV is worth to this position.</summary>
    public decimal VegaRupeesPerIvPoint { get; set; }
}

/// <summary>The open legs' greeks, summed for a run or the manual book.</summary>
public class RunGreeksTotals
{
    /// <summary>Σ theta ₹/day — negative when the book pays for time, positive when it collects.</summary>
    public decimal ThetaRupeesPerDay { get; set; }

    /// <summary>Σ vega ₹ per one point of IV.</summary>
    public decimal VegaRupeesPerIvPoint { get; set; }

    /// <summary>
    /// Net delta of the whole book in units of its underlying; null when the
    /// book holds more than one underlying, where units do not add up (a
    /// NIFTY point and a crude rupee are not the same move). See ByUnderlying.
    /// </summary>
    public decimal? NetDeltaQuantity { get; set; }

    /// <summary>Net delta per underlying, largest exposure first.</summary>
    public List<UnderlyingDeltaTotal> ByUnderlying { get; set; } = new();

    /// <summary>Open legs whose greeks are in the sums.</summary>
    public int Legs { get; set; }

    /// <summary>Open option legs no source could price: the sums leave them out.</summary>
    public int Unpriced { get; set; }

    /// <summary>True when any leg in the sums is stale.</summary>
    public bool Stale { get; set; }

    /// <summary>The oldest AsOfUtc among the legs in the sums.</summary>
    public DateTime? OldestAsOfUtc { get; set; }
}

/// <summary>Net delta of one underlying's legs.</summary>
public class UnderlyingDeltaTotal
{
    public string Underlying { get; set; } = string.Empty;
    public decimal DeltaQuantity { get; set; }
    public decimal DeltaRupeesPerPoint { get; set; }
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
