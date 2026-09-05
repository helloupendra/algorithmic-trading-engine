using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// Resolves jobs to connectors from the <c>provider_bindings</c> table, so
/// switching a vendor is a row update rather than a rebuild.
/// </summary>
/// <remarks>
/// With no bindings configured — a fresh install, or this installation today —
/// the router falls back to the registered providers that claim the capability.
/// That keeps the platform working before anyone opens the console, and means
/// this change ships with no behavioural difference at all.
/// </remarks>
public class ProviderRouter : IProviderRouter
{
    private readonly IProviderRegistry _registry;
    private readonly TradingDbContext _dbContext;

    public ProviderRouter(IProviderRegistry registry, TradingDbContext dbContext)
    {
        _registry = registry;
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<IMarketDataProvider>> ResolveDataChainAsync(
        ProviderCapability capability,
        string? segment = null,
        CancellationToken cancellationToken = default)
    {
        string capabilityName = capability.ToString();

        var bindings = await _dbContext.ProviderBindings
            .AsNoTracking()
            .Where(x => x.IsEnabled && x.Capability == capabilityName)
            .OrderBy(x => x.Priority)
            .ToListAsync(cancellationToken);

        // A binding with no segment covers every segment; a segment-specific
        // binding is only considered for its own segment.
        var applicable = bindings
            .Where(x => x.Segment == null || segment == null ||
                        string.Equals(x.Segment, segment, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var chain = new List<IMarketDataProvider>();

        foreach (var binding in applicable)
        {
            var provider = _registry.DataProviderKeys
                .Any(k => string.Equals(k, binding.ProviderKey, StringComparison.OrdinalIgnoreCase))
                ? _registry.GetDataProvider(binding.ProviderKey)
                : null;

            // A binding that names a connector this build does not ship is stale
            // configuration, not a crash: skip it and let the chain continue.
            if (provider is not null && provider.Descriptor.Capabilities.Supports(capability))
            {
                chain.Add(provider);
            }
        }

        if (chain.Count > 0)
        {
            return chain;
        }

        return _registry.DataProviderKeys
            .Select(_registry.GetDataProvider)
            .Where(x => x.Descriptor.Capabilities.Supports(capability))
            .ToList();
    }

    public async Task<IMarketDataProvider> ResolveDataAsync(
        ProviderCapability capability,
        string? segment = null,
        CancellationToken cancellationToken = default)
    {
        var chain = await ResolveDataChainAsync(capability, segment, cancellationToken);

        return chain.Count > 0
            ? chain[0]
            : throw new InvalidOperationException(
                $"No data provider is bound to '{capability}'" +
                (segment is null ? "." : $" for segment '{segment}'.") +
                " Bind one on the Data Sources page.");
    }

    public async Task<IBrokerProvider> ResolveBrokerAsync(
        long? brokerAccountId = null,
        CancellationToken cancellationToken = default)
    {
        if (brokerAccountId is not null)
        {
            var account = await _dbContext.BrokerAccounts
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == brokerAccountId && x.IsEnabled, cancellationToken)
                ?? throw new InvalidOperationException(
                    $"Broker account {brokerAccountId} does not exist or is disabled.");

            return _registry.GetBrokerProvider(account.ProviderKey);
        }

        // The shared platform account: the row with no owner, or — before anyone
        // has created one — the only broker this build ships.
        var shared = await _dbContext.BrokerAccounts
            .AsNoTracking()
            .Where(x => x.UserId == null && x.IsEnabled)
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (shared is not null)
        {
            return _registry.GetBrokerProvider(shared.ProviderKey);
        }

        var keys = _registry.BrokerProviderKeys;

        return keys.Count == 1
            ? _registry.GetBrokerProvider(keys[0])
            : throw new InvalidOperationException(
                keys.Count == 0
                    ? "This build ships no broker connector."
                    : $"More than one broker is available ({string.Join(", ", keys)}) and no shared platform account is configured.");
    }
}
