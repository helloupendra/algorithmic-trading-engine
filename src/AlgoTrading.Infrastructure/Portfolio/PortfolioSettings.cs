namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>
/// The Portfolio page's settings, from the "Portfolio" section of configuration (appsettings.Local.json, generated from
/// the server's .env). Nothing here is a user's credential: these are the platform's own apps at the brokers.
/// </summary>
public sealed class PortfolioSettings
{
    public const string Section = "Portfolio";

    /// <summary>
    /// The FYERS app users sign in to. Empty uses the platform's own FYERS app (the Connectors page), which FYERS lets
    /// only its creator sign in to while it is an individual app: other users need a third-party app (docs/modules/portfolio.md).
    /// </summary>
    public PortfolioFyersSettings Fyers { get; set; } = new();

    /// <summary>The Angel One SmartAPI app users sign in to. Empty uses the platform's Angel API key (Angel:ApiKey).</summary>
    public PortfolioAngelSettings Angel { get; set; } = new();

    /// <summary>How long snapshots are kept, in days.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>The shortest gap between two syncs by one user, in seconds.</summary>
    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>How far back the risk figures read daily closes, in calendar days.</summary>
    public int HistoryLookbackDays { get; set; } = 365;

    /// <summary>
    /// At most this many symbols have their daily closes fetched in one sync, largest holding first; the rest wait for
    /// the next. The fetches use the desk's own history connector, which the live desk needs too.
    /// </summary>
    public int HistoryFetchesPerSync { get; set; } = 30;
}

public sealed class PortfolioFyersSettings
{
    public string AppId { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>The redirect URL registered on the app: https://&lt;domain&gt;/api/Portfolio/callback/fyers.</summary>
    public string RedirectUri { get; set; } = string.Empty;
}

public sealed class PortfolioAngelSettings
{
    public string ApiKey { get; set; } = string.Empty;
}
