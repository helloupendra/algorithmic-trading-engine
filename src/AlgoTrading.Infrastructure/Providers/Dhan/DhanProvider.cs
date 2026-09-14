using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// The Dhan connector's identity card: its key, and an honest statement of what
/// it delivers today.
/// </summary>
/// <remarks>
/// Dhan is a broker as well as a data vendor, but only its data side is built,
/// so it registers as <see cref="ProviderKind.Data"/>. It becomes
/// <see cref="ProviderKind.Both"/> the day its order path exists, not before: a
/// connector that claims orders it cannot place is worse than one that does not.
///
/// <para>Verified against the live API on 2026-09-14, with an active data plan:
/// open interest in quotes and in history (intraday and daily, derivatives and
/// MCX), an option chain carrying OI, previous OI, volume, IV and greeks in one
/// call, and index volume in history. The live stream is a separate adapter in
/// the Python engine, run from Live feeds like every other vendor's.</para>
/// </remarks>
public static class DhanProvider
{
    /// <summary>
    /// Stable key. It is written into the SourceKey column of every row this
    /// connector produces, so it must never change.
    /// </summary>
    public const string Key = "dhan";

    public static readonly ProviderDescriptor Descriptor = new(
        Key,
        "Dhan",
        ProviderKind.Data,
        // A daily sign-in in the browser, the way FYERS works: Connect generates a
        // consent with the API key, the operator signs in on Dhan's site, and the
        // callback turns that into a 24-hour token. The PIN + TOTP sign-in that
        // would renew it unattended is the next step for this connector.
        ProviderAuthKind.OAuthDaily,
        new ProviderCapabilities
        {
            History = true,

            // The binary websocket adapter in the Python engine
            // (market_data/live/vendors/dhan.py), verified streaming MCX on
            // 2026-09-14: price, bid/ask with five levels, volume and OI.
            LiveTicks = true,

            // /marketfeed/ltp, /ohlc and /quote: up to 1,000 instruments a call.
            Quotes = true,

            // /optionchain: every strike of an expiry with OI, previous OI,
            // volume, IV, greeks and best bid/ask.
            OptionChain = true,

            // A broker, but the order path is not built. See the remarks.
            Orders = false,

            // Five levels in every quote; 20 and 200 levels on their own feeds.
            Depth = true,

            // In quotes, in intraday and daily history (the "oi" flag), and in
            // the option chain.
            OpenInterest = true,

            // delta, theta, gamma and vega per strike in the option chain.
            Greeks = true,

            // Dhan identifies an instrument by an exchange segment and a numeric
            // security id. Every symbol is translated at this adapter's boundary:
            // indices from a fixed table, everything else from
            // instrument_vendor_symbols, written by the instrument import.
            UsesCanonicalSymbols = false,

            // Intraday history: 90 days per request, five years back.
            HistoryMaxDaysPerCall = 90,

            // Data APIs: 5 requests a second.
            RequestsPerMinute = 300,

            Resolutions = new[] { "1", "5", "15", "25", "60", "D" },
            Segments = new[] { "CM", "FO", "MCX" },
        })
    {
        // Behind FYERS and TrueData until it has been run in shadow and compared.
        FallbackRank = 20,
        ClientIdLabel = "Client ID",
        SecretLabel = "API secret",
        CallbackPath = "/api/dhan/callback",
    };
}
