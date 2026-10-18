using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers;

namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>
/// A user's own Dhan account, read only, with an access token the user generated on web.dhan.co and pasted.
/// </summary>
/// <remarks>
/// <para>
/// Why a pasted token (https://dhanhq.co/docs/v2/authentication/): Dhan's API key and secret are "user specific", so
/// the platform's own key cannot sign anyone else in, and the flow for platforms serving other users is Dhan's
/// partner login, which needs Dhan to onboard the platform as a partner first. What every individual can do is
/// generate a 24-hour access token under My Profile → Access DhanHQ APIs, and that is what the user pastes with
/// their client id. Nothing else is asked for: not the password, not the PIN, not a TOTP secret.
/// </para>
/// <para>
/// Read only: the paths below are the only ones this class can call. It has no order, modify, cancel or exit call.
/// </para>
/// </remarks>
public sealed class DhanPortfolioClient(IHttpClientFactory httpClientFactory) : IPortfolioBroker
{
    public const string HttpClientName = "portfolio-dhan";
    public const string ApiBase = "https://api.dhan.co";
    public const string ProfilePath = "/v2/profile";
    public const string HoldingsPath = "/v2/holdings";
    public const string PositionsPath = "/v2/positions";
    public const string FundsPath = "/v2/fundlimit";

    private const string Name = "Dhan";

    /// <summary>How long a token generated on web.dhan.co lasts, per Dhan's documentation, when the token itself does not say.</summary>
    public static readonly TimeSpan WebTokenLifetime = TimeSpan.FromHours(24);

    public string Key => PortfolioBrokers.Dhan;

    public async Task<string> AccountIdAsync(PortfolioToken token, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync(ProfilePath, token, "the profile", empty: false, cancellationToken);
        return ProfileId(doc.RootElement);
    }

    public async Task<BrokerPortfolio> ReadAsync(PortfolioToken token, CancellationToken cancellationToken)
    {
        using var holdings = await GetAsync(HoldingsPath, token, "the holdings", empty: true, cancellationToken);
        using var positions = await GetAsync(PositionsPath, token, "the positions", empty: true, cancellationToken);
        using var funds = await GetAsync(FundsPath, token, "the fund limits", empty: false, cancellationToken);
        return new BrokerPortfolio(Holdings(holdings.RootElement), Positions(positions.RootElement), Funds(funds.RootElement));
    }

    public DateTime? ExpiresUtc(PortfolioToken token, DateTime issuedUtc) =>
        JwtExpiry.ExpiresUtc(token.AccessToken) ?? DateTime.SpecifyKind(issuedUtc, DateTimeKind.Utc) + WebTokenLifetime;

    // ---------------------------------------------------------------- parsing

    /// <summary>The account's Dhan client id (<c>dhanClientId</c>). Nothing else in the profile is kept.</summary>
    public static string ProfileId(JsonElement root)
    {
        string id = PortfolioJson.Str(root, "dhanClientId");
        return id.Length > 0 ? id : throw new PortfolioBrokerException("Dhan's profile named no client id.");
    }

    /// <summary>
    /// Holdings from the top-level array: <c>totalQty</c> (in the demat account and still to be delivered) at
    /// <c>avgCostPrice</c>. Dhan sends no price with a holding, so <see cref="BrokerHolding.Ltp"/> stays null.
    /// </summary>
    public static IReadOnlyList<BrokerHolding> Holdings(JsonElement root) =>
        PortfolioJson.Objects(root)
            .Select(h =>
            {
                string exchange = PortfolioJson.Str(h, "exchange").ToUpperInvariant();
                return new BrokerHolding
                {
                    Symbol = PortfolioJson.Str(h, "tradingSymbol"),
                    // "ALL" is a holding Dhan can sell on either exchange: it names neither.
                    Exchange = exchange is "NSE" or "BSE" ? exchange : string.Empty,
                    Isin = PortfolioJson.Str(h, "isin").ToUpperInvariant(),
                    Quantity = PortfolioJson.Dec(h, "totalQty") ?? 0,
                    AvgPrice = PortfolioJson.Dec(h, "avgCostPrice") ?? 0,
                    Ltp = null,
                };
            })
            .Where(h => h.Symbol.Length > 0 && h.Quantity > 0)
            .ToList();

    /// <summary>
    /// Positions from the top-level array: <c>netQty</c> at <c>costPrice</c>, P&amp;L as realised plus unrealised. Dhan
    /// sends no last price with a position. The segment names the derivative: NSE_FNO, BSE_FNO, MCX_COMM and the
    /// currency segments.
    /// </summary>
    public static IReadOnlyList<BrokerPosition> Positions(JsonElement root) =>
        PortfolioJson.Objects(root)
            .Select(p =>
            {
                string segment = PortfolioJson.Str(p, "exchangeSegment").ToUpperInvariant();
                decimal? realised = PortfolioJson.Dec(p, "realizedProfit");
                decimal? unrealised = PortfolioJson.Dec(p, "unrealizedProfit");
                return new BrokerPosition
                {
                    Symbol = PortfolioJson.Str(p, "tradingSymbol"),
                    Exchange = segment,
                    Product = PortfolioJson.Str(p, "productType"),
                    Quantity = PortfolioJson.Dec(p, "netQty") ?? 0,
                    AvgPrice = PortfolioJson.Dec(p, "costPrice") ?? PortfolioJson.Dec(p, "buyAvg") ?? 0,
                    Ltp = null,
                    Pnl = realised is null && unrealised is null ? null : (realised ?? 0) + (unrealised ?? 0),
                    IsDerivative = segment is not ("NSE_EQ" or "BSE_EQ" or ""),
                };
            })
            .Where(p => p.Symbol.Length > 0)
            .ToList();

    /// <summary>
    /// The fund limit: <c>availabelBalance</c> (Dhan's own spelling) available, <c>utilizedAmount</c> used, and the
    /// start-of-day balance <c>sodLimit</c> as the total.
    /// </summary>
    public static BrokerFunds? Funds(JsonElement root) =>
        root.ValueKind != JsonValueKind.Object
            ? null
            : new BrokerFunds(
                PortfolioJson.Dec(root, "availabelBalance") ?? PortfolioJson.Dec(root, "availableBalance"),
                PortfolioJson.Dec(root, "utilizedAmount"),
                PortfolioJson.Dec(root, "sodLimit"));

    // --------------------------------------------------------------- plumbing

    private async Task<JsonDocument> GetAsync(string path, PortfolioToken token, string what, bool empty, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiBase + path);
        request.Headers.TryAddWithoutValidation("access-token", token.AccessToken);
        if (!string.IsNullOrWhiteSpace(token.ClientId)) request.Headers.TryAddWithoutValidation("client-id", token.ClientId);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        var answer = await PortfolioHttp.SendAsync(httpClientFactory.CreateClient(HttpClientName), request, Name, cancellationToken);
        var doc = PortfolioHttp.Parse(answer.Body);

        if (answer.Status is >= 200 and < 300 && doc is not null)
        {
            // A 200 can still carry {"status":"failed"}, as Dhan's data APIs do.
            if (!(doc.RootElement.ValueKind == JsonValueKind.Object
                  && string.Equals(PortfolioJson.Str(doc.RootElement, "status"), "failed", StringComparison.OrdinalIgnoreCase)))
            {
                return doc;
            }
        }

        string code = doc is null ? string.Empty : PortfolioJson.Str(doc.RootElement, "errorCode");
        string message = doc is null ? string.Empty : PortfolioJson.Clip(PortfolioJson.Str(doc.RootElement, "errorMessage"));
        doc?.Dispose();

        // An account with nothing in it is answered as a data error (DH-907) saying so, not as an empty list.
        if (empty && code == "DH-907" && SaysNothingThere(message)) return JsonDocument.Parse("[]");

        bool rejected = answer.Status == 401 || code == "DH-901";
        throw new PortfolioBrokerException(
            rejected
                ? "Dhan no longer accepts this access token (a token made on web.dhan.co lasts 24 hours). Generate a new one and paste it again."
                : code == "DH-904"
                    ? "Dhan is limiting requests from this account. Try again in a minute."
                    : $"Dhan refused {what}{(code.Length > 0 || message.Length > 0 ? $" ({string.Join(": ", new[] { code, message }.Where(s => s.Length > 0))})" : $" (HTTP {answer.Status})")}.",
            rejected);
    }

    private static bool SaysNothingThere(string message) =>
        message.Contains("no holding", StringComparison.OrdinalIgnoreCase)
        || message.Contains("no position", StringComparison.OrdinalIgnoreCase)
        || message.Contains("no data", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
