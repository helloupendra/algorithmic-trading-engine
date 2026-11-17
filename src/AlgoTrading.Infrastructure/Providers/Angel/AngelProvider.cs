using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>
/// The Angel One connector's identity card: what it can do today, and nothing
/// it cannot.
/// </summary>
/// <remarks>
/// Added 2026-09-16 as a data connector. Its REST side is implemented here
/// (session, quotes, candles). Its websocket is the live feed since 18 Nov 2026
/// (market_data/live/vendors/angel.py, started from /api/Feeds/angel): it signs in
/// by itself with the PIN and a TOTP code, where FYERS needs a person every
/// morning, and carries 1,000 symbols where FYERS carries 100. Angel serves
/// option greeks for a live expiry but not a full chain with OI per strike, so
/// <c>Greeks</c> is true and <c>OptionChain</c> is not.
///
/// <para>The published limits that matter: 1-minute history comes 30 days at a
/// time (5-minute 100 days, hourly 400, daily 2000), a quote call takes at most
/// 50 instruments, and the websocket carries 20 levels of depth — five more
/// than any other connector here, which is why it is worth having.</para>
/// </remarks>
public static class AngelProvider
{
    /// <summary>Stable key, written into SourceKey on every row this connector produces.</summary>
    public const string Key = "angel";

    public static readonly ProviderDescriptor Descriptor = new(
        Key,
        "Angel One",
        ProviderKind.Data,
        // Client code + PIN + a TOTP secret: long-lived credentials with no
        // daily browser ritual, which is what ApiKey means here.
        ProviderAuthKind.ApiKey,
        new ProviderCapabilities
        {
            History = true,
            Quotes = true,

            // SmartAPI WebSocket 2.0, snap-quote mode (market_data/live/vendors/angel.py).
            LiveTicks = true,

            // 20 levels on its depth feed, once that feed exists.
            Depth = false,

            // Historical open interest has its own endpoint (getOIData).
            OpenInterest = true,

            // /marketData/v1/optionGreek: delta, gamma, theta, vega and IV for
            // every strike of one live expiry.
            Greeks = true,

            OptionChain = false,
            Orders = false,

            UsesCanonicalSymbols = false,

            // 1,000 tokens a websocket session, 3 sessions.
            MaxStreamSymbols = 1000,

            // The 1-minute window; longer intervals allow more (5m 100 days,
            // hourly 400, daily 2000).
            HistoryMaxDaysPerCall = 30,
            RequestsPerMinute = 180,
            Resolutions = new[] { "1", "3", "5", "10", "15", "30", "60", "D" },
            Segments = new[] { "CM", "FO", "CD", "MCX" },
        })
    {
        // Ranks behind the two connectors that have fed live sessions.
        FallbackRank = 30,
        ClientIdLabel = "Client code",
        SecretLabel = "TOTP secret",
    };
}
