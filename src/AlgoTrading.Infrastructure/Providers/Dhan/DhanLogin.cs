using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>What a completed Dhan sign-in produced.</summary>
public sealed record DhanSignIn(string ClientId, DateTime ExpiresUtc);

/// <summary>
/// Dhan's API-key sign-in, the daily browser flow:
/// generate a consent with the API key and secret → the operator signs in on Dhan's
/// site → Dhan redirects to the callback with a tokenId → the tokenId is consumed
/// for a 24-hour access token, which is saved as the connector's session.
/// </summary>
/// <remarks>
/// Verified against Dhan's documentation and the live consent endpoint on
/// 2026-09-14. A consent can be generated 25 times a day, so a Connect that is
/// pressed and abandoned costs one of them and nothing else.
/// </remarks>
public sealed class DhanLoginFlow : IProviderLoginFlow
{
    private readonly DhanSettings _settings;
    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IBrokerSessionStore _sessions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DhanLoginFlow> _logger;

    public DhanLoginFlow(
        IOptions<DhanSettings> settings,
        IBrokerCredentialsProvider credentials,
        IBrokerSessionStore sessions,
        IHttpClientFactory httpClientFactory,
        ILogger<DhanLoginFlow> logger)
    {
        _settings = settings.Value;
        _credentials = credentials;
        _sessions = sessions;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string ProviderKey => DhanProvider.Key;

    public async Task<string> GetLoginUrlAsync(CancellationToken cancellationToken = default)
    {
        var (clientId, apiKey, apiSecret) = await AppCredentialsAsync(cancellationToken);

        using var root = await PostAsync(
            $"/app/generate-consent?client_id={Uri.EscapeDataString(clientId)}", apiKey, apiSecret, cancellationToken);

        string consentId = root.RootElement.TryGetProperty("consentAppId", out var id) ? id.GetString() ?? string.Empty : string.Empty;
        if (string.IsNullOrWhiteSpace(consentId))
            throw new InvalidOperationException("Dhan generated no consent; check the API key and secret.");

        return $"{_settings.AuthBaseUrl.TrimEnd('/')}/login/consentApp-login?consentAppId={Uri.EscapeDataString(consentId)}";
    }

    /// <summary>
    /// Turns the tokenId from Dhan's redirect into a session. Refuses a sign-in
    /// to any Dhan account other than the configured client id: the callback is
    /// open to the browser, so the account it accepts must be pinned.
    /// </summary>
    public async Task<DhanSignIn> CompleteAsync(string tokenId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            throw new InvalidOperationException("Dhan's redirect carried no tokenId.");

        var (clientId, apiKey, apiSecret) = await AppCredentialsAsync(cancellationToken);

        using var root = await PostAsync(
            $"/app/consumeApp-consent?tokenId={Uri.EscapeDataString(tokenId)}", apiKey, apiSecret, cancellationToken);
        var r = root.RootElement;

        string token = r.TryGetProperty("accessToken", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        string signedInAs = r.TryGetProperty("dhanClientId", out var c) ? c.ToString() : string.Empty;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Dhan returned no access token for this sign-in.");

        if (!string.Equals(signedInAs, clientId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Signed in to Dhan as client {Mask(signedInAs)}, but this connector is set up for {Mask(clientId)}. Nothing was saved.");
        }

        await _sessions.SaveAsync(new BrokerSession
        {
            ProviderKey = DhanProvider.Key,
            BrokerName = "DHAN",
            AccessToken = token,
            RefreshToken = string.Empty,
        }, cancellationToken);

        var expires = BrokerSession.TokenExpiryUtc(DhanProvider.Key, null, DateTime.UtcNow)!.Value;
        _logger.LogInformation("Dhan signed in for client {Client}; token valid until {Expires:u}.", Mask(clientId), expires);
        return new DhanSignIn(clientId, expires);
    }

    private async Task<(string ClientId, string ApiKey, string ApiSecret)> AppCredentialsAsync(CancellationToken cancellationToken)
    {
        var creds = await _credentials.GetAsync(DhanProvider.Key, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(creds.ClientId) || string.IsNullOrWhiteSpace(creds.SecretKey) || string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException(
                "Dhan sign-in needs the client id, the API key and the API secret (Dhan section of appsettings.Local.json: ClientId, ApiKey, ApiSecret).");
        }

        return (creds.ClientId.Trim(), _settings.ApiKey.Trim(), creds.SecretKey.Trim());
    }

    private async Task<JsonDocument> PostAsync(string pathAndQuery, string apiKey, string apiSecret, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_settings.AuthBaseUrl.TrimEnd('/')}{pathAndQuery}");
        request.Headers.TryAddWithoutValidation("app_id", apiKey);
        request.Headers.TryAddWithoutValidation("app_secret", apiSecret);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        var http = _httpClientFactory.CreateClient(DhanProvider.Key);
        using var response = await http.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Not DhanApiClient's wording: on these two calls a 401 means the
            // consent or the sign-in was not accepted, not that a data token expired.
            var failure = DhanApiClient.Describe((int)response.StatusCode, body);
            string code = failure.Code is null ? string.Empty : $", {failure.Code}";
            throw new InvalidOperationException(
                $"Dhan did not accept the sign-in ({(int)response.StatusCode}{code}). Press Connect again; if it keeps failing, check the API key and secret.");
        }

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Dhan's sign-in service answered with something that is not JSON ({(int)response.StatusCode}).");
        }
    }

    /// <summary>"1113706926" → "111…926": enough to tell accounts apart in a message or log.</summary>
    internal static string Mask(string clientId) =>
        clientId.Length <= 6 ? "…" : $"{clientId[..3]}…{clientId[^3..]}";
}
