using System;

namespace AlgoTrading.Contracts.Trader;

/// <summary>
/// A trader's own broker account, as the Account page shows it.
/// </summary>
/// <remarks>
/// Separate from the platform's shared FYERS account on purpose. The platform
/// account feeds every strategy; a trader's account is theirs: their funds,
/// holdings, positions and order book, and — once live execution exists —
/// their orders. The two never share a session row.
/// </remarks>
public class TraderBrokerStatus
{
    public string ProviderKey { get; set; } = "fyers";
    public string ProviderName { get; set; } = "FYERS";

    /// <summary>App credentials are saved (client id and secret); nothing is linked until they sign in.</summary>
    public bool Configured { get; set; }
    public string? ClientId { get; set; }
    public string? RedirectUri { get; set; }
    public bool HasTradingPin { get; set; }

    /// <summary>A broker session exists and its token has not passed 06:00 IST.</summary>
    public bool IsAuthenticated { get; set; }
    public DateTime? SignedInUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>The redirect URL the trader must register in their FYERS app — this server's callback.</summary>
    public string CallbackUrl { get; set; } = string.Empty;
}

public class SaveTraderBrokerRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string? RedirectUri { get; set; }
    public string? TradingPin { get; set; }
}
