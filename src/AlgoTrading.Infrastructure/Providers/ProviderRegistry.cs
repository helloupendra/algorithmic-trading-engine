using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Csv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// The live adapter instances, by key: the ones this build ships (resolved from
/// the container) and the file-based vendors an operator added (constructed on
/// demand from their row).
/// </summary>
public class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<string, IMarketDataProvider> _dataProviders;
    private readonly Dictionary<string, IBrokerProvider> _brokerProviders;
    private readonly TradingDbContext _dbContext;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Built per vendor per scope; cheap, and keeps a scope consistent.</summary>
    private readonly Dictionary<string, IMarketDataProvider> _vendorProviders =
        new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(
        IEnumerable<IMarketDataProvider> dataProviders,
        IEnumerable<IBrokerProvider> brokerProviders,
        TradingDbContext dbContext,
        ILoggerFactory loggerFactory)
    {
        _dataProviders = dataProviders.ToDictionary(
            x => x.Descriptor.Key,
            StringComparer.OrdinalIgnoreCase);

        _brokerProviders = brokerProviders.ToDictionary(
            x => x.Descriptor.Key,
            StringComparer.OrdinalIgnoreCase);

        _dbContext = dbContext;
        _loggerFactory = loggerFactory;
    }

    public IReadOnlyList<string> DataProviderKeys
        => _dataProviders.Keys
            .Concat(EnabledVendors().Select(x => x.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x)
            .ToList();

    public IReadOnlyList<string> BrokerProviderKeys => _brokerProviders.Keys.OrderBy(x => x).ToList();

    public IMarketDataProvider GetDataProvider(string providerKey)
    {
        if (_dataProviders.TryGetValue(providerKey, out var shipped))
        {
            return shipped;
        }

        if (_vendorProviders.TryGetValue(providerKey, out var cached))
        {
            return cached;
        }

        var vendor = EnabledVendors()
            .FirstOrDefault(x => string.Equals(x.Key, providerKey, StringComparison.OrdinalIgnoreCase));

        if (vendor is null)
        {
            throw new InvalidOperationException(
                $"No data provider is registered under '{providerKey}'. Registered: {string.Join(", ", DataProviderKeys)}.");
        }

        var provider = new CsvMarketDataProvider(
            CsvVendorDescriptor.For(vendor),
            vendor.Directory,
            _loggerFactory.CreateLogger($"DataVendor.{vendor.Key}"));

        _vendorProviders[vendor.Key] = provider;

        return provider;
    }

    public IBrokerProvider GetBrokerProvider(string providerKey)
        => _brokerProviders.TryGetValue(providerKey, out var provider)
            ? provider
            : throw new InvalidOperationException(
                $"No broker is registered under '{providerKey}'. Registered: {string.Join(", ", BrokerProviderKeys)}.");

    private List<Domain.Entities.DataVendor> EnabledVendors()
        => _dbContext.DataVendors.AsNoTracking().Where(x => x.IsEnabled).ToList();
}
