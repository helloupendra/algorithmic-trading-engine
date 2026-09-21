namespace AlgoTrading.Infrastructure.Providers.SimBroker;

/// <summary>
/// What an app on the simulated OpenFNO broker needs. Filled from the
/// "SimBroker" section of configuration, with SIMBROKER_* environment
/// variables as the fallback.
/// </summary>
/// <remarks>
/// The broker's login is client id + app id + app secret + a time-based code,
/// so a session can be renewed without a browser — but the app is bound to the
/// addresses it was whitelisted with, and a call from anywhere else is refused
/// with <c>STATIC_IP_MISMATCH</c> however good the credentials are.
/// <see cref="SimBrokerClient.WhoAmIAsync"/> reports the address the broker
/// actually sees, which is the one that check compares.
/// </remarks>
public sealed class SimBrokerSettings
{
    /// <summary>Named HTTP client, also the descriptor key: one name, so the two cannot drift.</summary>
    public const string HttpClientName = "simbroker";

    /// <summary>Where the broker is. The default is the public deployment of this project's own broker.</summary>
    public string BaseUrl { get; set; } = "https://broker.openfno.com";

    /// <summary>The trading account, for example <c>OFB00001</c>.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    /// <summary>The base32 secret the back office showed once when the account was opened.</summary>
    public string TotpSecret { get; set; } = string.Empty;

    /// <summary>
    /// The address the app is whitelisted against, shown in the console so a
    /// refusal can be explained without reading the broker's logs.
    /// </summary>
    public string StaticIp { get; set; } = string.Empty;

    /// <summary>
    /// The broker's back-office key, which opens accounts, moves money and
    /// issues apps for this platform's traders.
    /// </summary>
    /// <remarks>
    /// This is the whole broker, not one account, so it is never returned by an
    /// endpoint and never reaches the console — only whether it is set. It is
    /// needed because the platform's admin issues a trader's account for them;
    /// without it the connector still works for a single account configured by
    /// hand, and everything in <see cref="Missing"/> stays satisfied.
    /// </remarks>
    public string AdminKey { get; set; } = string.Empty;

    /// <summary>Whether the platform can act as the broker's back office.</summary>
    public bool CanAdminister => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(AdminKey);

    /// <summary>Which values are still missing, by the name an operator would set.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(BaseUrl)) missing.Add("SIMBROKER_BASE_URL");
        if (string.IsNullOrWhiteSpace(ClientId)) missing.Add("SIMBROKER_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(AppId)) missing.Add("SIMBROKER_APP_ID");
        if (string.IsNullOrWhiteSpace(AppSecret)) missing.Add("SIMBROKER_APP_SECRET");
        if (string.IsNullOrWhiteSpace(TotpSecret)) missing.Add("SIMBROKER_TOTP_SECRET");
        return missing;
    }

    public bool IsConfigured => Missing().Count == 0;
}
