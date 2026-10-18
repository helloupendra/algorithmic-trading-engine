using System.Net.Http.Headers;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers;
using AlgoTrading.Infrastructure.Providers.Angel;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>
/// Spaces the portfolio's SmartAPI calls across every user: Angel One limits calls per app, not per account, and
/// answers a burst with a plain-text 403. One per process (a singleton).
/// </summary>
public sealed class AngelPortfolioPacer(TimeSpan gap)
{
    /// <summary>The gap the desk's own SmartAPI client keeps (<see cref="AngelApiClient.MinCallGap"/>).</summary>
    public static readonly TimeSpan DefaultGap = AngelApiClient.MinCallGap;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastUtc = DateTime.MinValue;

    public AngelPortfolioPacer() : this(DefaultGap)
    {
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        if (gap <= TimeSpan.Zero) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var wait = _lastUtc + gap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            _lastUtc = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// A user's own Angel One account, read only, through SmartAPI's publisher login: the user signs in on Angel's own
/// page and Angel redirects back with the session's tokens.
/// </summary>
/// <remarks>
/// <para>
/// The flow: the browser goes to <see cref="LoginBase"/> with the app's API key and a state; after the user signs in
/// (mobile, OTP, PIN, all on Angel's page) Angel redirects to the redirect URL registered on the app under My Apps,
/// with <c>auth_token</c> (the session's JWT) and <c>feed_token</c>. Only the auth token is kept: the feed token is for
/// the live socket, which the Portfolio page does not open, and a refresh token, when Angel sends one, is not stored
/// because nothing here renews a session; the user connects again the next day.
/// </para>
/// <para>
/// Read only: the paths below are the only ones this class can call. SmartAPI files positions under
/// <c>order/v1</c>, but <c>getPosition</c> is a read; this class has no order, modify, cancel or convert call.
/// </para>
/// </remarks>
public sealed class AngelPortfolioClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<PortfolioSettings> settings,
    IOptions<AngelSettings> angel,
    AngelPortfolioPacer pacer) : IPortfolioBroker
{
    public const string HttpClientName = "portfolio-angel";
    public const string ApiBase = "https://apiconnect.angelone.in";
    public const string LoginBase = "https://smartapi.angelone.in/publisher-login";
    public const string ProfilePath = "/rest/secure/angelbroking/user/v1/getProfile";
    public const string HoldingsPath = "/rest/secure/angelbroking/portfolio/v1/getAllHolding";
    public const string PositionsPath = "/rest/secure/angelbroking/order/v1/getPosition";
    public const string FundsPath = "/rest/secure/angelbroking/user/v1/getRMS";

    private const string Name = "Angel One";

    /// <summary>SmartAPI's codes for a session it no longer accepts: invalid, expired or missing token, an ended session.</summary>
    private static readonly HashSet<string> RejectedCodes = new(StringComparer.OrdinalIgnoreCase) { "AG8001", "AG8002", "AG8003", "AB1010", "AB8050", "AB8051" };

    public string Key => PortfolioBrokers.Angel;

    /// <summary>The SmartAPI app users sign in to; null when none is set up on this server.</summary>
    public string? ApiKey
    {
        get
        {
            string own = settings.CurrentValue.Angel.ApiKey.Trim();
            if (own.Length > 0) return own;
            string platform = angel.Value.ApiKey.Trim();
            return platform.Length > 0 ? platform : null;
        }
    }

    /// <summary>Angel's hosted sign-in for the app, carrying <paramref name="state"/>.</summary>
    public static string LoginUrl(string apiKey, string state) =>
        $"{LoginBase}?api_key={Uri.EscapeDataString(apiKey)}&state={Uri.EscapeDataString(state)}";

    /// <summary>The token from Angel's redirect, as it is stored: the session's JWT and the app it belongs to.</summary>
    public PortfolioToken FromRedirect(string authToken)
    {
        string jwt = authToken.Trim();
        if (jwt.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) jwt = jwt[7..].Trim();
        return new PortfolioToken(jwt, AppId: ApiKey);
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

    /// <summary>From the session JWT's own end; Angel publishes no fixed lifetime for a publisher session.</summary>
    public DateTime? ExpiresUtc(PortfolioToken token, DateTime issuedUtc) => JwtExpiry.ExpiresUtc(token.AccessToken);

    // ---------------------------------------------------------------- parsing

    /// <summary>The account's Angel client code (<c>data.clientcode</c>). Nothing else in the profile is read.</summary>
    public static string ProfileId(JsonElement root)
    {
        string id = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? PortfolioJson.Str(data, "clientcode") : string.Empty;
        return id.Length > 0 ? id : throw new PortfolioBrokerException("Angel One's profile named no client code.");
    }

    /// <summary>
    /// Holdings from <c>data.holdings[]</c> (getAllHolding; getHolding's bare array is read too): <c>quantity</c> at
    /// <c>averageprice</c>, with Angel's <c>ltp</c>.
    /// </summary>
    public static IReadOnlyList<BrokerHolding> Holdings(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)) return [];
        var rows = data.ValueKind == JsonValueKind.Array ? PortfolioJson.Objects(data) : PortfolioJson.Objects(data, "holdings");
        return rows
            .Select(h =>
            {
                decimal? ltp = PortfolioJson.Dec(h, "ltp");
                return new BrokerHolding
                {
                    Symbol = PortfolioJson.Str(h, "tradingsymbol"),
                    Exchange = PortfolioJson.Str(h, "exchange").ToUpperInvariant(),
                    Isin = PortfolioJson.Str(h, "isin").ToUpperInvariant(),
                    Quantity = PortfolioJson.Dec(h, "quantity") ?? 0,
                    AvgPrice = PortfolioJson.Dec(h, "averageprice") ?? 0,
                    Ltp = ltp > 0 ? ltp : null,
                };
            })
            .Where(h => h.Symbol.Length > 0 && h.Quantity > 0)
            .ToList();
    }

    /// <summary>
    /// Positions from <c>data[]</c> (null when there are none): <c>netqty</c> at <c>avgnetprice</c>, Angel's
    /// <c>ltp</c> and <c>pnl</c>. A future or option by its instrument type, or by its exchange (NFO, BFO, MCX, CDS).
    /// </summary>
    public static IReadOnlyList<BrokerPosition> Positions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)) return [];
        return PortfolioJson.Objects(data)
            .Select(p =>
            {
                string exchange = PortfolioJson.Str(p, "exchange").ToUpperInvariant();
                string type = PortfolioJson.Str(p, "instrumenttype").ToUpperInvariant();
                decimal? ltp = PortfolioJson.Dec(p, "ltp");
                decimal? pnl = PortfolioJson.Dec(p, "pnl");
                if (pnl is null)
                {
                    decimal? realised = PortfolioJson.Dec(p, "realised");
                    decimal? unrealised = PortfolioJson.Dec(p, "unrealised");
                    pnl = realised is null && unrealised is null ? null : (realised ?? 0) + (unrealised ?? 0);
                }

                return new BrokerPosition
                {
                    Symbol = PortfolioJson.Str(p, "tradingsymbol"),
                    Exchange = exchange,
                    Product = PortfolioJson.Str(p, "producttype"),
                    Quantity = PortfolioJson.Dec(p, "netqty") ?? 0,
                    AvgPrice = PortfolioJson.Dec(p, "avgnetprice") ?? PortfolioJson.Dec(p, "netprice") ?? 0,
                    Ltp = ltp > 0 ? ltp : null,
                    Pnl = pnl,
                    IsDerivative = type.StartsWith("FUT", StringComparison.Ordinal) || type.StartsWith("OPT", StringComparison.Ordinal)
                                   || exchange is "NFO" or "BFO" or "MCX" or "CDS" or "BCD" or "NCDEX",
                };
            })
            .Where(p => p.Symbol.Length > 0)
            .ToList();
    }

    /// <summary>The RMS limits: <c>availablecash</c> available, <c>utiliseddebits</c> used, and Angel's <c>net</c> as the total.</summary>
    public static BrokerFunds? Funds(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? new BrokerFunds(PortfolioJson.Dec(data, "availablecash"), PortfolioJson.Dec(data, "utiliseddebits"), PortfolioJson.Dec(data, "net"))
            : null;

    // --------------------------------------------------------------- plumbing

    private async Task<JsonDocument> GetAsync(string path, PortfolioToken token, string what, CancellationToken cancellationToken)
    {
        string apiKey = token.AppId ?? ApiKey ?? throw new PortfolioBrokerException("Angel One linking is not set up on this server.");
        await pacer.WaitAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        var headers = request.Headers;
        headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers.TryAddWithoutValidation("X-UserType", "USER");
        headers.TryAddWithoutValidation("X-SourceID", "WEB");
        headers.TryAddWithoutValidation("X-PrivateKey", apiKey);
        string local = AngelApiClient.LocalIp();
        headers.TryAddWithoutValidation("X-ClientLocalIP", local);
        headers.TryAddWithoutValidation("X-ClientPublicIP", string.IsNullOrWhiteSpace(angel.Value.StaticIp) ? local : angel.Value.StaticIp);
        headers.TryAddWithoutValidation("X-MACAddress", AngelApiClient.MacAddress());

        var answer = await PortfolioHttp.SendAsync(httpClientFactory.CreateClient(HttpClientName), request, Name, cancellationToken);
        if (AngelApiClient.IsRateLimit(answer.Status, answer.Body))
            throw new PortfolioBrokerException("Angel One is limiting requests right now. Try again in a minute.");

        var doc = PortfolioHttp.Parse(answer.Body);
        if (doc is null)
        {
            bool unauthorised = answer.Status == 401;
            throw new PortfolioBrokerException(
                unauthorised ? "Angel One no longer accepts this sign-in. Connect Angel One again." : $"Angel One answered {what} with something that is not JSON (HTTP {answer.Status}).",
                unauthorised);
        }

        // SmartAPI answers a refusal with HTTP 200 and "status": false.
        var root = doc.RootElement;
        bool ok = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True;
        if (ok && answer.Status is >= 200 and < 300) return doc;

        string code = PortfolioJson.Str(root, "errorcode");
        string message = PortfolioJson.Clip(PortfolioJson.Str(root, "message"));
        doc.Dispose();
        bool rejected = answer.Status == 401 || RejectedCodes.Contains(code);
        throw new PortfolioBrokerException(
            rejected
                ? "Angel One no longer accepts this sign-in. Connect Angel One again."
                : $"Angel One refused {what}{(message.Length > 0 ? $": {message}" : $" (HTTP {answer.Status})")}{(code.Length > 0 ? $" [{code}]" : string.Empty)}.",
            rejected);
    }
}
