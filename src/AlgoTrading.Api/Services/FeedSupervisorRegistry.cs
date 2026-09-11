// src/AlgoTrading.Api/Services/FeedSupervisorRegistry.cs

using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers;
using AlgoTrading.Infrastructure.Providers.Fyers;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Every live feed the API can run: one supervisor per shipped connector that
/// declares <c>LiveTicks</c>.
/// </summary>
/// <remarks>
/// The list is read from the connectors rather than written out here, so that
/// adding a vendor adds its feed to <c>/api/Feeds</c>, the Live feeds page and
/// the market-close sweep without anyone having to remember all three.
/// <para>
/// Only the shipped connectors count. A vendor added from the console is a row
/// in the database with no feed code behind it, and a Start button for it
/// could only ever fail.
/// </para>
/// <para>
/// FYERS is the existing <see cref="IngestorSupervisor"/> instance, not a new
/// supervisor for the same process: two owners of one process disagree about
/// whether it is running as soon as either of them starts or stops it.
/// </para>
/// </remarks>
public sealed class FeedSupervisorRegistry
{
    private readonly IReadOnlyList<(string Key, string DisplayName, PythonDaemonSupervisor Supervisor)> _all;
    private readonly Dictionary<string, PythonDaemonSupervisor> _byKey;

    public FeedSupervisorRegistry(
        ProviderCatalogSeed catalog,
        IngestorSupervisor ingestor,
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory)
    {
        var feeds = new List<(string Key, string DisplayName, PythonDaemonSupervisor Supervisor)>();

        // Live vendors first, in the order the router would choose them; the
        // key breaks ties so the page and the close sweep never reorder.
        var live = catalog.Descriptors
            .Where(d => d.Capabilities.LiveTicks)
            .OrderBy(d => d.FallbackRank)
            .ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in live)
        {
            PythonDaemonSupervisor supervisor =
                string.Equals(descriptor.Key, FyersProvider.Key, StringComparison.OrdinalIgnoreCase)
                    ? ingestor
                    : new FeedSupervisor(
                        FeedSupervisor.Describe(
                            descriptor.Key,
                            name: $"{descriptor.DisplayName} feed",
                            SystemSettingKeys.PidForFeed(descriptor.Key)),
                        engine,
                        scopeFactory,
                        loggerFactory.CreateLogger<FeedSupervisor>());

            feeds.Add((descriptor.Key, descriptor.DisplayName, supervisor));
        }

        _all = feeds;
        _byKey = feeds.ToDictionary(f => f.Key, f => f.Supervisor, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every feed, live vendors first.</summary>
    public IReadOnlyList<(string Key, string DisplayName, PythonDaemonSupervisor Supervisor)> All => _all;

    /// <summary>The feed for a connector key (case-insensitive), or null when that connector has none.</summary>
    public PythonDaemonSupervisor? Get(string key)
        => _byKey.TryGetValue(key, out var supervisor) ? supervisor : null;
}
