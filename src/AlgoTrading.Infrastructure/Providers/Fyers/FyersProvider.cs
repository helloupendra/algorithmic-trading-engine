using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers.Fyers;

/// <summary>
/// The FYERS connector's identity card: its key, and an honest statement of what
/// the feed actually delivers today.
/// </summary>
public static class FyersProvider
{
    /// <summary>
    /// Stable key. It is written into the SourceKey column of every price row
    /// this connector produces, so it must never change.
    /// </summary>
    public const string Key = "fyers";

    public static readonly ProviderDescriptor Descriptor = new(
        Key,
        "FYERS",
        ProviderKind.Both,
        ProviderAuthKind.OAuthDaily,
        new ProviderCapabilities
        {
            History = true,
            LiveTicks = true,
            Quotes = true,
            OptionChain = true,

            // Orders exist at FYERS but the platform does not route them through
            // this interface yet — live orders still go out from the Python
            // engine. Claimed only when there is a caller.
            Orders = false,

            // Verified against the running feed on 2026-09-05: option ticks carry
            // bidSize/askSize, index ticks do not; open interest arrives null for
            // every contract in the current subscription mode, so it is not
            // claimed. A strategy that needs OI must see this as false rather
            // than discover nulls at runtime.
            Depth = true,
            OpenInterest = false,
            Greeks = true,

            // The canonical symbol grammar was taken from FYERS, so no mapping
            // rows are needed for this connector.
            UsesCanonicalSymbols = true,

            MaxStreamSymbols = 200,
            HistoryMaxDaysPerCall = 100,
            Resolutions = new[] { "1", "2", "3", "5", "10", "15", "20", "30", "60", "120", "240", "D" },
            Segments = new[] { "CM", "FO", "CD", "MCX" },
        })
    {
        // A live vendor: first choice whenever nothing is configured.
        FallbackRank = 0,
    };
}
