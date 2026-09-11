// src/AlgoTrading.Api/Services/FeedSupervisor.cs

using AlgoTrading.Infrastructure.Providers.Fyers;
using AlgoTrading.Infrastructure.Providers.TrueData;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns one vendor's live feed process: <c>market_data/live/run_feed.py --vendor &lt;key&gt;</c>.
/// </summary>
/// <remarks>
/// One class for every vendor because the engine has one entry point for every
/// vendor. The TrueData feed used to have a supervisor, three endpoints and a
/// panel of its own, each a copy of the FYERS one with the names changed; the
/// next vendor would have been a third copy of each. Now a connector that
/// declares live ticks gets a feed from <see cref="FeedSupervisorRegistry"/>
/// and nothing here changes.
/// <para>
/// All of the process handling is inherited. What this adds is the one place
/// that says how a feed is launched and recognised, which the FYERS
/// <see cref="IngestorSupervisor"/> uses as well, so the two can never drift.
/// </para>
/// </remarks>
public sealed class FeedSupervisor : PythonDaemonSupervisor
{
    // The one-script-per-vendor streamers that run_feed.py replaced. A feed
    // started from one of them before the deploy is still running under that
    // name, and without these it would read as stopped: Start would open a
    // second connection (TrueData refuses a second session outright) and the
    // market close could not stop it. A vendor added after run_feed.py never
    // needs an entry.
    private static readonly Dictionary<string, string[]> RetiredScriptMarkers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [FyersProvider.Key] = new[] { "fyers_streamer" },
            [TrueDataProvider.Key] = new[] { "truedata_streamer" },
        };

    public FeedSupervisor(
        DaemonDescriptor daemon,
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger<FeedSupervisor> logger)
        : base(daemon, engine, scopeFactory, logger)
    {
    }

    /// <summary>
    /// The command-line fragment that identifies <paramref name="providerKey"/>'s
    /// feed. The script name alone cannot: every vendor runs the same one.
    /// </summary>
    public static string MarkerFor(string providerKey) => $"--vendor {providerKey}";

    /// <summary>How <paramref name="providerKey"/>'s feed is launched, recognised and recorded.</summary>
    /// <param name="providerKey">The connector key, passed to the script exactly as declared.</param>
    /// <param name="name">What logs and messages call it: "ingestor", "TrueData feed".</param>
    /// <param name="pidSettingKey">Where its pid is kept across an API restart.</param>
    public static DaemonDescriptor Describe(string providerKey, string name, string pidSettingKey) => new(
        Name: name,
        ScriptParts: new[] { "market_data", "live", "run_feed.py" },
        ProcessMarker: MarkerFor(providerKey),
        PidSettingKey: pidSettingKey,
        Args: new[] { "--vendor", providerKey },
        LegacyMarkers: RetiredScriptMarkers.TryGetValue(providerKey, out var retired) ? retired.ToArray() : null);
}
