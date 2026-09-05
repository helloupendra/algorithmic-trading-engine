namespace AlgoTrading.Infrastructure.Providers.Fyers;

/// <summary>
/// FYERS-specific settings, bound from the "Fyers" section of appsettings.
/// </summary>
/// <remarks>
/// Vendor settings belong to the vendor's adapter, not to the platform: the
/// generic layer only ever deals in <see cref="AlgoTrading.Application.Interfaces.BrokerCredentials"/>.
/// The section name is unchanged so existing installs and the generated
/// appsettings.Local.json keep working untouched.
/// </remarks>
public class FyersSettings
{
    public string ClientId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string DataApiBaseUrl { get; set; } = "https://api-t1.fyers.in";
}
