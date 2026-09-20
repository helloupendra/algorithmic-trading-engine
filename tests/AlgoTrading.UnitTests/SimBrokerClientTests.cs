using System.Net;
using System.Text;
using System.Text.Json;
using AlgoTrading.Infrastructure.Providers.SimBroker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The connector to the simulated broker, against a stand-in for the broker.
/// </summary>
/// <remarks>
/// What is worth testing here is not the happy path — it is the three answers
/// that are easy to read as success: a 200 carrying an order the risk checks
/// refused, a 400 carrying the broker's own refusal code, and a 401 in the
/// middle of the day, which is the daily cut-off and has to sign in again.
/// </remarks>
public class SimBrokerClientTests
{
    private const string Secret = "GEZDGNBVGY3TQOJQ";

    [Fact]
    public async Task It_signs_in_with_a_code_from_the_secret_and_keeps_the_session()
    {
        var broker = new FakeBroker();
        var client = Build(broker, out var time);

        var first = await client.LoginAsync();
        Assert.True(first.Succeeded);
        Assert.Equal("OFB00001", first.Value!.ClientId);

        // The code sent is the one the secret produces for that moment: the
        // broker would refuse anything else.
        var login = JsonDocument.Parse(broker.Requests[0].Body!).RootElement;
        Assert.Equal(AlgoTrading.Infrastructure.Providers.Totp.Generate(Secret, time.GetUtcNow()),
            login.GetProperty("totp").GetString());
        Assert.Equal("APP-TEST", login.GetProperty("appId").GetString());

        // A second call reuses the session rather than signing in again — the
        // broker refuses a code it has already seen.
        var second = await client.LoginAsync();
        Assert.True(second.Succeeded);
        Assert.Single(broker.Requests, r => r.Path == "/api/v1/session");
        Assert.True(client.HasSession);
    }

    [Fact]
    public async Task An_order_the_risk_checks_refused_comes_back_as_an_answer_not_an_error()
    {
        var broker = new FakeBroker();
        broker.OnPost("/api/v1/orders", _ => (HttpStatusCode.OK, Order(status: "REJECTED", rejectionCode: "INSUFFICIENT_FUNDS")));
        var client = Build(broker, out _);

        var result = await client.PlaceOrderAsync("NSE:NIFTY26SEPFUT", "BUY", 65, 25000m);

        // 200 means the broker answered; the order exists and it was refused.
        Assert.True(result.Succeeded);
        Assert.True(result.Value!.WasRejected);
        Assert.Equal("INSUFFICIENT_FUNDS", result.Value.RejectionCode);
        Assert.Equal(65, result.Value.Quantity);
    }

    [Fact]
    public async Task A_refused_request_carries_the_brokers_own_code()
    {
        var broker = new FakeBroker();
        broker.OnPost("/api/v1/orders", _ => (HttpStatusCode.BadRequest,
            """{"error":{"code":"LOT_SIZE_MULTIPLE","message":"Quantity 70 is not a multiple of the lot size 65."}}"""));
        var client = Build(broker, out _);

        var result = await client.PlaceOrderAsync("NSE:NIFTY26SEPFUT", "BUY", 70, 25000m);

        Assert.False(result.Succeeded);
        Assert.Equal("LOT_SIZE_MULTIPLE", result.ErrorCode);
        Assert.Contains("lot size 65", result.ErrorMessage);
        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
    }

    [Fact]
    public async Task A_stop_order_is_sent_as_a_stop_limit_and_a_plain_one_is_not()
    {
        var broker = new FakeBroker();
        var client = Build(broker, out _);

        await client.PlaceOrderAsync("NSE:NIFTY26SEPFUT", "SELL", 65, 24900m, triggerPrice: 24950m, tag: "smc-1");
        await client.PlaceOrderAsync("NSE:NIFTY26SEPFUT", "BUY", 65, 25000m);

        var stop = JsonDocument.Parse(broker.Requests.First(r => r.Path == "/api/v1/orders").Body!).RootElement;
        Assert.Equal("STOP_LIMIT", stop.GetProperty("type").GetString());
        Assert.Equal(24950m, stop.GetProperty("triggerPrice").GetDecimal());
        Assert.Equal("smc-1", stop.GetProperty("tag").GetString());

        var plain = JsonDocument.Parse(broker.Requests.Last(r => r.Path == "/api/v1/orders").Body!).RootElement;
        Assert.Equal("LIMIT", plain.GetProperty("type").GetString());
        Assert.False(plain.TryGetProperty("triggerPrice", out _));
        Assert.False(plain.TryGetProperty("tag", out _));
    }

    [Fact]
    public async Task The_daily_cut_off_signs_in_again_and_tries_once_more()
    {
        var broker = new FakeBroker();
        int funds = 0;
        broker.OnGet("/api/v1/funds", _ => ++funds == 1
            ? (HttpStatusCode.Unauthorized, """{"error":{"code":"SESSION_EXPIRED","message":"The session ended at 06:00 IST."}}""")
            : (HttpStatusCode.OK, FundsJson));
        var client = Build(broker, out var time);

        await client.LoginAsync();
        time.Advance(TimeSpan.FromSeconds(30));   // a fresh TOTP step, or the second login is refused
        var result = await client.GetFundsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(498_000m, result.Value!.Available);
        Assert.Equal(2, broker.Requests.Count(r => r.Path == "/api/v1/session"));
        Assert.Equal(2, funds);
    }

    [Fact]
    public async Task A_refusal_that_is_not_the_cut_off_is_not_retried()
    {
        var broker = new FakeBroker();
        int calls = 0;
        broker.OnGet("/api/v1/funds", _ =>
        {
            calls++;
            return (HttpStatusCode.Forbidden, """{"error":{"code":"STATIC_IP_MISMATCH","message":"This app is not allowed from 13.200.1.1."}}""");
        });
        var client = Build(broker, out _);

        var result = await client.GetFundsAsync();

        Assert.Equal("STATIC_IP_MISMATCH", result.ErrorCode);
        Assert.Equal(1, calls);
        Assert.Single(broker.Requests, r => r.Path == "/api/v1/session");
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_connector_is_not_configured()
    {
        var broker = new FakeBroker();
        var client = Build(broker, out _, settings: new SimBrokerSettings { BaseUrl = "https://broker.openfno.test" });

        var result = await client.PlaceOrderAsync("NSE:NIFTY26SEPFUT", "BUY", 65, 25000m);

        Assert.Equal("NOT_CONFIGURED", result.ErrorCode);
        Assert.Contains("SIMBROKER_APP_ID", result.ErrorMessage);
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task The_address_the_broker_sees_needs_no_session()
    {
        var broker = new FakeBroker();
        var client = Build(broker, out _, settings: new SimBrokerSettings { BaseUrl = "https://broker.openfno.test" });

        var result = await client.WhoAmIAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("15.252.66.246", result.Value);
        Assert.DoesNotContain(broker.Requests, r => r.Path == "/api/v1/session");
    }

    [Fact]
    public async Task A_broker_that_cannot_be_reached_is_said_plainly()
    {
        var broker = new FakeBroker { Unreachable = true };
        var client = Build(broker, out _);

        var result = await client.WhoAmIAsync();

        Assert.Equal("UNREACHABLE", result.ErrorCode);
        Assert.Contains("broker.openfno.test", result.ErrorMessage);
    }

    [Fact]
    public async Task Positions_come_back_with_the_numbers_the_broker_reported()
    {
        var broker = new FakeBroker();
        var client = Build(broker, out _);

        var result = await client.GetPositionsAsync();

        var position = Assert.Single(result.Value!);
        Assert.Equal("NSE:NIFTY26SEPFUT", position.Symbol);
        Assert.Equal(65, position.Quantity);
        Assert.Equal(25000.5m, position.AveragePrice);
        Assert.Equal(-123.69m, position.NetToday);
    }

    private static SimBrokerClient Build(FakeBroker broker, out FakeTime time, SimBrokerSettings? settings = null)
    {
        settings ??= new SimBrokerSettings
        {
            BaseUrl = "https://broker.openfno.test",
            ClientId = "OFB00001",
            AppId = "APP-TEST",
            AppSecret = "secret",
            TotpSecret = Secret,
        };

        time = new FakeTime(new DateTimeOffset(2026, 9, 21, 4, 0, 0, TimeSpan.Zero));
        return new SimBrokerClient(
            new SingleClientFactory(new HttpClient(broker)),
            new StaticOptionsMonitor<SimBrokerSettings>(settings),
            NullLogger<SimBrokerClient>.Instance,
            time);
    }

    private const string PositionsJson = """
        [{"symbol":"NSE:NIFTY26SEPFUT","exchange":"NSE","segment":"FO","product":"NRML","quantity":65,
          "averagePrice":25000.5,"lastPrice":25000,"unrealised":-32.5,"realisedToday":0,
          "chargesToday":91.19,"netToday":-123.69,"buyQuantity":65,"buyAverage":25000.5,
          "sellQuantity":0,"sellAverage":null,"margin":195003.9,"updatedAt":"2026-09-21T09:30:00+05:30"}]
        """;

    private const string FundsJson = """
        {"clientId":"OFB00001","netDeposits":500000,"ledgerBalance":500000,"realisedToday":0,
         "chargesToday":0,"cash":500000,"orderMargin":0,"positionMargin":2000,"unrealised":0,
         "available":498000,"ledger":[]}
        """;

    private static string Order(string status = "TRANSIT", string? rejectionCode = null) => $$"""
        {"orderId":"26092000000001","symbol":"NSE:NIFTY26SEPFUT","exchange":"NSE","segment":"FO",
         "side":"BUY","quantity":65,"lotSize":65,"lots":1,"filledQuantity":0,"pendingQuantity":65,
         "type":"LIMIT","product":"NRML","validity":"DAY","limitPrice":25000,"triggerPrice":null,
         "averagePrice":null,"status":"{{status}}","rejectionCode":{{(rejectionCode is null ? "null" : $"\"{rejectionCode}\"")}},
         "message":null,"tag":null,"algoId":"99999","appId":"APP-TEST","blockedMargin":195000,
         "modifications":0,"tradingDate":"2026-09-21","placedAt":"2026-09-21T09:30:00+05:30",
         "updatedAt":"2026-09-21T09:30:00+05:30"}
        """;

    /// <summary>A stand-in for the broker: it answers the calls this client makes, and records them.</summary>
    private sealed class FakeBroker : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<string?, (HttpStatusCode, string)>> _posts = new();
        private readonly Dictionary<string, Func<string?, (HttpStatusCode, string)>> _gets = new();

        public List<(string Method, string Path, string? Body)> Requests { get; } = new();

        public bool Unreachable { get; init; }

        public void OnPost(string path, Func<string?, (HttpStatusCode, string)> answer) => _posts[path] = answer;

        public void OnGet(string path, Func<string?, (HttpStatusCode, string)> answer) => _gets[path] = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable) throw new HttpRequestException("Connection refused.");

            string path = request.RequestUri!.AbsolutePath;
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            // whoami is the one call that needs no token, and the test for that
            // would pass by accident if nothing checked the others carry one.
            if (path != "/api/v1/whoami" && path != "/api/v1/session")
                Assert.NotNull(request.Headers.Authorization);

            Requests.Add((request.Method.Method, path, body));

            var (status, json) = Answer(request.Method, path, body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }

        private (HttpStatusCode, string) Answer(HttpMethod method, string path, string? body)
        {
            var table = method == HttpMethod.Post ? _posts : _gets;
            if (table.TryGetValue(path, out var answer)) return answer(body);

            return (method.Method, path) switch
            {
                ("POST", "/api/v1/session") => (HttpStatusCode.OK,
                    """{"accessToken":"token-1","clientId":"OFB00001","appId":"APP-TEST","expiresAt":"2026-09-22T06:00:00+05:30"}"""),
                ("GET", "/api/v1/whoami") => (HttpStatusCode.OK, """{"ip":"15.252.66.246"}"""),
                ("GET", "/api/v1/funds") => (HttpStatusCode.OK, FundsJson),
                ("GET", "/api/v1/positions") => (HttpStatusCode.OK, PositionsJson),
                ("POST", "/api/v1/orders") => (HttpStatusCode.OK, Order()),
                _ => (HttpStatusCode.NotFound, """{"error":{"code":"NOT_FOUND","message":"No such call."}}"""),
            };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTime(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
