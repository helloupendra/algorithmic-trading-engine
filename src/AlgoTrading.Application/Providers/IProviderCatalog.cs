namespace AlgoTrading.Application.Providers;

/// <summary>
/// What the platform knows <em>about</em> its connectors, without constructing
/// any of them.
/// </summary>
/// <remarks>
/// Descriptors are static metadata, so they are separated from
/// <see cref="IProviderRegistry"/>, which hands out live adapter instances.
/// Keeping them apart is not tidiness: an adapter needs to ask "does this
/// provider speak canonical symbols?" through the symbol mapper, and if that
/// question resolved adapter instances the graph would be circular.
/// </remarks>
public interface IProviderCatalog
{
    /// <summary>Every registered connector's identity card, ordered by key.</summary>
    IReadOnlyList<ProviderDescriptor> Descriptors { get; }

    /// <summary>The descriptor for a key, or null when nothing is registered under it.</summary>
    ProviderDescriptor? Find(string providerKey);
}
