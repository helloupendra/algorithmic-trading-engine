using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>The FYERS app a user signs in to, and where FYERS sends them back.</summary>
/// <param name="Source">"portfolio" for the app in the Portfolio section, "platform" for the Connectors page's app.</param>
public sealed record FyersPortfolioApp(string AppId, string SecretKey, string RedirectUri, string Source);

/// <summary>
/// A user's own FYERS account, read only: the hosted sign-in, the auth-code exchange, and holdings, positions and
/// funds. Plain HTTP against FYERS API v3, no SDK, so every call is visible here and testable without a network.
/// </summary>
/// <remarks>
/// <para>
/// The flow is FYERS's OAuth authorization code (https://fyers.in/api-docs/v3/api/authentication/third-party-app/):
/// the user's browser goes to <see cref="AuthorizePath"/> with the app id, the registered redirect URL and a state;
/// FYERS sends it back with <c>auth_code</c> and the same state; the code is exchanged at <see cref="TokenPath"/> with
/// the SHA-256 of <c>app_id:app_secret</c>. The token lasts the day (FYERS: until the next morning); refreshing it
/// needs the user's PIN, which this platform never asks for, so the user connects again instead.
/// </para>
/// <para>
/// Read only: the paths below are the only ones this class can call. It has no order, modify, cancel or exit call.
/// </para>
/// </remarks>
public sealed class FyersPortfolioClient(
    IHttpClientFactory httpClientFactory,
    IBrokerCredentialsProvider credentials,
    IOptionsMonitor<PortfolioSettings> settings) : IPortfolioBroker
{
    public const string HttpClientName = "portfolio-fyers";
    public const string ApiBase = "https://api-t1.fyers.in";
    public const string AuthorizePath = "/api/v3/generate-authcode";
    public const string TokenPath = "/api/v3/validate-authcode";
    public const string ProfilePath = "/api/v3/profile";
    public const string HoldingsPath = "/api/v3/holdings";
    public const string PositionsPath = "/api/v3/positions";
    public const string FundsPath = "/api/v3/funds";

    private const string Name = "FYERS";

    public string Key => PortfolioBrokers.Fyers;

    /// <summary>The app users sign in to, or null when none is set up on this server.</summary>
    public async Task<FyersPortfolioApp?> AppAsync(CancellationToken cancellationToken)
    {
        var own = settings.CurrentValue.Fyers;
        if (!string.IsNullOrWhiteSpace(own.AppId) && !string.IsNullOrWhiteSpace(own.SecretKey) && !string.IsNullOrWhiteSpace(own.RedirectUri))
        {
            return new FyersPortfolioApp(own.AppId.Trim(), own.SecretKey.Trim(), own.RedirectUri.Trim(), "portfolio");
        }

        var platform = await credentials.GetAsync(PortfolioBrokers.Fyers, cancellationToken: cancellationToken);
        return platform.Source == "none" || string.IsNullOrWhiteSpace(platform.ClientId)
               || string.IsNullOrWhiteSpace(platform.SecretKey) || string.IsNullOrWhiteSpace(platform.RedirectUri)
            ? null
            : new FyersPortfolioApp(platform.ClientId.Trim(), platform.SecretKey.Trim(), platform.RedirectUri.Trim(), "platform");
    }

    /// <summary>FYERS's hosted sign-in for <paramref name="app"/>, carrying <paramref name="state"/> back to the callback.</summary>
    public static string AuthorizeUrl(FyersPortfolioApp app, string state) =>
        ApiBase + AuthorizePath +
        $"?client_id={Uri.EscapeDataString(app.AppId)}" +
        $"&redirect_uri={Uri.EscapeDataString(app.RedirectUri)}" +
        "&response_type=code" +
        $"&state={Uri.EscapeDataString(state)}";

    /// <summary>The SHA-256 of <c>app_id:app_secret</c> in lower-case hex, as the token exchange asks.</summary>
    public static string AppIdHash(string appId, string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{appId}:{secret}"))).ToLowerInvariant();

    /// <summary>Exchanges the <c>auth_code</c> FYERS sent back for the user's access token.</summary>
    /// <exception cref="PortfolioBrokerException">No app is set up, or FYERS refused the code.</exception>
    public async Task<PortfolioToken> ExchangeAsync(string authCode, CancellationToken cancellationToken)
    {
        var app = await AppAsync(cancellationToken)
                  ?? throw new PortfolioBrokerException("FYERS linking is not set up on this server.");

        string body = JsonSerializer.Serialize(new { grant_type = "authorization_code", appIdHash = AppIdHash(app.AppId, app.SecretKey), code = authCode });
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + TokenPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var answer = await PortfolioHttp.SendAsync(httpClientFactory.CreateClient(HttpClientName), request, Name, cancellationToken);
        using var doc = Ok(answer, "the sign-in");
        string token = PortfolioJson.Str(doc.RootElement, "access_token");
        if (token.Length == 0) throw new PortfolioBrokerException("FYERS accepted the sign-in but returned no access token.");
        return new PortfolioToken(token, AppId: app.AppId);
    }

    public async Task<string> AccountIdAsync(PortfolioToken token, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(ProfilePath, token, "the profile", cancellationToken);
        return ProfileId(doc.RootElement);
    }

    public async Task<BrokerPortfolio> ReadAsync(PortfolioToken token, CancellationToken cancellationToken)
    {
        using var holdings = await GetAsync(HoldingsPath, token, "the holdings", cancellationToken);
        using var positions = await GetAsync(PositionsPath, token, "the positions", cancellationToken);
        using var funds = await GetAsync(FundsPath, token, "the funds", cancellationToken);
        return new BrokerPortfolio(Holdings(holdings.RootElement), Positions(positions.RootElement), Funds(funds.RootElement));
    }

    public DateTime? ExpiresUtc(PortfolioToken token, DateTime issuedUtc) =>
        JwtExpiry.ExpiresUtc(token.AccessToken) ?? BrokerSession.TokenExpiryUtc(PortfolioBrokers.Fyers, null, issuedUtc);

    // ---------------------------------------------------------------- parsing

    /// <summary>The account's FYERS id (<c>data.fy_id</c>). Nothing else in the profile is read.</summary>
    public static string ProfileId(JsonElement root)
    {
        string id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? PortfolioJson.Str(data, "fy_id") : string.Empty;
        return id.Length > 0 ? id : throw new PortfolioBrokerException("FYERS's profile named no account.");
    }

    /// <summary>
    /// Holdings from <c>holdings[]</c>: the quantity left after today's sales (<c>remainingQuantity</c>), the average
    /// cost (<c>costPrice</c>) and FYERS's last price (<c>ltp</c>).
    /// </summary>
    public static IReadOnlyList<BrokerHolding> Holdings(JsonElement root) =>
        PortfolioJson.Objects(root, "holdings")
            .Select(h =>
            {
                string symbol = PortfolioJson.Str(h, "symbol");
                decimal qty = PortfolioJson.Dec(h, "remainingQuantity") ?? PortfolioJson.Dec(h, "quantity") ?? 0;
                decimal? ltp = PortfolioJson.Dec(h, "ltp");
                return new BrokerHolding
                {
                    Symbol = symbol,
                    Exchange = ExchangeOf(symbol),
                    Isin = PortfolioJson.Str(h, "isin").ToUpperInvariant(),
                    Quantity = qty,
                    AvgPrice = PortfolioJson.Dec(h, "costPrice") ?? 0,
                    Ltp = ltp > 0 ? ltp : null,
                };
            })
            .Where(h => h.Symbol.Length > 0 && h.Quantity > 0)
            .ToList();

    /// <summary>Positions from <c>netPositions[]</c>. FYERS's segment 10 is the cash market; anything else is F&amp;O, currency or commodity.</summary>
    public static IReadOnlyList<BrokerPosition> Positions(JsonElement root) =>
        PortfolioJson.Objects(root, "netPositions")
            .Select(p =>
            {
                string symbol = PortfolioJson.Str(p, "symbol");
                decimal? segment = PortfolioJson.Dec(p, "segment");
                decimal? ltp = PortfolioJson.Dec(p, "ltp");
                return new BrokerPosition
                {
                    Symbol = symbol,
                    Exchange = ExchangeOf(symbol),
                    Product = PortfolioJson.Str(p, "productType"),
                    Quantity = PortfolioJson.Dec(p, "netQty") ?? 0,
                    AvgPrice = PortfolioJson.Dec(p, "netAvg") ?? PortfolioJson.Dec(p, "avgPrice") ?? 0,
                    Ltp = ltp > 0 ? ltp : null,
                    Pnl = PortfolioJson.Dec(p, "pl"),
                    IsDerivative = segment is not null ? segment != 10 : LooksLikeDerivative(symbol),
                };
            })
            .Where(p => p.Symbol.Length > 0)
            .ToList();

    /// <summary>
    /// Funds from <c>fund_limit[]</c>, found by title as FYERS advises, not by position: "Available Balance",
    /// "Utilized Amount" and "Total Balance", equity and commodity together.
    /// </summary>
    public static BrokerFunds? Funds(JsonElement root)
    {
        var rows = PortfolioJson.Objects(root, "fund_limit").ToList();
        if (rows.Count == 0) return null;

        decimal? Row(string title)
        {
            var row = rows.FirstOrDefault(r => string.Equals(PortfolioJson.Str(r, "title"), title, StringComparison.OrdinalIgnoreCase));
            if (row.ValueKind != JsonValueKind.Object) return null;
            decimal? equity = PortfolioJson.Dec(row, "equityAmount");
            decimal? commodity = PortfolioJson.Dec(row, "commodityAmount");
            return equity is null && commodity is null ? null : (equity ?? 0) + (commodity ?? 0);
        }

        return new BrokerFunds(Row("Available Balance"), Row("Utilized Amount"), Row("Total Balance"));
    }

    private static string ExchangeOf(string symbol)
    {
        int colon = symbol.IndexOf(':');
        return colon > 0 ? symbol[..colon].ToUpperInvariant() : string.Empty;
    }

    private static bool LooksLikeDerivative(string symbol) =>
        symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("CE", StringComparison.OrdinalIgnoreCase)
        || symbol.EndsWith("PE", StringComparison.OrdinalIgnoreCase);

    // --------------------------------------------------------------- plumbing

    private async Task<JsonDocument> GetAsync(string path, PortfolioToken token, string what, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        // FYERS's header is "app_id:access_token", with no scheme in front.
        request.Headers.TryAddWithoutValidation("Authorization", $"{token.AppId}:{token.AccessToken}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var answer = await PortfolioHttp.SendAsync(httpClientFactory.CreateClient(HttpClientName), request, Name, cancellationToken);
        return Ok(answer, what);
    }

    /// <summary>
    /// The answer as JSON when FYERS said <c>"s":"ok"</c>; otherwise its refusal in words. Codes -8, -15, -16 and -17
    /// (and HTTP 401) are FYERS's "token expired or invalid".
    /// </summary>
    private static JsonDocument Ok(BrokerAnswer answer, string what)
    {
        var doc = PortfolioHttp.Parse(answer.Body);
        if (doc is null)
        {
            bool rejected = answer.Status == 401;
            throw new PortfolioBrokerException(
                rejected ? "FYERS no longer accepts this sign-in. Connect FYERS again." : $"FYERS answered {what} with something that is not JSON (HTTP {answer.Status}).",
                rejected);
        }

        var root = doc.RootElement;
        string status = PortfolioJson.Str(root, "s");
        if (answer.Status is >= 200 and < 300 && string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)) return doc;

        decimal code = PortfolioJson.Dec(root, "code") ?? 0;
        string message = PortfolioJson.Clip(PortfolioJson.Str(root, "message"));
        doc.Dispose();
        bool tokenRejected = answer.Status == 401 || code is -8 or -15 or -16 or -17;
        throw new PortfolioBrokerException(
            tokenRejected
                ? "FYERS no longer accepts this sign-in (its tokens last a day). Connect FYERS again."
                : $"FYERS refused {what}{(message.Length > 0 ? $": {message}" : $" (HTTP {answer.Status})")}.",
            tokenRejected);
    }
}
