using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// Every connector the build ships, indexed by key. Which one is actually used
/// is not decided here — that is <see cref="ProviderRouter"/>'s job, from the
/// bindings table.
/// </summary>
public class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IMarketDataProvider> _dataProviders;
    private readonly Dictionary<string, IBrokerProvider> _brokerProviders;

    public ProviderRegistry(
        IEnumerable<IMarketDataProvider> dataProviders,
        IEnumerable<IBrokerProvider> brokerProviders)
    {
        _dataProviders = dataProviders.ToDictionary(
            x => x.Descriptor.Key,
            StringComparer.OrdinalIgnoreCase);

        _brokerProviders = brokerProviders.ToDictionary(
            x => x.Descriptor.Key,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> DataProviderKeys => _dataProviders.Keys.OrderBy(x => x).ToList();

    public IReadOnlyList<string> BrokerProviderKeys => _brokerProviders.Keys.OrderBy(x => x).ToList();

    public IMarketDataProvider GetDataProvider(string providerKey)
        => _dataProviders.TryGetValue(providerKey, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"No data provider is registered under '{providerKey}'. Registered: {string.Join(", ", DataProviderKeys)}.");

    public IBrokerProvider GetBrokerProvider(string providerKey)
        => _brokerProviders.TryGetValue(providerKey, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"No broker is registered under '{providerKey}'. Registered: {string.Join(", ", BrokerProviderKeys)}.");
}
