using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers.Replay;

/// <summary>
/// The replay connector's identity card. It serves history out of this
/// platform's own <c>candles</c> table, so it needs no vendor account, no
/// credentials and no network — which is exactly what makes it the honest test
/// of whether the platform is really source-agnostic.
/// </summary>
public static class ReplayProvider
{
    /// <summary>
    /// Stable key, written into the SourceKey column of anything this connector
    /// produces. It must never change.
    /// </summary>
    public const string Key = "replay";

    public static readonly ProviderDescriptor Descriptor = new(
        Key,
        "Replay (stored candles)",
        ProviderKind.Data,
        ProviderAuthKind.None,
        new ProviderCapabilities
        {
            // History only, and only what has already been stored. Everything else
            // is false because replaying a database cannot invent a live tick, a
            // depth snapshot or an option chain.
            History = true,
            LiveTicks = false,
            Quotes = false,
            OptionChain = false,
            Orders = false,
            Depth = false,
            OpenInterest = false,
            Greeks = false,

            // It reads rows the platform itself wrote, so the symbols are already
            // canonical by definition.
            UsesCanonicalSymbols = true,

            // And those rows live in the same table a sync would write to, so the
            // sync path must read from this connector without persisting.
            ServesFromPlatformStore = true,

            // No vendor, so no rate limit and no per-call window.
            MaxStreamSymbols = null,
            HistoryMaxDaysPerCall = null,
            RequestsPerMinute = null,

            // Whatever is in the table; these are the codes the platform stores.
            Resolutions = new[] { "1", "2", "3", "5", "10", "15", "20", "30", "60", "120", "240", "D" },
            Segments = new[] { "CM", "FO", "CD", "MCX" },
        })
    {
        // Last resort: it can only ever return what the platform already stored,
        // so it must never quietly outrank a source that can fetch something new.
        FallbackRank = 100,
    };
}
