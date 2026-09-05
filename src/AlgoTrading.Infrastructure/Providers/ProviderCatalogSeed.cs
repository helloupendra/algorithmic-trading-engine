using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// The connectors this build ships, filled in at startup by each adapter's own
/// registration and immutable thereafter.
/// </summary>
/// <remarks>
/// This is only half of the catalog. Vendors an operator adds from the console
/// live in the database and are merged in by <see cref="ProviderCatalog"/>.
/// </remarks>
public sealed class ProviderCatalogSeed
{
    private readonly Dictionary<string, ProviderDescriptor> _byKey =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called by a connector's registration extension at startup.</summary>
    public void Add(ProviderDescriptor descriptor) => _byKey[descriptor.Key] = descriptor;

    public IReadOnlyList<ProviderDescriptor> Descriptors => _byKey.Values.ToList();

    public ProviderDescriptor? Find(string providerKey)
        => _byKey.TryGetValue(providerKey, out var descriptor) ? descriptor : null;

    public bool Contains(string providerKey) => _byKey.ContainsKey(providerKey);
}
