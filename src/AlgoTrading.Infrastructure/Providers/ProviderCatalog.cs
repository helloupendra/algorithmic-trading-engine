using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Csv;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// Every connector the platform knows about: the adapters this build ships, plus
/// the file-based vendors an operator added from the console.
/// </summary>
/// <remarks>
/// Descriptors only — no adapter instances are constructed here, because an
/// adapter asks the symbol mapper which asks this catalog, and resolving
/// instances would make that graph circular.
/// </remarks>
public sealed class ProviderCatalog : IProviderCatalog
{
    private readonly ProviderCatalogSeed _seed;
    private readonly TradingDbContext _dbContext;

    /// <summary>Read once per scope: a request must see a consistent catalog.</summary>
    private List<ProviderDescriptor>? _all;

    public ProviderCatalog(ProviderCatalogSeed seed, TradingDbContext dbContext)
    {
        _seed = seed;
        _dbContext = dbContext;
    }

    /// <summary>
    /// Ordered the way the platform itself prefers them — live vendors first,
    /// offline sources last — so the console lists connectors in the same order
    /// as the routing chain below them rather than alphabetically.
    /// </summary>
    public IReadOnlyList<ProviderDescriptor> Descriptors => Load();

    public ProviderDescriptor? Find(string providerKey)
        => Load().FirstOrDefault(x => string.Equals(x.Key, providerKey, StringComparison.OrdinalIgnoreCase));

    private List<ProviderDescriptor> Load()
    {
        if (_all is not null) return _all;

        var vendors = _dbContext.DataVendors
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .ToList();

        var descriptors = new List<ProviderDescriptor>(_seed.Descriptors);

        foreach (var vendor in vendors)
        {
            // A shipped adapter always wins the key: the API refuses to create a
            // vendor that collides, but an older row must not shadow one either.
            if (_seed.Contains(vendor.Key)) continue;

            descriptors.Add(CsvVendorDescriptor.For(vendor));
        }

        _all = descriptors
            .OrderBy(x => x.FallbackRank)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _all;
    }
}
