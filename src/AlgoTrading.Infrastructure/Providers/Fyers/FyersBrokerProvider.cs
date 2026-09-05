using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
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
    private readonly IBrokerCredentialsProvider _credentials;

    public FyersBrokerProvider(IBrokerCredentialsProvider credentials)
    {
        _credentials = credentials;
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
    public async Task<string> GetAuthUrlAsync(string? state = null, CancellationToken cancellationToken = default)
    {
        var creds = await _credentials.GetAsync(FyersProvider.Key, cancellationToken: cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var creds = await _credentials.GetAsync(FyersProvider.Key, cancellationToken: cancellationToken);

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
}
