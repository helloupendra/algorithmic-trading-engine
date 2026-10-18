namespace AlgoTrading.Domain.Entities;

/// <summary>
/// What one broker said one user's account held at one moment: holdings, positions and funds, normalised to one shape
/// for every broker and stored as JSON.
/// </summary>
/// <remarks>
/// <para>
/// Written only when a read succeeds, so a row is always a complete answer from the broker. The holdings carry the
/// price the account was valued at and where it came from (the broker's own last price, or the desk's stored daily
/// close when the broker gives none), so a report built from the row later says the same thing it said when it was
/// taken.
/// </para>
/// <para>
/// Kept for 90 days (<c>PortfolioRetentionService</c>), and deleted at once when the user disconnects the broker
/// with <c>forget</c>. The written summary of a report is cached on the newest row it was built from
/// (<see cref="SummaryJson"/>), so viewing the page does not ask a model each time.
/// </para>
/// </remarks>
public class PortfolioSnapshot
{
    public long Id { get; set; }

    public long UserId { get; set; }

    /// <summary>One of <see cref="PortfolioBrokers"/>.</summary>
    public string Broker { get; set; } = string.Empty;

    public DateTime TakenUtc { get; set; }

    public string HoldingsJson { get; set; } = "[]";

    public string PositionsJson { get; set; } = "[]";

    /// <summary>The account's money as the broker reported it; "null" when the broker's answer had none.</summary>
    public string FundsJson { get; set; } = "null";

    /// <summary>The snapshots a cached summary was written from (their ids, sorted, comma separated); null when none is cached.</summary>
    public string? SummaryKey { get; set; }

    /// <summary>The cached summary (text, model, when) or the reason there is none; null when none was written.</summary>
    public string? SummaryJson { get; set; }
}
