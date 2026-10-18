namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>
/// One holding as a broker reported it, in the desk's own shape whichever broker it came from.
/// </summary>
/// <remarks>
/// The first block is what the broker said. The second is filled by the desk when the snapshot is taken
/// (<c>PortfolioPricing</c>): which listed instrument the ISIN is, the price the holding is valued at and where that
/// price came from, and the previous session's close for the day's change. Storing them with the snapshot keeps a
/// report built from it later saying exactly what it said when it was taken.
/// </remarks>
public sealed record BrokerHolding
{
    /// <summary>The broker's trading symbol as it sent it: "NSE:RELIANCE-EQ" (FYERS), "RELIANCE-EQ" (Angel One), "RELIANCE" (Dhan).</summary>
    public required string Symbol { get; init; }

    /// <summary>NSE or BSE; empty when the broker does not say (Dhan reports "ALL" for a holding on both).</summary>
    public string Exchange { get; init; } = string.Empty;

    public string Isin { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal AvgPrice { get; init; }

    /// <summary>The broker's own last price; null when it reports none (Dhan's holdings carry no price).</summary>
    public decimal? Ltp { get; init; }

    // ---- filled by the desk when the snapshot is taken ----

    /// <summary>The desk's symbol for the instrument the ISIN names ("NSE:RELIANCE-EQ"); null when the instrument master has no such ISIN.</summary>
    public string? CanonicalSymbol { get; init; }

    /// <summary>The instrument's name from the instrument master.</summary>
    public string? Name { get; init; }

    /// <summary>Where <see cref="Ltp"/> came from: <see cref="PriceSources"/>.</summary>
    public string PriceSource { get; init; } = PriceSources.Broker;

    /// <summary>The session of the desk's close the holding is valued at, when <see cref="PriceSource"/> is the desk's close.</summary>
    public DateOnly? PriceDate { get; init; }

    /// <summary>The close of the session before the snapshot's, from the desk's stored daily closes; null when not stored.</summary>
    public decimal? PrevClose { get; init; }

    public DateOnly? PrevCloseDate { get; init; }
}

/// <summary>Where a holding's price came from.</summary>
public static class PriceSources
{
    /// <summary>The broker's own last traded price.</summary>
    public const string Broker = "broker";

    /// <summary>The desk's newest stored daily close (the broker gave no price).</summary>
    public const string DeskClose = "desk-close";

    /// <summary>No price known: the holding is left out of the value and the P&amp;L, and the report says so.</summary>
    public const string None = "none";
}

/// <summary>One open or closed-today position as a broker reported it.</summary>
public sealed record BrokerPosition
{
    public required string Symbol { get; init; }

    /// <summary>The exchange or segment as the broker named it (NSE, NFO, NSE_FNO, MCX...).</summary>
    public string Exchange { get; init; } = string.Empty;

    /// <summary>The broker's product (CNC, INTRADAY, MARGIN, CARRYFORWARD...).</summary>
    public string Product { get; init; } = string.Empty;

    /// <summary>Net quantity: positive long, negative short, zero closed during the day.</summary>
    public decimal Quantity { get; init; }

    public decimal AvgPrice { get; init; }

    public decimal? Ltp { get; init; }

    /// <summary>The broker's own P&amp;L for the position (realised and unrealised together), when it reports one.</summary>
    public decimal? Pnl { get; init; }

    /// <summary>A future or an option (equity, currency or commodity), as opposed to cash equity.</summary>
    public bool IsDerivative { get; init; }
}

/// <summary>The account's money as the broker reported it. A null field is one the broker did not send.</summary>
public sealed record BrokerFunds(decimal? Available, decimal? Used, decimal? Total);

/// <summary>Everything one read of an account returned.</summary>
public sealed record BrokerPortfolio(IReadOnlyList<BrokerHolding> Holdings, IReadOnlyList<BrokerPosition> Positions, BrokerFunds? Funds);

/// <summary>
/// The broker's token and what it must be sent with. Only ever stored encrypted (<see cref="PortfolioTokenVault"/>) and
/// never returned or logged.
/// </summary>
/// <param name="AccessToken">The token the broker issued, or the one the user generated and pasted.</param>
/// <param name="AppId">The app the token was issued to: FYERS sends it in the Authorization header, Angel One as X-PrivateKey.</param>
/// <param name="ClientId">The broker's client id, for the brokers that want it as a header (Dhan).</param>
public sealed record PortfolioToken(string AccessToken, string? AppId = null, string? ClientId = null);

/// <summary>A broker read that failed, in words a user can act on. Never carries the token.</summary>
public sealed class PortfolioBrokerException(string message, bool tokenRejected = false) : Exception(message)
{
    /// <summary>The broker no longer accepts the token: the user has to connect again.</summary>
    public bool TokenRejected { get; } = tokenRejected;
}

/// <summary>
/// The read-only side of one broker: whose account a token opens, and what that account holds. Nothing here can
/// place, change or cancel an order (<c>PortfolioReadOnlyTests</c> checks the source).
/// </summary>
public interface IPortfolioBroker
{
    /// <summary>One of <c>PortfolioBrokers</c>.</summary>
    string Key { get; }

    /// <summary>
    /// The broker's client id for the account the token opens: one read call, which is also the check that the token
    /// works.
    /// </summary>
    /// <exception cref="PortfolioBrokerException">The broker refused or could not be read.</exception>
    Task<string> AccountIdAsync(PortfolioToken token, CancellationToken cancellationToken);

    /// <summary>Holdings, positions and funds, read now.</summary>
    /// <exception cref="PortfolioBrokerException">The broker refused or could not be read.</exception>
    Task<BrokerPortfolio> ReadAsync(PortfolioToken token, CancellationToken cancellationToken);

    /// <summary>When the token stops working: from the token itself, else the broker's documented rule; null when neither says.</summary>
    DateTime? ExpiresUtc(PortfolioToken token, DateTime issuedUtc);
}

/// <summary>Small text rules shared by the portfolio clients.</summary>
public static class PortfolioText
{
    /// <summary>"1100003626" → "••••3626": enough for a person to know which account, too little to be the id.</summary>
    public static string MaskAccount(string? clientId)
    {
        string id = (clientId ?? string.Empty).Trim();
        return id.Length <= 4 ? "••••" : "••••" + id[^4..];
    }
}
