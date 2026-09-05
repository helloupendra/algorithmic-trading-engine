namespace AlgoTrading.Application.Providers;

/// <summary>What a provider is for. A vendor that both sells data and takes orders is <see cref="Both"/>.</summary>
public enum ProviderKind
{
    Data,
    Execution,
    Both,
}

/// <summary>How the platform authenticates with a provider.</summary>
public enum ProviderAuthKind
{
    /// <summary>No credentials at all — file, replay and paper providers.</summary>
    None,

    /// <summary>A long-lived key/secret pair; no daily ritual.</summary>
    ApiKey,

    /// <summary>Hosted OAuth login whose token dies every trading day (FYERS, most Indian brokers).</summary>
    OAuthDaily,
}

/// <summary>
/// A capability a binding can be routed on. Kept deliberately small: these are
/// the things the platform actually asks a source for.
/// </summary>
public enum ProviderCapability
{
    History,
    LiveTicks,
    Quotes,
    OptionChain,
    Orders,
}

/// <summary>
/// What a provider can actually deliver — declared up front, so a strategy never
/// has to discover at runtime that its source has no open interest. The console
/// renders this as a matrix and the conformance tests assert every claim.
/// </summary>
public sealed record ProviderCapabilities
{
    public bool History { get; init; }
    public bool LiveTicks { get; init; }
    public bool Quotes { get; init; }
    public bool OptionChain { get; init; }
    public bool Orders { get; init; }

    /// <summary>Top-of-book bid/ask sizes on the live feed.</summary>
    public bool Depth { get; init; }

    /// <summary>Open interest on option quotes/ticks.</summary>
    public bool OpenInterest { get; init; }

    /// <summary>Delta/gamma/theta/vega on option quotes.</summary>
    public bool Greeks { get; init; }

    /// <summary>
    /// True when the vendor speaks the platform's canonical symbol as-is, so
    /// <see cref="AlgoTrading.Application.Interfaces.ISymbolMapper"/> can skip the
    /// lookup entirely. FYERS is the canonical grammar today, so it sets this.
    /// </summary>
    public bool UsesCanonicalSymbols { get; init; }

    /// <summary>Websocket subscription ceiling, null when the vendor does not publish one.</summary>
    public int? MaxStreamSymbols { get; init; }

    /// <summary>Largest range a single history call accepts, in days.</summary>
    public int? HistoryMaxDaysPerCall { get; init; }

    /// <summary>Published REST rate limit, used to pace backfills.</summary>
    public int? RequestsPerMinute { get; init; }

    /// <summary>Canonical resolution codes the provider serves ("1", "5", "15", "D").</summary>
    public IReadOnlyList<string> Resolutions { get; init; } = Array.Empty<string>();

    /// <summary>Segments the provider covers ("CM", "FO", "CD", "MCX").</summary>
    public IReadOnlyList<string> Segments { get; init; } = Array.Empty<string>();

    /// <summary>Whether this provider claims the capability a binding is asking for.</summary>
    public bool Supports(ProviderCapability capability) => capability switch
    {
        ProviderCapability.History => History,
        ProviderCapability.LiveTicks => LiveTicks,
        ProviderCapability.Quotes => Quotes,
        ProviderCapability.OptionChain => OptionChain,
        ProviderCapability.Orders => Orders,
        _ => false,
    };
}

/// <summary>
/// Identity card of a connector. <paramref name="Key"/> is the stable lowercase
/// id used in configuration, bindings and the SourceKey column on every price
/// row — it must never change once data has been written with it.
/// </summary>
public sealed record ProviderDescriptor(
    string Key,
    string DisplayName,
    ProviderKind Kind,
    ProviderAuthKind Auth,
    ProviderCapabilities Capabilities);
