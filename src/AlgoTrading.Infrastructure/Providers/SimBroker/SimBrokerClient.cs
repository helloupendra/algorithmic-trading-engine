using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.SimBroker;

/// <summary>What a call came back with: a value, or a refusal with the broker's own code.</summary>
/// <remarks>
/// A refusal is not an exception here. The broker answers <c>400</c> for a
/// request that is not valid (nothing was created), <c>200</c> with status
/// <c>REJECTED</c> for an order its risk checks turned down — a real order in
/// the book, with a reason — and <c>403 STATIC_IP_MISMATCH</c> for a call from
/// an address the app has not whitelisted. All three are answers, and a caller
/// has to be able to tell them apart.
/// </remarks>
public sealed record SimBrokerResult<T>(T? Value, string? ErrorCode = null, string? ErrorMessage = null, HttpStatusCode? Status = null)
{
    public bool Succeeded => ErrorCode is null;

    public static SimBrokerResult<T> Ok(T value) => new(value);

    public static SimBrokerResult<T> Failed(string code, string message, HttpStatusCode? status = null)
        => new(default, code, message, status);

    /// <summary>Carries a refusal across a change of payload type, so a caller can return it unchanged.</summary>
    public SimBrokerResult<TOther> As<TOther>()
        => SimBrokerResult<TOther>.Failed(ErrorCode ?? "ERROR", ErrorMessage ?? "The call failed.", Status);
}

public sealed record SimBrokerSession(string AccessToken, string ClientId, string AppId, DateTimeOffset ExpiresAt);

/// <summary>An account's money, as the broker reports it.</summary>
/// <param name="Cash">The ledger plus today's unsettled profit, loss and charges.</param>
/// <param name="Available">What a new order may use: cash less margin held, less any unrealised loss.</param>
public sealed record SimBrokerFunds(
    decimal NetDeposits,
    decimal LedgerBalance,
    decimal RealisedToday,
    decimal ChargesToday,
    decimal Cash,
    decimal OrderMargin,
    decimal PositionMargin,
    decimal Unrealised,
    decimal Available);

public sealed record SimBrokerOrder(
    string OrderId,
    string Symbol,
    string Side,
    int Quantity,
    int FilledQuantity,
    int PendingQuantity,
    string Type,
    string Product,
    decimal? LimitPrice,
    decimal? TriggerPrice,
    decimal? AveragePrice,
    string Status,
    string? RejectionCode,
    string? Message,
    string? Tag,
    decimal BlockedMargin,
    DateTimeOffset PlacedAt)
{
    /// <summary>The order exists but the risk checks refused it; <see cref="RejectionCode"/> says why.</summary>
    public bool WasRejected => string.Equals(Status, "REJECTED", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nothing more will happen to this order.</summary>
    public bool IsFinal => Status is "FILLED" or "CANCELLED" or "REJECTED" or "EXPIRED";
}

/// <param name="NetToday">Realised plus unrealised, less the charges the position has paid today.</param>
public sealed record SimBrokerPosition(
    string Symbol,
    string Product,
    int Quantity,
    decimal AveragePrice,
    decimal? LastPrice,
    decimal Unrealised,
    decimal RealisedToday,
    decimal ChargesToday,
    decimal NetToday,
    decimal Margin);

/// <summary>
/// Talks to an OpenFNO Broker — the simulated Indian broker this project also
/// maintains (https://broker.openfno.com). It enforces the rules an engine will
/// meet at a real broker: a whitelisted static IP, a daily two-factor login, a
/// cap on order operations per second, no market orders, whole lots, tick
/// sizes, margin and market hours.
/// </summary>
/// <remarks>
/// <para>
/// Why a client rather than an <see cref="Application.Providers.IBrokerProvider"/>:
/// that interface describes a hosted OAuth login — an auth URL for a browser
/// and an auth code exchanged for tokens. This broker signs in the way an API
/// client does, with an app id, an app secret and a TOTP code in one call, and
/// the session ends at 06:00 IST the next day. Forcing it into the OAuth shape
/// would misdescribe both.
/// </para>
/// <para>
/// The session is held in memory for as long as the broker says it is good for,
/// and a call that comes back <c>401</c> signs in once more and is tried again —
/// which is what the daily cut-off looks like from here.
/// </para>
/// <para>
/// <b>Nothing in the platform routes live orders through this yet.</b> Today it
/// serves the connectivity check on the Connectors page; order routing needs to
/// be proved against a market session before any strategy is pointed at it.
/// </para>
/// </remarks>
public sealed class SimBrokerClient
{
    private readonly IHttpClientFactory _factory;
    private readonly IOptionsMonitor<SimBrokerSettings> _settings;
    private readonly ILogger<SimBrokerClient> _logger;
    private readonly TimeProvider _time;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private SimBrokerSession? _session;

    public SimBrokerClient(
        IHttpClientFactory factory,
        IOptionsMonitor<SimBrokerSettings> settings,
        ILogger<SimBrokerClient> logger,
        TimeProvider? time = null)
    {
        _factory = factory;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public SimBrokerSettings Settings => _settings.CurrentValue;

    /// <summary>Whether a session is held, for the status the console shows.</summary>
    public bool HasSession => _session is not null && _session.ExpiresAt > _time.GetUtcNow();

    public DateTimeOffset? SessionExpiresAt => _session?.ExpiresAt;

    public string? SessionClientId => _session?.ClientId;

    /// <summary>
    /// Signs in for the day: app id, app secret, client id and a code derived
    /// from the stored TOTP secret. An existing session is reused unless
    /// <paramref name="force"/> asks for a new one.
    /// </summary>
    /// <remarks>
    /// The broker refuses a code it has already seen, so two logins inside one
    /// 30-second step fail the second time. That is the vendor's rule, not a
    /// bug here: the gate below keeps concurrent callers to one login, and a
    /// caller that really wants a second one waits for the next step.
    /// </remarks>
    public async Task<SimBrokerResult<SimBrokerSession>> LoginAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && HasSession) return SimBrokerResult<SimBrokerSession>.Ok(_session!);

        var settings = Settings;
        if (!settings.IsConfigured)
            return SimBrokerResult<SimBrokerSession>.Failed(
                "NOT_CONFIGURED",
                "The simulated broker is not configured: set " + string.Join(", ", settings.Missing()) + ".");

        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            if (!force && HasSession) return SimBrokerResult<SimBrokerSession>.Ok(_session!);

            string code;
            try
            {
                code = Totp.Generate(settings.TotpSecret, _time.GetUtcNow());
            }
            catch (ArgumentException ex)
            {
                return SimBrokerResult<SimBrokerSession>.Failed("INVALID_TOTP_SECRET", ex.Message);
            }

            var body = new
            {
                appId = settings.AppId,
                appSecret = settings.AppSecret,
                clientId = settings.ClientId,
                totp = code,
            };

            using var response = await Client().PostAsJsonAsync("api/v1/session", body, Json, cancellationToken);
            var grant = await ReadAsync<SessionBody>(response, cancellationToken);
            if (!grant.Succeeded || grant.Value is null) return grant.As<SimBrokerSession>();

            _session = new SimBrokerSession(grant.Value.AccessToken, grant.Value.ClientId, grant.Value.AppId, grant.Value.ExpiresAt);
            _logger.LogInformation("Signed in to the simulated broker as {ClientId} until {ExpiresAt:u}.",
                _session.ClientId, _session.ExpiresAt);
            return SimBrokerResult<SimBrokerSession>.Ok(_session);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>Forgets the session, so the next call signs in again.</summary>
    public void ForgetSession() => _session = null;

    public Task<SimBrokerResult<SimBrokerFunds>> GetFundsAsync(CancellationToken cancellationToken = default)
        => SendAsync<FundsBody, SimBrokerFunds>(
            () => new HttpRequestMessage(HttpMethod.Get, "api/v1/funds"),
            f => new SimBrokerFunds(f.NetDeposits, f.LedgerBalance, f.RealisedToday, f.ChargesToday,
                f.Cash, f.OrderMargin, f.PositionMargin, f.Unrealised, f.Available),
            cancellationToken);

    public Task<SimBrokerResult<IReadOnlyList<SimBrokerPosition>>> GetPositionsAsync(CancellationToken cancellationToken = default)
        => SendAsync<List<PositionBody>, IReadOnlyList<SimBrokerPosition>>(
            () => new HttpRequestMessage(HttpMethod.Get, "api/v1/positions"),
            list => list.Select(Map).ToList(),
            cancellationToken);

    /// <summary>The order book for one IST trading day, or today when no date is given.</summary>
    public Task<SimBrokerResult<IReadOnlyList<SimBrokerOrder>>> GetOrdersAsync(
        DateOnly? tradingDate = null, CancellationToken cancellationToken = default)
        => SendAsync<List<OrderBody>, IReadOnlyList<SimBrokerOrder>>(
            () => new HttpRequestMessage(HttpMethod.Get, tradingDate is null
                ? "api/v1/orders"
                : "api/v1/orders?date=" + tradingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            list => list.Select(Map).ToList(),
            cancellationToken);

    /// <summary>
    /// Places one order. <paramref name="quantity"/> is in units — lots times
    /// the lot size — because that is what the exchange counts and what the
    /// broker checks against the lot multiple and the freeze quantity.
    /// </summary>
    /// <remarks>
    /// Only limit and stop-limit orders exist: the broker refuses a market
    /// order from an API app (<c>MARKET_ORDER_NOT_ALLOWED</c>), as a real one
    /// does under the retail-algo rules, so a caller must name its price.
    /// </remarks>
    public Task<SimBrokerResult<SimBrokerOrder>> PlaceOrderAsync(
        string symbol,
        string side,
        int quantity,
        decimal limitPrice,
        string product = "NRML",
        decimal? triggerPrice = null,
        string validity = "DAY",
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["side"] = side.ToUpperInvariant(),
            ["quantity"] = quantity,
            ["type"] = triggerPrice is null ? "LIMIT" : "STOP_LIMIT",
            ["product"] = product.ToUpperInvariant(),
            ["validity"] = validity.ToUpperInvariant(),
            ["limitPrice"] = limitPrice,
        };
        if (triggerPrice is not null) body["triggerPrice"] = triggerPrice;
        if (!string.IsNullOrWhiteSpace(tag)) body["tag"] = tag;

        return SendAsync<OrderBody, SimBrokerOrder>(
            () => new HttpRequestMessage(HttpMethod.Post, "api/v1/orders")
            {
                Content = JsonContent.Create(body, options: Json),
            },
            order =>
            {
                var placed = Map(order);
                if (placed.WasRejected)
                    _logger.LogWarning("The simulated broker rejected order {OrderId} on {Symbol}: {Code} {Message}",
                        placed.OrderId, placed.Symbol, placed.RejectionCode, placed.Message);
                return placed;
            },
            cancellationToken);
    }

    /// <summary>Changes the price, the trigger or what is left of the quantity.</summary>
    public Task<SimBrokerResult<SimBrokerOrder>> ModifyOrderAsync(
        string orderId,
        decimal? limitPrice = null,
        decimal? triggerPrice = null,
        int? quantity = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>();
        if (limitPrice is not null) body["limitPrice"] = limitPrice;
        if (triggerPrice is not null) body["triggerPrice"] = triggerPrice;
        if (quantity is not null) body["quantity"] = quantity;

        return SendAsync<OrderBody, SimBrokerOrder>(
            () => new HttpRequestMessage(HttpMethod.Patch, $"api/v1/orders/{orderId}")
            {
                Content = JsonContent.Create(body, options: Json),
            },
            Map,
            cancellationToken);
    }

    public Task<SimBrokerResult<SimBrokerOrder>> CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
        => SendAsync<OrderBody, SimBrokerOrder>(
            () => new HttpRequestMessage(HttpMethod.Delete, $"api/v1/orders/{orderId}"),
            Map,
            cancellationToken);

    /// <summary>
    /// The address the broker sees for us, which is the one its static-IP check
    /// compares. No token is needed, so this answers even when the credentials
    /// are wrong — and it is the first thing to ask when placing an order is
    /// refused with <c>STATIC_IP_MISMATCH</c>.
    /// </summary>
    public async Task<SimBrokerResult<string>> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await Client().GetAsync("api/v1/whoami", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SimBrokerResult<string>.Failed("UNREACHABLE", $"{Settings.BaseUrl} could not be reached: {ex.Message}");
        }

        using (response)
        {
            var who = await ReadAsync<WhoAmIBody>(response, cancellationToken);
            return who.Succeeded && who.Value is not null
                ? SimBrokerResult<string>.Ok(who.Value.Ip)
                : who.As<string>();
        }
    }

    /// <summary>
    /// Signs in if needed, sends the request with the token, and — if the
    /// broker says the session has gone — signs in once more and sends it
    /// again. The request is built by a factory because a message cannot be
    /// sent twice.
    /// </summary>
    private async Task<SimBrokerResult<TOut>> SendAsync<TBody, TOut>(
        Func<HttpRequestMessage> request,
        Func<TBody, TOut> map,
        CancellationToken cancellationToken)
    {
        var login = await LoginAsync(cancellationToken: cancellationToken);
        if (!login.Succeeded || login.Value is null) return login.As<TOut>();

        var first = await SendOnceAsync<TBody>(request(), login.Value.AccessToken, cancellationToken);
        if (first.Succeeded && first.Value is not null) return SimBrokerResult<TOut>.Ok(map(first.Value));

        // 401 is the daily cut-off, or a session another program ended by
        // logging in with the same app. One fresh login, one retry.
        if (first.Status != HttpStatusCode.Unauthorized) return first.As<TOut>();

        _session = null;
        var again = await LoginAsync(force: true, cancellationToken);
        if (!again.Succeeded || again.Value is null) return again.As<TOut>();

        var second = await SendOnceAsync<TBody>(request(), again.Value.AccessToken, cancellationToken);
        return second.Succeeded && second.Value is not null
            ? SimBrokerResult<TOut>.Ok(map(second.Value))
            : second.As<TOut>();
    }

    private async Task<SimBrokerResult<TBody>> SendOnceAsync<TBody>(
        HttpRequestMessage request, string accessToken, CancellationToken cancellationToken)
    {
        using (request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            HttpResponseMessage response;
            try
            {
                response = await Client().SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                return SimBrokerResult<TBody>.Failed("UNREACHABLE", $"{Settings.BaseUrl} could not be reached: {ex.Message}");
            }

            using (response)
            {
                return await ReadAsync<TBody>(response, cancellationToken);
            }
        }
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient(SimBrokerSettings.HttpClientName);
        client.BaseAddress ??= new Uri(Settings.BaseUrl.TrimEnd('/') + "/");
        return client;
    }

    /// <summary>
    /// The broker's one error shape — <c>{"error":{"code":…,"message":…}}</c> —
    /// turned into a result. A body that is neither the payload nor that shape
    /// is reported with its status rather than guessed at.
    /// </summary>
    private static async Task<SimBrokerResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(text, Json);
                if (value is not null) return SimBrokerResult<T>.Ok(value);
            }
            catch (JsonException)
            {
                // Falls through, with the body itself as the message.
            }

            return SimBrokerResult<T>.Failed("BAD_RESPONSE", Describe(response, text), response.StatusCode);
        }

        try
        {
            var error = JsonSerializer.Deserialize<ErrorEnvelope>(text, Json);
            if (error?.Error is not null)
                return SimBrokerResult<T>.Failed(error.Error.Code, error.Error.Message, response.StatusCode);
        }
        catch (JsonException)
        {
        }

        return SimBrokerResult<T>.Failed(
            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), Describe(response, text), response.StatusCode);
    }

    private static string Describe(HttpResponseMessage response, string body)
        => $"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}";

    private static SimBrokerOrder Map(OrderBody o) => new(
        o.OrderId, o.Symbol, o.Side, o.Quantity, o.FilledQuantity, o.PendingQuantity, o.Type, o.Product,
        o.LimitPrice, o.TriggerPrice, o.AveragePrice, o.Status, o.RejectionCode, o.Message, o.Tag,
        o.BlockedMargin, o.PlacedAt);

    private static SimBrokerPosition Map(PositionBody p) => new(
        p.Symbol, p.Product, p.Quantity, p.AveragePrice, p.LastPrice,
        p.Unrealised, p.RealisedToday, p.ChargesToday, p.NetToday, p.Margin);

    private sealed record ErrorEnvelope([property: JsonPropertyName("error")] ErrorDetail? Error);

    private sealed record ErrorDetail(string Code, string Message);

    private sealed record SessionBody(string AccessToken, string ClientId, string AppId, DateTimeOffset ExpiresAt);

    private sealed record FundsBody(
        decimal NetDeposits, decimal LedgerBalance, decimal RealisedToday, decimal ChargesToday,
        decimal Cash, decimal OrderMargin, decimal PositionMargin, decimal Unrealised, decimal Available);

    private sealed record OrderBody(
        string OrderId, string Symbol, string Side, int Quantity, int FilledQuantity, int PendingQuantity,
        string Type, string Product, decimal? LimitPrice, decimal? TriggerPrice, decimal? AveragePrice,
        string Status, string? RejectionCode, string? Message, string? Tag, decimal BlockedMargin,
        DateTimeOffset PlacedAt);

    private sealed record PositionBody(
        string Symbol, string Product, int Quantity, decimal AveragePrice, decimal? LastPrice,
        decimal Unrealised, decimal RealisedToday, decimal ChargesToday, decimal NetToday, decimal Margin);

    private sealed record WhoAmIBody(string Ip);
}
