using AlgoTrading.Application.Providers;

namespace AlgoTrading.Infrastructure.Providers.SimBroker;

/// <summary>
/// The simulated broker's identity card: what it can do today, and nothing it
/// cannot.
/// </summary>
/// <remarks>
/// Added 2026-09-20. It takes orders, so its kind is <see cref="ProviderKind.Execution"/>
/// and <c>Orders</c> is true — but it is listed here so an operator can see and
/// test the connection, not so a binding can quietly route a live strategy to
/// it. Nothing sends orders this way until the path has been proved against a
/// market session.
///
/// <para>It sells no data of its own: its prices come from this platform's own
/// feed, so History, Quotes and LiveTicks stay false and a binding can never
/// take price data from it.</para>
///
/// <para>The published limits that matter: ten order operations a second, no
/// market orders from an API app, and a session that ends at 06:00 IST the next
/// day.</para>
/// </remarks>
public static class SimBrokerProvider
{
    /// <summary>Stable key; also the name of the HTTP client this connector uses.</summary>
    public const string Key = SimBrokerSettings.HttpClientName;

    public static readonly ProviderDescriptor Descriptor = new(
        Key,
        "OpenFNO Broker (simulated)",
        ProviderKind.Execution,

        // Client id + app id + app secret + a TOTP secret: long-lived
        // credentials, with a daily sign-in that needs no browser.
        ProviderAuthKind.ApiKey,
        new ProviderCapabilities
        {
            Orders = true,

            // It prices from this platform's feed; it is not a data source.
            History = false,
            Quotes = false,
            LiveTicks = false,
            OptionChain = false,

            // FYERS grammar, symbol for symbol.
            UsesCanonicalSymbols = true,

            // Ten order operations a second; the REST allowance is wider, and
            // this is the one that bites.
            RequestsPerMinute = 600,

            Segments = new[] { "CM", "FO", "CD", "MCX" },
        })
    {
        // Last, behind every real vendor: it must never win a routing tie-break.
        FallbackRank = 900,
        ClientIdLabel = "Client ID",
        SecretLabel = "App secret",
    };
}
