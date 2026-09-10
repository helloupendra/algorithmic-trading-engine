using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using System.Net.Http.Json;
using System.Text.Json;
using FyersCSharpSDK;
using Newtonsoft.Json.Linq;

namespace AlgoTrading.Infrastructure.Providers.Fyers;

/// <summary>
/// The execution side of the FYERS connector: the hosted-login URL and the auth
/// code exchange. FYERS tokens expire daily, which is why this is the first stop
/// of every trading morning.
/// </summary>
public class FyersBrokerProvider : IBrokerProvider
{
    /// <summary>FYERS API v3 token refresh (grant_type=refresh_token).</summary>
    private const string RefreshUrl = "https://api-t1.fyers.in/api/v3/validate-refresh-token";

    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IHttpClientFactory _httpClientFactory;

    public FyersBrokerProvider(
        IBrokerCredentialsProvider credentials,
        IHttpClientFactory httpClientFactory)
    {
        _credentials = credentials;
        _httpClientFactory = httpClientFactory;
    }

    public ProviderDescriptor Descriptor => FyersProvider.Descriptor;

    /// <inheritdoc />
    /// <remarks>
    /// Built by hand (FYERS API v3 generate-authcode) instead of via the SDK:
    /// the SDK's GetGenerateCode opens a browser on the *server*, which is
    /// useless when the operator is on the web console. The frontend sends the
    /// user's own browser to this URL; FYERS then redirects to our configured
    /// callback with an auth_code.
    /// </remarks>
    public async Task<string> GetAuthUrlAsync(string? state = null, long? brokerAccountId = null, CancellationToken cancellationToken = default)
    {
        var creds = await _credentials.GetAsync(FyersProvider.Key, brokerAccountId, cancellationToken);
        if (creds.Source == "none")
        {
            throw new InvalidOperationException(
                "FYERS app credentials are not configured. Save them on the Broker page first.");
        }

        return "https://api-t1.fyers.in/api/v3/generate-authcode" +
               $"?client_id={Uri.EscapeDataString(creds.ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(creds.RedirectUri)}" +
               "&response_type=code" +
               $"&state={Uri.EscapeDataString(state ?? "webui")}";
    }

    public async Task<BrokerTokenResult> ExchangeAuthCodeAsync(
        string authCode,
        long? brokerAccountId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var creds = await _credentials.GetAsync(FyersProvider.Key, brokerAccountId, cancellationToken);

        FyersClass fyers = FyersClass.Instance;

        string appHashId = Utility.GenerateAppHashID(creds.ClientId, creds.SecretKey);

        JObject tokenResponse = await fyers.GenerateToken(
            creds.SecretKey,
            creds.RedirectUri,
            authCode,
            appHashId);

        // The vendor's JSON stops here: everything above this adapter sees tokens
        // or a reason, never a FYERS payload shape.
        string accessToken = tokenResponse["TOKEN"]?.ToString() ?? string.Empty;
        string refreshToken = tokenResponse["refresh_token"]?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return BrokerTokenResult.Failed(
                tokenResponse["message"]?.ToString() ?? "FYERS returned no access token.");
        }

        return new BrokerTokenResult(accessToken, refreshToken);
    }

    /// <inheritdoc />
    public async Task<BrokerTokenResult> RefreshAccessTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return BrokerTokenResult.Failed("No refresh token was stored; sign in to FYERS again.");
        }

        var creds = await _credentials.GetAsync(FyersProvider.Key, cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(creds.TradingPin))
        {
            // Said plainly, because this is a setup step and not a fault: the
            // vendor requires the PIN on this call and will not renew without it.
            return BrokerTokenResult.Failed(
                "FYERS needs the trading PIN to refresh a token. Save it on the Broker page to keep mornings unattended.");
        }

        var payload = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["appIdHash"] = Utility.GenerateAppHashID(creds.ClientId, creds.SecretKey),
            ["refresh_token"] = refreshToken,
            ["pin"] = creds.TradingPin,
        };

        var http = _httpClientFactory.CreateClient(FyersProvider.Key);

        HttpResponseMessage response;
        string body;
        try
        {
            response = await http.PostAsJsonAsync(RefreshUrl, payload, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // A refresh runs unattended before the open; a transport failure
            // must read as "could not renew", not take the caller down.
            return BrokerTokenResult.Failed($"Could not reach FYERS to refresh the token: {ex.Message}");
        }

        using JsonDocument doc = SafeParse(body);
        JsonElement root = doc.RootElement;

        string status = root.TryGetProperty("s", out var s) ? s.GetString() ?? string.Empty : string.Empty;
        string? token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;

        if (!response.IsSuccessStatusCode || !string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(token))
        {
            string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            return BrokerTokenResult.Failed(
                string.IsNullOrWhiteSpace(message)
                    ? $"FYERS declined the refresh (HTTP {(int)response.StatusCode}). Sign in again."
                    : $"FYERS declined the refresh: {message}");
        }

        // FYERS returns only a new access token; the refresh token it was
        // issued with stays valid and is carried forward unchanged.
        return new BrokerTokenResult(token, refreshToken);
    }

    private static JsonDocument SafeParse(string body)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            // An HTML error page or a truncated response must not throw here;
            // the caller reports "declined" and the morning script says so.
            return JsonDocument.Parse("{}");
        }
    }
}
