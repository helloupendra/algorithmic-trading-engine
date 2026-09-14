using System.Net;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Dhan;
using AlgoTrading.Infrastructure.Session;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Dhan's daily browser sign-in, and the guard that keeps its session away from
/// everything that means "the FYERS session".
/// </summary>
public class DhanLoginTests
{
    private const string ClientId = "1113706926";

    // ------------------------------------------------------------ the sign-in

    [Fact]
    public async Task Connect_sends_the_API_key_and_returns_Dhans_login_page()
    {
        var handler = new Handler("""{"consentAppId":"940b0ca1","consentAppStatus":"GENERATED","status":"success"}""");
        var (flow, _) = Build(handler);

        string url = await flow.GetLoginUrlAsync();

        Assert.Equal("https://auth.dhan.co/login/consentApp-login?consentAppId=940b0ca1", url);
        Assert.Equal($"https://auth.dhan.co/app/generate-consent?client_id={ClientId}", handler.LastUrl);
        Assert.Equal("key-123", handler.LastHeaders["app_id"]);
        Assert.Equal("secret-456", handler.LastHeaders["app_secret"]);
    }

    [Fact]
    public async Task The_callback_saves_a_session_that_lasts_24_hours()
    {
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","dhanClientName":"X","accessToken":"jwt-abc","expiryTime":"2026-09-15T16:00:00"}""");
        var (flow, sessions) = Build(handler);

        var signIn = await flow.CompleteAsync("token-id-1");

        Assert.Equal("https://auth.dhan.co/app/consumeApp-consent?tokenId=token-id-1", handler.LastUrl);
        var saved = Assert.Single(sessions.Saved);
        Assert.Equal("dhan", saved.ProviderKey);
        Assert.Equal("jwt-abc", saved.AccessToken);
        Assert.InRange(signIn.ExpiresUtc - DateTime.UtcNow, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));
    }

    [Fact]
    public async Task The_callback_refuses_a_sign_in_to_another_account()
    {
        var handler = new Handler("""{"dhanClientId":"9999999999","accessToken":"jwt-other"}""");
        var (flow, sessions) = Build(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => flow.CompleteAsync("token-id-2"));

        Assert.Contains("Nothing was saved", ex.Message);
        Assert.DoesNotContain("9999999999", ex.Message);
        Assert.Empty(sessions.Saved);
    }

    [Fact]
    public async Task Connect_explains_a_missing_API_key()
    {
        var (flow, _) = Build(new Handler("{}"), apiKey: "");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => flow.GetLoginUrlAsync());
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public void Dhan_tokens_expire_24_hours_after_issue_and_FYERS_rule_is_unchanged()
    {
        var issued = new DateTime(2026, 9, 14, 11, 0, 0, DateTimeKind.Utc);
        Assert.Equal(issued.AddHours(24), BrokerSession.TokenExpiryUtc("dhan", null, issued));
        // FYERS: 06:00 IST the next morning = 00:30 UTC.
        Assert.Equal(new DateTime(2026, 9, 15, 0, 30, 0, DateTimeKind.Utc), BrokerSession.TokenExpiryUtc("fyers", null, issued));
    }

    // --------------------------------------------- the FYERS session stays FYERS

    [Fact]
    public async Task The_platform_session_is_never_another_connectors()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"sessions-{Guid.NewGuid():N}").Options;
        await using var db = new TradingDbContext(options);
        var store = new DatabaseBrokerSessionStore(db, new EphemeralDataProtectionProvider());

        await store.SaveAsync(new BrokerSession { ProviderKey = "fyers", BrokerName = "FYERS", AccessToken = "fyers-token" });
        await Task.Delay(20);
        // Newer, and before 2026-09-14 it would have been returned as "the" session.
        await store.SaveAsync(new BrokerSession { ProviderKey = "dhan", BrokerName = "DHAN", AccessToken = "dhan-token" });

        Assert.Equal("fyers-token", (await store.GetCurrentAsync())!.AccessToken);
        Assert.Equal("dhan-token", (await store.GetForProviderAsync("dhan"))!.AccessToken);
    }

    [Fact]
    public async Task Calls_use_the_signed_in_session_before_a_configured_token()
    {
        var sessions = new Sessions();
        var api = new DhanApiClient(
            Options.Create(new DhanSettings { AccessToken = "pasted-token" }),
            new Credentials(ClientId, "secret-456"),
            sessions,
            new Factory(new Handler("{}")),
            new DhanRateGate(),
            NullLogger<DhanApiClient>.Instance);

        var (_, before) = await api.ResolveTokenAsync();
        Assert.Equal("configuration", before.Source);

        sessions.Current = new BrokerSession { ProviderKey = "dhan", AccessToken = "signed-in-token", IsActive = true, UpdatedUtc = DateTime.UtcNow };
        var (_, after) = await api.ResolveTokenAsync();
        Assert.Equal("sign-in", after.Source);
        Assert.Equal("signed-in-token", after.Value);

        // An expired sign-in falls back rather than being sent to Dhan to fail.
        sessions.Current.UpdatedUtc = DateTime.UtcNow.AddHours(-25);
        var (_, expired) = await api.ResolveTokenAsync();
        Assert.Equal("configuration", expired.Source);
    }

    // ---------------------------------------------------------------- fixtures

    private static (DhanLoginFlow Flow, Sessions Sessions) Build(Handler handler, string apiKey = "key-123")
    {
        var sessions = new Sessions();
        var flow = new DhanLoginFlow(
            Options.Create(new DhanSettings { ApiKey = apiKey }),
            new Credentials(ClientId, "secret-456"),
            sessions,
            new Factory(handler),
            NullLogger<DhanLoginFlow>.Instance);
        return (flow, sessions);
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = string.Empty;
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.OriginalString;
            LastHeaders.Clear();
            foreach (var h in request.Headers) LastHeaders[h.Key] = string.Join(",", h.Value);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Credentials(string clientId, string secret) : IBrokerCredentialsProvider
    {
        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BrokerCredentials(clientId, secret, string.Empty, null, "test", null, null));

        public Task SaveAsync(string providerKey, string clientId, string secretKey, string redirectUri, string updatedBy,
            string? tradingPin = null, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class Sessions : IBrokerSessionStore
    {
        public List<BrokerSession> Saved { get; } = new();
        public BrokerSession? Current { get; set; }

        public Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task<BrokerSession?> GetForAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task ClearAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerSession?> GetForProviderAsync(string providerKey, CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default) { Saved.Add(session); return Task.CompletedTask; }
        public Task ClearAsync(string? providerKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
