// src/AlgoTrading.Contracts/Strategies/OrdersResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// One IST day of orders across every run and manual book the caller may see,
/// newest first, a page at a time. Served by GET /api/Orders.
/// </summary>
/// <remarks>
/// Two kinds of row. An <c>order</c> is a paper order the engine booked (its
/// status is the order's own, "Filled" for every order booked today). A
/// <c>rejection</c> is an order the risk gate refused (kill switch, order
/// rate, daily loss), read from its <c>OrderRejected</c> risk event, with
/// status "Rejected" and the gate's reason. A signal refused for another
/// reason (a stale quote, a stopped run) books nothing and is not listed.
/// </remarks>
public class OrdersResponse
{
    /// <summary>The IST day, yyyy-MM-dd.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>00:00 IST of that day, as UTC.</summary>
    public DateTime DayStartUtc { get; set; }

    /// <summary>"LivePaper" (live runs and manual books) or "OfflineReplay" (backtests).</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>Rows matching every filter; the page is <see cref="Skip"/> onward.</summary>
    public int Total { get; set; }

    public int Skip { get; set; }

    public int Take { get; set; }

    /// <summary>Newest first: placed (or refused) time, then id.</summary>
    public List<OrderRowResponse> Orders { get; set; } = new();

    /// <summary>
    /// The day's rows by status over every account the caller may see, before
    /// the account, run, status and symbol filters: what a status filter can
    /// offer.
    /// </summary>
    public List<OrderStatusCount> Statuses { get; set; } = new();

    /// <summary>
    /// Every run with a row on the day, over every account the caller may see,
    /// before the account, run, status and symbol filters: what an account or
    /// run filter can offer, with each run's counts. By account, then run id.
    /// </summary>
    public List<OrderRunFacet> Runs { get; set; } = new();
}

public class OrderRowResponse
{
    /// <summary>"order" (a booked paper order) or "rejection" (refused by the risk gate).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The paper order's id, or the rejection's risk event id: unique within its <see cref="Kind"/>.</summary>
    public long Id { get; set; }

    /// <summary>When it was placed, or refused. A replay's order is stamped with its bar's time.</summary>
    public DateTime AtUtc { get; set; }

    public DateTime? FilledUtc { get; set; }

    public long RunId { get; set; }

    /// <summary>The run's strategy; "Manual" for a manual book.</summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>The run's underlying (NIFTY, CRUDEOIL, ...); null for a manual book, which holds anything.</summary>
    public string? Underlying { get; set; }

    public bool IsManualBook { get; set; }

    /// <summary>The account the run belongs to.</summary>
    public long UserId { get; set; }

    public string? UserName { get; set; }

    public string Symbol { get; set; } = string.Empty;

    /// <summary>"BUY" or "SELL"; null on a rejection recorded before its side was (28 Sep).</summary>
    public string? Side { get; set; }

    /// <summary>
    /// Lots, as paper_orders stores the quantity: a strategy leg's and the
    /// manual ticket's quantity is in lots, and a share's lot size is 1, so
    /// for a share this is shares. Null on a rejection recorded before its
    /// size was.
    /// </summary>
    public int? Lots { get; set; }

    /// <summary>
    /// The lot size the row is read at: the contract's current one for a live
    /// run (as its run card reads it), the run's frozen one for a backtest.
    /// </summary>
    public int? LotSize { get; set; }

    /// <summary>Units: <see cref="Lots"/> × <see cref="LotSize"/>.</summary>
    public int? Quantity { get; set; }

    /// <summary>"MARKET_SIM" or "LIMIT_SIM"; null on a rejection.</summary>
    public string? OrderType { get; set; }

    /// <summary>"Filled" (or an older order's "Pending" / "Cancelled"); "Rejected" on a rejection.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>What the runner or the ticket asked for; the fill can differ by the spread or the move since.</summary>
    public decimal? RequestedPrice { get; set; }

    public decimal? FillPrice { get; set; }

    /// <summary>
    /// How the fill was priced (paper_orders.MetadataJson): <c>bid</c>,
    /// <c>ask</c>, <c>ltp-less-half-spread</c>, <c>ltp-plus-half-spread</c>,
    /// <c>signal</c>, <c>signal-…</c>, <c>mark-…</c>, <c>entry-…</c>. Null on
    /// orders filled before it was recorded (28 Sep) and on rejections.
    /// </summary>
    public string? PriceRule { get; set; }

    /// <summary>The same as a sentence, as the run views show it: "filled at the bid", "LTP 101.20 less half-spread (0.15%)".</summary>
    public string? PriceNote { get; set; }

    /// <summary>Age of the quote the fill was priced from, when it came from one.</summary>
    public double? QuoteAgeSeconds { get; set; }

    /// <summary>Priced on a quote older than a fill may use in the session (a square-off always fills).</summary>
    public bool StaleQuote { get; set; }

    public string? GroupId { get; set; }

    public long? SignalId { get; set; }

    /// <summary>The runner's id for the signal, the same on every retry of it.</summary>
    public string? ClientSignalId { get; set; }

    /// <summary>Why the risk gate refused it; null on an order.</summary>
    public string? Reason { get; set; }
}

public class OrderStatusCount
{
    public string Status { get; set; } = string.Empty;

    public int Orders { get; set; }
}

public class OrderRunFacet
{
    public long RunId { get; set; }

    public long UserId { get; set; }

    public string? UserName { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    /// <summary>Null for a manual book.</summary>
    public string? Underlying { get; set; }

    public bool IsManualBook { get; set; }

    /// <summary>The run's own status now (Running, Stopped, ...).</summary>
    public string RunStatus { get; set; } = string.Empty;

    /// <summary>Its rows on the day, rejections included.</summary>
    public int Orders { get; set; }

    public int Filled { get; set; }

    public int Rejected { get; set; }
}
