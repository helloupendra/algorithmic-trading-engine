namespace AlgoTrading.Application.Interfaces;

/// <summary>The effective broker app credentials and where they came from.</summary>
public record BrokerCredentials(
    string ClientId,
    string SecretKey,
    string RedirectUri,
    /// <summary>The broker's trading PIN, when saved; needed to refresh an expired token.</summary>
    string? TradingPin,
    /// <summary>"database" when saved from the console, "config" when from appsettings, "none" when absent.</summary>
    string Source,
    string? UpdatedBy,
    DateTime? UpdatedUtc);

/// <summary>
/// Resolves the app credentials the platform should use for a provider: the row
/// the admin saved from the console wins; server configuration is the fallback so
/// existing .env-based installs keep working unchanged.
/// </summary>
public interface IBrokerCredentialsProvider
{
    /// <summary>
    /// Credentials for one provider.
    /// </summary>
    /// <param name="providerKey">Provider key, e.g. "fyers".</param>
    /// <param name="brokerAccountId">
    /// The account these credentials belong to; null is the shared platform
    /// account, which is how the installation runs today.
    /// </param>
    Task<BrokerCredentials> GetAsync(
        string providerKey,
        long? brokerAccountId = null,
        CancellationToken cancellationToken = default);

    /// <summary>Saves (or replaces) the credentials for a provider and account.</summary>
    Task SaveAsync(
        string providerKey,
        string clientId,
        string secretKey,
        string redirectUri,
        string updatedBy,
        /// <summary>
        /// The broker's trading PIN, needed to refresh an expired token
        /// unattended. Null leaves whatever is stored untouched, so saving the
        /// app credentials again never silently clears it.
        /// </summary>
        string? tradingPin = null,
        long? brokerAccountId = null,
        CancellationToken cancellationToken = default);
}
