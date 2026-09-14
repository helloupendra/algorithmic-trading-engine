namespace AlgoTrading.Application.Providers;

/// <summary>
/// A connector whose session starts with a sign-in in the browser, for a vendor
/// that is not (yet) registered as an order-taking broker.
/// </summary>
/// <remarks>
/// The Connectors page's Connect button asks for the login URL through one
/// generic endpoint. Brokers answer it through <see cref="IBrokerProvider"/>; a
/// data connector with a daily browser sign-in answers it here instead, so it can
/// have the same button without joining the broker registry, whose single-broker
/// resolution the FYERS sign-in still depends on.
/// </remarks>
public interface IProviderLoginFlow
{
    /// <summary>The connector this flow signs in to.</summary>
    string ProviderKey { get; }

    /// <summary>A fresh URL on the vendor's site where the operator signs in.</summary>
    Task<string> GetLoginUrlAsync(CancellationToken cancellationToken = default);
}
