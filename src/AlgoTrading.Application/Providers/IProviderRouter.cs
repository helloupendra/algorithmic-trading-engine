namespace AlgoTrading.Application.Providers;

/// <summary>
/// Decides <em>which</em> connector serves a given job, from the bindings table
/// rather than from the DI container. Callers ask the router, never the
/// container, so swapping a vendor is a row update — no rebuild, no restart.
/// </summary>
public interface IProviderRouter
{
    /// <summary>
    /// The data providers that can serve this capability, best first. The list is
    /// the failover chain; today's callers use the head of it, and the phase that
    /// adds health monitoring walks further down when the head is unhealthy.
    /// </summary>
    Task<IReadOnlyList<IMarketDataProvider>> ResolveDataChainAsync(
        ProviderCapability capability,
        string? segment = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The provider currently bound to this capability.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is bound and no fallback exists.</exception>
    Task<IMarketDataProvider> ResolveDataAsync(
        ProviderCapability capability,
        string? segment = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The broker for an account — or, when <paramref name="brokerAccountId"/> is
    /// null, the shared platform account. Order routing never fails over
    /// automatically, so this returns exactly one broker or throws.
    /// </summary>
    /// <exception cref="InvalidOperationException">No broker is bound to the account.</exception>
    Task<IBrokerProvider> ResolveBrokerAsync(
        long? brokerAccountId = null,
        CancellationToken cancellationToken = default);
}
