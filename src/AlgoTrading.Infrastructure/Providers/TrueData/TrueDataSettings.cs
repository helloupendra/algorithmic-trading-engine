namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>
/// Where the TrueData services live. Separate hosts by design on their side:
/// auth, history, symbol masters and the real-time push are four endpoints.
/// </summary>
public class TrueDataSettings
{
    /// <summary>OAuth2 password-grant token endpoint.</summary>
    public string AuthBaseUrl { get; set; } = "https://auth.truedata.in";

    /// <summary>Bars, ticks and bhavcopy over REST, with the bearer token.</summary>
    public string HistoryBaseUrl { get; set; } = "https://history.truedata.in";

    /// <summary>
    /// Symbol masters and option chains. This host authenticates with the
    /// username and password in the query string rather than the bearer token —
    /// TrueData's choice, not ours.
    /// </summary>
    public string SymbolApiBaseUrl { get; set; } = "https://api.truedata.in";

    /// <summary>
    /// Option chain with greeks. A separate TrueData product on a separate host,
    /// and a separately charged one — a subscription without it answers here
    /// with a refusal rather than an empty chain.
    /// </summary>
    public string GreeksBaseUrl { get; set; } = "https://greeks.truedata.in";

    /// <summary>Real-time push host; the port decides sandbox or production.</summary>
    public string StreamHost { get; set; } = "push.truedata.in";

    /// <summary>
    /// 8086 is the sandbox, 8084 production. TrueData moves an account from one
    /// to the other once integration is signed off, so this is configuration and
    /// not a constant.
    /// </summary>
    public int RealTimePort { get; set; } = 8086;
}
