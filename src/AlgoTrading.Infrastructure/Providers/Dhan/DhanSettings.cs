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

    /// <summary>
    /// The account's Dhan PIN. With <see cref="TotpSecret"/> it lets the platform
    /// take the day's token itself (<see cref="DhanLoginFlow.SignInWithTotpAsync"/>)
    /// instead of waiting for someone to press Connect.
    /// </summary>
    /// <remarks>
    /// Set on the server only. Two hosts holding it would each sign the same
    /// account in on their own schedule, and whichever came second would hand
    /// the other a token it had not asked for.
    /// </remarks>
    public string Pin { get; set; } = string.Empty;

    /// <summary>
    /// The base32 secret Dhan shows once when TOTP is set up for the account
    /// (web.dhan.co → DhanHQ Trading APIs → Setup TOTP).
    /// </summary>
    public string TotpSecret { get; set; } = string.Empty;

    /// <summary>When the automatic sign-in may run.</summary>
    public DhanAutoSignInSettings AutoSignIn { get; set; } = new();

    /// <summary>Everything the automatic sign-in needs besides the client id.</summary>
    public bool HasAutoSignInSecrets => !string.IsNullOrWhiteSpace(Pin) && !string.IsNullOrWhiteSpace(TotpSecret);

    /// <summary>Recording Dhan's option chain into the platform's chain history.</summary>
    public DhanChainPollerSettings ChainPoller { get; set; } = new();

    /// <summary>What the live feed streams beyond the watchlist.</summary>
    public DhanUniverseSettings Universe { get; set; } = new();
}

/// <summary>"Dhan:ChainPoller" in configuration.</summary>
public class DhanChainPollerSettings
{
    /// <summary>
    /// Off unless configured. Dhan's one-chain-every-three-seconds limit is per
    /// account, so a developer's API and the server polling at once would starve
    /// each other; only the host that records turns it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Seconds between the starts of two rounds.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Comma-separated index names ("NIFTY", "SENSEX") or MCX commodities
    /// ("CRUDEOIL"). Each is polled only while its own exchange is open.
    /// </summary>
    /// <remarks>
    /// A string, not a list: the configuration binder appends to a list that
    /// already holds defaults, so "Underlyings": ["NIFTY"] would have meant all
    /// eight plus NIFTY again.
    /// </remarks>
    public string Underlyings { get; set; } = "NIFTY,BANKNIFTY,FINNIFTY,MIDCPNIFTY,SENSEX,BANKEX,CRUDEOIL,NATURALGAS";

    public IReadOnlyList<string> UnderlyingList => DhanSettingsLists.Split(Underlyings);
}

/// <summary>"Dhan:AutoSignIn" in configuration.</summary>
public class DhanAutoSignInSettings
{
    /// <summary>On whenever the PIN and TOTP secret are set; this turns it off without removing them.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The morning window, IST: the day's token is taken inside it, before the
    /// 08:45 job starts the feeds.
    /// </summary>
    /// <remarks>
    /// The earliest the worker signs in on any day, even with no token at all;
    /// later when the last token ends after it (see <see cref="DhanAutoSignInPolicy"/>).
    /// </remarks>
    public TimeSpan MorningFromIst { get; set; } = new(8, 0, 0);

    /// <summary>
    /// The latest the no-token alert waits for a sign-in that could only start
    /// late: five minutes for a person to press Connect before the 08:45 job.
    /// </summary>
    public TimeSpan MorningUntilIst { get; set; } = new(8, 40, 0);

    /// <summary>
    /// With no valid token at this time on a trading day, one alert says the
    /// automatic sign-in failed and to press Connect (see <see cref="DhanAutoSignInPolicy.NoTokenAlertUtc"/>).
    /// </summary>
    public TimeSpan NoTokenAlertIst { get; set; } = new(8, 10, 0);
}

/// <summary>"Dhan:Universe" in configuration.</summary>
public class DhanUniverseSettings
{
    /// <summary>Strikes streamed on each side of the at-the-money strike.</summary>
    public int StrikesEachSide { get; set; } = 5;

    /// <summary>Futures streamed per underlying, nearest expiry first.</summary>
    public int FuturesPerUnderlying { get; set; } = 2;

    /// <summary>Comma-separated, like every list here (see <see cref="DhanChainPollerSettings.Underlyings"/>).</summary>
    public string IndexUnderlyings { get; set; } = "NIFTY,BANKNIFTY,FINNIFTY,MIDCPNIFTY,SENSEX,BANKEX";

    public string McxFutureUnderlyings { get; set; } = "CRUDEOIL,CRUDEOILM,NATURALGAS,GOLD,GOLDM,SILVER,SILVERM";

    public string McxOptionUnderlyings { get; set; } = "CRUDEOIL,NATURALGAS";
}

internal static class DhanSettingsLists
{
    public static IReadOnlyList<string> Split(string? value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
