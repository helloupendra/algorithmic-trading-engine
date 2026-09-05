namespace AlgoTrading.Application.Providers;

/// <summary>
/// The outcome of exchanging an auth code for a session. Vendor JSON never
/// leaves the adapter — the platform sees tokens or a reason, nothing else.
/// </summary>
/// <param name="AccessToken">Empty when the exchange failed.</param>
/// <param name="RefreshToken">Empty when the vendor issues none.</param>
/// <param name="ErrorMessage">Set only when <paramref name="AccessToken"/> is empty.</param>
public sealed record BrokerTokenResult(
    string AccessToken,
    string RefreshToken,
    string? ErrorMessage = null)
{
    public bool Succeeded => !string.IsNullOrWhiteSpace(AccessToken);

    public static BrokerTokenResult Failed(string reason) => new(string.Empty, string.Empty, reason);
}

/// <summary>
/// The execution side of a connector: who the platform logs in to, and (from the
/// phase that introduces order routing) who receives the orders.
/// </summary>
/// <remarks>
/// Only the session surface is declared here, because that is all the platform
/// asks a broker for today — live orders still go out through the Python engine.
/// Placing, modifying and cancelling join this interface in the phase that gives
/// them a caller; an interface full of methods nobody implements is worse than a
/// small honest one.
/// </remarks>
public interface IBrokerProvider
{
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// The vendor's hosted-login URL for the operator's browser. The vendor
    /// redirects back to our callback with an auth code.
    /// </summary>
    /// <exception cref="InvalidOperationException">Credentials for this provider are not configured.</exception>
    Task<string> GetAuthUrlAsync(string? state = null, CancellationToken cancellationToken = default);

    /// <summary>Exchanges the callback's auth code for a usable session.</summary>
    Task<BrokerTokenResult> ExchangeAuthCodeAsync(string authCode, CancellationToken cancellationToken = default);
}
