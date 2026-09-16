namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>
/// What the Angel One SmartAPI app needs. Filled from the "Angel" section of
/// configuration, with ANGEL_* environment variables as the fallback.
/// </summary>
/// <remarks>
/// Angel's session is client code + trading PIN + a time-based code from the
/// 2FA secret, so unlike FYERS and Dhan it can be renewed without a browser —
/// but the app is bound to the static IP it was registered with, and a call
/// from any other machine is refused however good the credentials are.
/// </remarks>
public sealed class AngelSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string ClientCode { get; set; } = string.Empty;

    /// <summary>The 4-digit trading PIN; SmartAPI calls this field "password".</summary>
    public string Pin { get; set; } = string.Empty;

    /// <summary>The base32 secret shown when 2FA was set up, used to derive the TOTP.</summary>
    public string TotpSecret { get; set; } = string.Empty;

    public string RootUrl { get; set; } = "https://apiconnect.angelone.in";

    /// <summary>The IP the app is registered against, shown in the console so a refusal can be explained.</summary>
    public string StaticIp { get; set; } = string.Empty;

    /// <summary>Which values are still missing, by the name an operator would set.</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ApiKey)) missing.Add("ANGEL_API_KEY");
        if (string.IsNullOrWhiteSpace(ClientCode)) missing.Add("ANGEL_CLIENT_CODE");
        if (string.IsNullOrWhiteSpace(Pin)) missing.Add("ANGEL_PIN");
        if (string.IsNullOrWhiteSpace(TotpSecret)) missing.Add("ANGEL_TOTP_SECRET");
        return missing;
    }

    public bool IsConfigured => Missing().Count == 0;
}
