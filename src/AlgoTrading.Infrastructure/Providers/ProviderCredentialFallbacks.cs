using AlgoTrading.Application.Interfaces;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>
/// Credentials read from server configuration, keyed by provider. Each adapter
/// registers its own section here at startup, which keeps the generic
/// credentials provider free of vendor names while preserving the behaviour that
/// an installation can still be configured entirely from appsettings/.env.
/// </summary>
public sealed class ProviderCredentialFallbacks
{
    private readonly Dictionary<string, BrokerCredentials> _byProvider =
        new(StringComparer.OrdinalIgnoreCase);

    public void Add(string providerKey, string clientId, string secretKey, string redirectUri)
    {
        bool configured =
            !string.IsNullOrWhiteSpace(clientId) &&
            !string.IsNullOrWhiteSpace(secretKey);

        _byProvider[providerKey] = new BrokerCredentials(
            clientId ?? string.Empty,
            secretKey ?? string.Empty,
            redirectUri ?? string.Empty,
            configured ? "config" : "none",
            null,
            null);
    }

    /// <summary>Configured credentials for a provider, or a "none" record when it has none.</summary>
    public BrokerCredentials Find(string providerKey)
        => _byProvider.TryGetValue(providerKey, out var credentials)
            ? credentials
            : new BrokerCredentials(string.Empty, string.Empty, string.Empty, "none", null, null);
}
