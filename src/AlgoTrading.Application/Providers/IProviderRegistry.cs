namespace AlgoTrading.Application.Providers;

/// <summary>
/// The live adapter instances, by key. The registry is compile-time (one entry
/// per adapter shipped); which of them is actually <em>used</em> is a runtime
/// decision made by <see cref="IProviderRouter"/> from the bindings table.
/// Metadata without instances lives in <see cref="IProviderCatalog"/>.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>The data provider registered under this key.</summary>
    /// <exception cref="InvalidOperationException">No data provider is registered under the key.</exception>
    IMarketDataProvider GetDataProvider(string providerKey);

    /// <summary>The broker registered under this key.</summary>
    /// <exception cref="InvalidOperationException">No broker is registered under the key.</exception>
    IBrokerProvider GetBrokerProvider(string providerKey);

    /// <summary>Keys of the registered data providers.</summary>
    IReadOnlyList<string> DataProviderKeys { get; }

    /// <summary>Keys of the registered brokers.</summary>
    IReadOnlyList<string> BrokerProviderKeys { get; }
}
