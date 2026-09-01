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

    /// <summary>Broker identifier, e.g. "FYERS". One row per broker.</summary>
    public string BrokerName { get; set; } = string.Empty;

    /// <summary>The broker app's client id (e.g. FYERS "APPID-100").</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Data-Protection-encrypted app secret.</summary>
    public string SecretKeyEncrypted { get; set; } = string.Empty;

    /// <summary>OAuth redirect URI registered with the broker app.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    public string UpdatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
