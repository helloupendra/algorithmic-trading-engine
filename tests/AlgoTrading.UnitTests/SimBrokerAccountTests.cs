using System.Net;
using System.Text;
using System.Text.Json;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.SimBroker;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Giving a trader their own account at the simulated broker.
/// </summary>
/// <remarks>
/// The things worth pinning down: the app is whitelisted to the address the
/// broker itself reports (the check it will make is against that, not against
/// whatever this server thinks its address is), the secrets the broker shows
/// once are stored encrypted and can be handed back, and a half-finished
/// issue is reported rather than rolled back by pretending.
/// </remarks>
public class SimBrokerAccountTests
{
    [Fact]
    public async Task Issuing_opens_an_account_whitelists_this_server_and_pays_the_opening_money_in()
    {
        var broker = new FakeBroker();
        await using var db = NewDb();
        var service = Build(db, broker);

        var issued = await service.IssueAsync(7, "Asha", 500_000m, "admin");

        Assert.True(issued.Succeeded);
        Assert.Equal("OFB00007", issued.Value!.ClientId);
        Assert.Equal("APP-NEW", issued.Value.AppId);

        // The address came from the broker's own whoami, not from configuration.
        var app = JsonDocument.Parse(broker.Body("POST", "/admin/accounts/OFB00007/apps")!).RootElement;
        var ips = app.GetProperty("staticIps").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(new[] { "15.252.66.246", "13.1.1.1" }, ips);

        var funds = JsonDocument.Parse(broker.Body("POST", "/admin/accounts/OFB00007/funds")!).RootElement;
        Assert.Equal(500_000m, funds.GetProperty("amount").GetDecimal());

        // Every admin call carried the key; none of them carried a bearer token.
        Assert.All(broker.Requests, r => Assert.Equal("back-office-key", r.AdminKey));
    }

    [Fact]
    public async Task The_secrets_the_broker_shows_once_are_stored_encrypted_and_can_be_handed_back()
    {
        var broker = new FakeBroker();
        await using var db = NewDb();
        var service = Build(db, broker);

        await service.IssueAsync(7, "Asha", 0m, "admin");

        var row = await db.SimBrokerAccounts.SingleAsync();
        Assert.DoesNotContain("s3cret", row.AppSecretProtected);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", row.TotpSecretProtected);

        var revealed = await service.RevealAsync(7);
        Assert.Equal("s3cret", revealed!.AppSecret);
        Assert.Equal("JBSWY3DPEHPK3PXP", revealed.TotpSecret);
        Assert.Contains("otpauth://totp/", revealed.TotpUri);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", revealed.TotpUri);
    }

    [Fact]
    public async Task A_second_account_for_the_same_trader_is_refused_before_anything_is_opened()
    {
        var broker = new FakeBroker();
        await using var db = NewDb();
        var service = Build(db, broker);

        await service.IssueAsync(7, "Asha", 0m, "admin");
        int calls = broker.Requests.Count;

        var again = await service.IssueAsync(7, "Asha", 0m, "admin");

        Assert.Equal("ALREADY_LINKED", again.ErrorCode);
        Assert.Equal(calls, broker.Requests.Count);
        Assert.Equal(1, await db.SimBrokerAccounts.CountAsync());
    }

    [Fact]
    public async Task An_account_opened_whose_app_fails_is_reported_and_not_saved_as_usable()
    {
        var broker = new FakeBroker();
        broker.On("POST", "/admin/accounts/OFB00007/apps", _ => (HttpStatusCode.BadRequest,
            """{"error":{"code":"INVALID_REQUEST","message":"A static IP is required."}}"""));
        await using var db = NewDb();
        var service = Build(db, broker);

        var issued = await service.IssueAsync(7, "Asha", 500_000m, "admin");

        Assert.Equal("INVALID_REQUEST", issued.ErrorCode);

        // No row, because without an app the trader cannot sign in — and no
        // money went in either, since that comes after.
        Assert.Equal(0, await db.SimBrokerAccounts.CountAsync());
        Assert.Null(broker.Body("POST", "/admin/accounts/OFB00007/funds"));
    }

    [Fact]
    public async Task Nothing_is_sent_when_the_back_office_key_is_missing()
    {
        var broker = new FakeBroker();
        await using var db = NewDb();
        var service = Build(db, broker, new SimBrokerSettings { BaseUrl = "https://broker.openfno.test" });

        var issued = await service.IssueAsync(7, "Asha", 0m, "admin");

        Assert.Equal("NOT_CONFIGURED", issued.ErrorCode);
        Assert.Contains("SIMBROKER_ADMIN_KEY", issued.ErrorMessage);
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task A_negative_amount_pays_money_out()
    {
        var broker = new FakeBroker();
        await using var db = NewDb();
        var service = Build(db, broker);
        await service.IssueAsync(7, "Asha", 0m, "admin");

        var result = await service.PayAsync(7, -20_000m, "Returned to the desk");

        Assert.True(result.Succeeded);
        var body = JsonDocument.Parse(broker.Body("POST", "/admin/accounts/OFB00007/withdrawals")!).RootElement;
        Assert.Equal(20_000m, body.GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task What_cannot_be_read_is_said_out_loud_and_never_shown_as_zero()
    {
        var broker = new FakeBroker();
        broker.On("GET", "/admin/accounts/OFB00007/positions", _ => (HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"CHAOS_UNAVAILABLE","message":"The broker is unavailable."}}"""));
        await using var db = NewDb();
        var service = Build(db, broker);
        await service.IssueAsync(7, "Asha", 0m, "admin");

        var snapshot = await service.SnapshotAsync(7);

        Assert.True(snapshot.Succeeded);
        Assert.Equal(498_000m, snapshot.Value!.Funds!.Available);
        Assert.Empty(snapshot.Value.Positions);
        Assert.Contains(snapshot.Value.Warnings, w => w.Contains("The broker is unavailable."));
    }

    [Fact]
    public async Task A_trader_with_no_account_is_told_so_rather_than_shown_an_empty_one()
    {
        var broker = new FakeBroker();
        await using var db = NewDb();
        var service = Build(db, broker);

        var snapshot = await service.SnapshotAsync(7);

        Assert.Equal("NOT_LINKED", snapshot.ErrorCode);
        Assert.Null(await service.FindAsync(7));
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task Disabling_the_link_leaves_the_account_at_the_broker_alone()
    {
        var broker = new FakeBroker();
        await using var db = NewDb();
        var service = Build(db, broker);
        await service.IssueAsync(7, "Asha", 0m, "admin");
        int calls = broker.Requests.Count;

        Assert.True(await service.SetEnabledAsync(7, false));

        Assert.False((await service.FindAsync(7))!.IsEnabled);
        Assert.Equal(calls, broker.Requests.Count);
    }

    private static TradingDbContext NewDb()
        => new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"simbroker-{Guid.NewGuid():N}").Options);

    private static SimBrokerAccountService Build(TradingDbContext db, FakeBroker broker, SimBrokerSettings? settings = null)
    {
        settings ??= new SimBrokerSettings
        {
            BaseUrl = "https://broker.openfno.test",
            AdminKey = "back-office-key",
            StaticIp = "13.1.1.1",
        };

        var factory = new SingleClientFactory(new HttpClient(broker));
        var monitor = new StaticOptionsMonitor<SimBrokerSettings>(settings);
        var admin = new SimBrokerAdminClient(factory, monitor, NullLogger<SimBrokerAdminClient>.Instance);
        var client = new SimBrokerClient(factory, monitor, NullLogger<SimBrokerClient>.Instance);
        return new SimBrokerAccountService(db, admin, client, new EphemeralDataProtectionProvider(),
            NullLogger<SimBrokerAccountService>.Instance);
    }

    /// <summary>A stand-in for the broker's back office, which records what it was asked.</summary>
    private sealed class FakeBroker : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<string?, (HttpStatusCode, string)>> _answers = new();

        public List<(string Method, string Path, string? Body, string? AdminKey)> Requests { get; } = new();

        public void On(string method, string path, Func<string?, (HttpStatusCode, string)> answer)
            => _answers[$"{method} {path}"] = answer;

        /// <summary>The body of the last such call, or null when it was never made.</summary>
        public string? Body(string method, string path)
            => Requests.LastOrDefault(r => r.Method == method && r.Path == path).Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            string? key = request.Headers.TryGetValues("X-Admin-Key", out var values) ? values.FirstOrDefault() : null;

            // whoami is the broker's one unauthenticated call; everything under
            // /admin must carry the key.
            if (path.StartsWith("/admin", StringComparison.Ordinal))
            {
                Assert.NotNull(key);
                Requests.Add((request.Method.Method, path, body, key));
            }

            var (status, json) = Answer(request.Method.Method, path, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private (HttpStatusCode, string) Answer(string method, string path, string? body)
        {
            if (_answers.TryGetValue($"{method} {path}", out var answer)) return answer(body);

            return (method, path) switch
            {
                ("GET", "/api/v1/whoami") => (HttpStatusCode.OK, """{"ip":"15.252.66.246"}"""),
                ("POST", "/admin/accounts") => (HttpStatusCode.OK,
                    """{"clientId":"OFB00007","totpSecret":"JBSWY3DPEHPK3PXP","totpUri":"otpauth://totp/x"}"""),
                ("POST", "/admin/accounts/OFB00007/apps") => (HttpStatusCode.OK,
                    """{"clientId":"OFB00007","appId":"APP-NEW","appSecret":"s3cret","staticIps":["15.252.66.246","13.1.1.1"]}"""),
                ("POST", "/admin/accounts/OFB00007/funds") => (HttpStatusCode.OK, FundsJson),
                ("POST", "/admin/accounts/OFB00007/withdrawals") => (HttpStatusCode.OK, FundsJson),
                ("GET", "/admin/accounts/OFB00007/funds") => (HttpStatusCode.OK, FundsJson),
                ("GET", "/admin/accounts/OFB00007/positions") => (HttpStatusCode.OK, "[]"),
                ("GET", "/admin/accounts/OFB00007/orders") => (HttpStatusCode.OK, "[]"),
                ("GET", "/admin/accounts/OFB00007/kill-switch") => (HttpStatusCode.OK,
                    """{"active":false,"since":null,"until":null,"setBy":null}"""),
                _ => (HttpStatusCode.NotFound, """{"error":{"code":"NOT_FOUND","message":"No such call."}}"""),
            };
        }

        private const string FundsJson = """
            {"clientId":"OFB00007","netDeposits":500000,"ledgerBalance":500000,"realisedToday":0,
             "chargesToday":0,"cash":500000,"orderMargin":0,"positionMargin":2000,"unrealised":0,
             "available":498000,"ledger":[]}
            """;
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
}
