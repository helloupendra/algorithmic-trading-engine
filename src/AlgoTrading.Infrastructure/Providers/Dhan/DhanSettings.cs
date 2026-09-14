namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// Where the Dhan services live. Bound from the "Dhan" configuration section;
/// the client id and access token are read through the credentials provider,
/// not from here.
/// </summary>
public class DhanSettings
{
    /// <summary>Data and trading REST APIs (v2).</summary>
    public string ApiBaseUrl { get; set; } = "https://api.dhan.co/v2";

    /// <summary>Token generation (PIN + TOTP, API key consent).</summary>
    public string AuthBaseUrl { get; set; } = "https://auth.dhan.co";

    /// <summary>
    /// The detailed instrument master: one row per tradable instrument with its
    /// security id, lot size, expiry, strike and freeze quantity. About 35 MB.
    /// </summary>
    public string InstrumentMasterUrl { get; set; } = "https://images.dhan.co/api-data/api-scrip-master-detailed.csv";

    /// <summary>Live market feed (binary websocket), used by the Python engine.</summary>
    public string FeedUrl { get; set; } = "wss://api-feed.dhan.co";

    /// <summary>
    /// Registered with the Dhan API key. It must match what was entered in Dhan's
    /// console character for character.
    /// </summary>
    public string RedirectUri { get; set; } = "https://openfno.com/api/dhan/callback";

    /// <summary>
    /// The API key ("app id") generated in Dhan's console, valid 12 months. Sent
    /// as the app_id header on the consent calls. Its secret is the connector's
    /// secret credential.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// A token pasted from Dhan's console, used only while no signed-in session
    /// exists. Lasts 24 hours.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;
}
