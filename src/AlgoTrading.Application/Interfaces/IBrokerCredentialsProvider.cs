namespace AlgoTrading.Application.Interfaces;

/// <summary>The effective broker app credentials and where they came from.</summary>
public record BrokerCredentials(
    string ClientId,
    string SecretKey,
    string RedirectUri,
    /// <summary>"database" when saved from the console, "config" when from appsettings, "none" when absent.</summary>
    string Source,
    string? UpdatedBy,
    DateTime? UpdatedUtc);

/// <summary>
/// Resolves the broker app credentials the platform should use: the row the
/// admin saved from the console wins; server configuration is the fallback so
/// existing .env-based installs keep working unchanged.
/// </summary>
public interface IBrokerCredentialsProvider
{
    Task<BrokerCredentials> GetFyersAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves (or replaces) the credentials for a broker.</summary>
    Task SaveFyersAsync(
        string clientId,
        string secretKey,
        string redirectUri,
        string updatedBy,
        CancellationToken cancellationToken = default);
}
