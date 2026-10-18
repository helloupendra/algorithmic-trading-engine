namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One platform user's read-only link to their own account at a broker, for the Portfolio page.
/// </summary>
/// <remarks>
/// <para>
/// The row holds a token the broker issued (or one the user generated and pasted), encrypted with ASP.NET Data
/// Protection (<see cref="TokenProtected"/>). It never holds a password, a PIN or a TOTP secret: the user signs in at
/// the broker's own page, and only what the broker hands back is kept. No endpoint returns the token, and it is used
/// only to read holdings, positions and funds.
/// </para>
/// <para>
/// One link per user per broker. Disconnecting deletes the row, and with it the token; the snapshots it produced
/// stay unless the user asks for them to be forgotten too.
/// </para>
/// </remarks>
public class PortfolioConnection
{
    public long Id { get; set; }

    /// <summary>The platform user the account belongs to. Taken from the sign-in token, never from a request.</summary>
    public long UserId { get; set; }

    /// <summary>One of <see cref="PortfolioBrokers"/>.</summary>
    public string Broker { get; set; } = string.Empty;

    /// <summary>One of <see cref="PortfolioConnectionStatus"/>'s stored values: connected, expired or error.</summary>
    public string Status { get; set; } = PortfolioConnectionStatus.Connected;

    /// <summary>The broker's client id masked to its last four characters ("••••4821"), so the page can say which account.</summary>
    public string AccountLabel { get; set; } = string.Empty;

    /// <summary>The broker's token, encrypted. Never returned, never logged.</summary>
    public string TokenProtected { get; set; } = string.Empty;

    public DateTime ConnectedUtc { get; set; }

    /// <summary>When the broker's token stops working, when it can be told; null when the broker does not say.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>When the account was last read, whether or not the read worked.</summary>
    public DateTime? LastSyncUtc { get; set; }

    /// <summary>Why the last read failed, in words for the user; null after a good one.</summary>
    public string? LastError { get; set; }
}

/// <summary>The brokers a user can link for the Portfolio page.</summary>
public static class PortfolioBrokers
{
    public const string Fyers = "fyers";
    public const string Dhan = "dhan";
    public const string Angel = "angel";

    /// <summary>Every broker, in the order the page lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Fyers, Dhan, Angel];

    public static bool IsKnown(string? broker) => broker is not null && All.Contains(broker);

    /// <summary>What the page calls the broker.</summary>
    public static string NameOf(string broker) => broker switch
    {
        Fyers => "FYERS",
        Dhan => "Dhan",
        Angel => "Angel One",
        _ => broker,
    };
}

/// <summary>A link's state as the Portfolio page shows it.</summary>
public static class PortfolioConnectionStatus
{
    /// <summary>Not stored: there is no row.</summary>
    public const string NotConnected = "not_connected";

    public const string Connected = "connected";

    /// <summary>The broker no longer accepts the token: the user connects again.</summary>
    public const string Expired = "expired";

    /// <summary>The last read failed for another reason (the broker was down, an answer could not be read).</summary>
    public const string Error = "error";
}
