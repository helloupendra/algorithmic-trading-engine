namespace AlgoTrading.Domain.Entities;

/// <summary>
/// Per-installation broker app credentials, entered by the admin from the
/// console. This is what lets a fresh clone run without editing any config
/// file: each operator saves their own broker app's client id and secret.
/// The secret is stored encrypted (ASP.NET Data Protection), never plaintext.
/// </summary>
public class BrokerConfig
{
    public long Id { get; set; }

    /// <summary>Broker identifier, e.g. "FYERS". One row per broker per account.</summary>
    public string BrokerName { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="BrokerAccount"/> these credentials belong to. Null means the
    /// shared platform account. Each trader creating their own vendor app means
    /// their own client id and secret, so the column exists from the start.
    /// </summary>
    public long? BrokerAccountId { get; set; }

    /// <summary>The broker app's client id (e.g. FYERS "APPID-100").</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Data-Protection-encrypted app secret.</summary>
    public string SecretKeyEncrypted { get; set; } = string.Empty;

    /// <summary>OAuth redirect URI registered with the broker app.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>
    /// The broker's trading PIN, encrypted, or empty when not saved.
    /// </summary>
    /// <remarks>
    /// FYERS access tokens expire daily and its refresh call requires the PIN.
    /// Stored only so the platform can renew its own token without a person at
    /// the keyboard every morning; it is never used to place an order, because
    /// this platform places none — the only run modes are LivePaper and
    /// OfflineReplay. Encrypted at rest beside the app secret, and optional:
    /// leave it unset and the morning login stays manual.
    /// </remarks>
    public string TradingPinEncrypted { get; set; } = string.Empty;

    public string UpdatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
