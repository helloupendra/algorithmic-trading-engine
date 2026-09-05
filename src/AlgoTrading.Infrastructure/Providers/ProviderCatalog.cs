using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// The descriptor list, filled in at startup by each connector's own
/// registration and immutable thereafter.
/// </summary>
public sealed class ProviderCatalog : IProviderCatalog
{
    private readonly Dictionary<string, ProviderDescriptor> _byKey =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called by a connector's registration extension at startup.</summary>
    public void Add(ProviderDescriptor descriptor) => _byKey[descriptor.Key] = descriptor;

    public IReadOnlyList<ProviderDescriptor> Descriptors
        => _byKey.Values.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();

    public ProviderDescriptor? Find(string providerKey)
        => _byKey.TryGetValue(providerKey, out var descriptor) ? descriptor : null;
}
