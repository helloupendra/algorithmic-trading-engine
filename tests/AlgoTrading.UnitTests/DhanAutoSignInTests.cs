using System.Net;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Dhan's PIN + TOTP sign-in, and the rule that decides when the desk takes a
/// token by itself. The rule matters more than the call: a sign-in at the wrong
/// moment can cut off a feed that was streaming, and one retried after a refusal
/// can lock the account.
/// </summary>
public class DhanAutoSignInTests
{
    private const string ClientId = "1113706926";
    private const string Pin = "482913";
    // RFC 6238's SHA-1 test key, "12345678901234567890", in base32.
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    // Tuesday 29 Sep 2026, 08:05:10 IST: inside the morning window, early in a TOTP step.
    private static readonly DateTimeOffset TuesdayMorning = new(2026, 9, 29, 2, 35, 10, TimeSpan.Zero);

    // ------------------------------------------------------------ the call

    [Fact]
    public async Task Sends_the_client_id_PIN_and_the_current_code_and_saves_the_session()
    {
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","dhanClientName":"X","accessToken":"jwt-totp","expiryTime":"2026-09-30T08:05:10"}""");
        var (flow, sessions) = Build(handler);

        var signIn = await flow.SignInWithTotpAsync();

        string code = Totp.Generate(Secret, TuesdayMorning);
        Assert.Equal($"https://auth.dhan.co/app/generateAccessToken?dhanClientId={ClientId}&pin={Pin}&totp={code}", handler.LastUrl);
        Assert.Equal("POST", handler.LastMethod);
        var saved = Assert.Single(sessions.Saved);
        Assert.Equal("dhan", saved.ProviderKey);
        Assert.Equal("jwt-totp", saved.AccessToken);
        Assert.Equal(TuesdayMorning.UtcDateTime.AddHours(24), signIn.ExpiresUtc);
    }

    [Fact]
    public async Task A_refusal_is_marked_refused_and_never_repeats_the_PIN()
    {
        var handler = new Handler("""{"errorCode":"DH-906","errorMessage":"Invalid TOTP","pin":"482913"}""", HttpStatusCode.BadRequest);
        var (flow, sessions) = Build(handler);

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Refused, ex.Failure);
        Assert.Contains("Invalid TOTP", ex.Message);
        Assert.DoesNotContain(Pin, ex.Message);
        Assert.Empty(sessions.Saved);
    }

    [Fact]
    public async Task A_200_without_a_token_is_a_refusal_not_a_sign_in()
    {
        var (flow, sessions) = Build(new Handler("""{"status":"failure","message":"Invalid pin"}"""));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Refused, ex.Failure);
        Assert.Empty(sessions.Saved);
    }

    [Fact]
    public async Task A_server_error_is_worth_another_try()
    {
        var (flow, _) = Build(new Handler("<html>bad gateway</html>", HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Unreachable, ex.Failure);
    }

    [Fact]
    public async Task Names_what_is_missing_without_calling_Dhan()
    {
        var handler = new Handler("{}");
        var (flow, _) = Build(handler, pin: "");

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.NotSetUp, ex.Failure);
        Assert.Contains("DHAN_PIN", ex.Message);
        Assert.DoesNotContain("DHAN_TOTP_SECRET", ex.Message);
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    [Fact]
    public async Task A_secret_that_is_not_base32_names_the_setting()
    {
        var (flow, _) = Build(new Handler("{}"), secret: "not-base32!");

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.NotSetUp, ex.Failure);
        Assert.Contains("DHAN_TOTP_SECRET", ex.Message);
        Assert.DoesNotContain("!", ex.Message);
    }

    [Fact]
    public async Task A_token_for_another_account_is_not_saved()
    {
        var (flow, sessions) = Build(new Handler("""{"dhanClientId":"9999999999","accessToken":"jwt-other"}"""));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Refused, ex.Failure);
        Assert.DoesNotContain("9999999999", ex.Message);
        Assert.Empty(sessions.Saved);
    }

    // ------------------------------------------------------------ when to sign in

    private static readonly DhanAutoSignInSettings Window = new();

    private static DateTime Ist(int day, int hour, int minute) =>
        new DateTime(2026, 9, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    [Fact]
    public void Signs_in_when_there_is_no_valid_token_on_a_weekday()
    {
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 13, 0), null, Window));
    }

    [Fact]
    public void Never_at_the_weekend()
    {
        // Saturday 26 and Sunday 27 Sep.
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(26, 8, 10), null, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(27, 20, 0), null, Window));
    }

    [Fact]
    public void Replaces_a_token_that_would_die_during_today_only_in_the_morning_window()
    {
        var endsAtOnePm = Ist(29, 13, 0);

        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 8, 5), endsAtOnePm, Window));
        // Before the window, and once the feeds are streaming: left alone.
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 7, 30), endsAtOnePm, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 10, 0), endsAtOnePm, Window));
    }

    [Fact]
    public void A_token_that_lasts_past_tonight_is_left_alone_even_in_the_window()
    {
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 8, 5), Ist(30, 7, 0), Window));
    }

    [Fact]
    public void A_token_about_to_end_counts_as_gone_at_any_hour()
    {
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 13, 55), Ist(29, 14, 0), Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 13, 40), Ist(29, 14, 0), Window));
    }

    // ------------------------------------------------------------ how often

    [Fact]
    public void A_refusal_stops_the_machine_for_the_day_but_not_the_next()
    {
        var state = new DhanAutoSignInState();
        var now = Ist(29, 8, 5);

        bool dayOver = state.Failed(now, "automatic", "Invalid TOTP", DhanSignInFailure.Refused);

        Assert.True(dayOver);
        Assert.False(state.MayTryAutomatically(now.AddHours(3), out _));
        Assert.True(state.MayTryAutomatically(Ist(30, 8, 0), out _));
    }

    [Fact]
    public void An_unreachable_Dhan_is_tried_again_after_a_pause_three_times_a_day()
    {
        var state = new DhanAutoSignInState();
        var now = Ist(29, 8, 0);

        Assert.False(state.Failed(now, "automatic", "down", DhanSignInFailure.Unreachable));
        Assert.False(state.MayTryAutomatically(now.AddMinutes(5), out _));
        Assert.True(state.MayTryAutomatically(now.AddMinutes(16), out _));

        Assert.False(state.Failed(now.AddMinutes(16), "automatic", "down", DhanSignInFailure.Unreachable));
        Assert.True(state.Failed(now.AddMinutes(32), "automatic", "down", DhanSignInFailure.Unreachable));
        Assert.False(state.MayTryAutomatically(now.AddHours(2), out _));
    }

    // ------------------------------------------------------------ the worker

    [Fact]
    public async Task The_worker_does_nothing_without_the_PIN_and_secret()
    {
        var handler = new Handler("{}");
        using var provider = Services(handler, new DhanSettings(), current: null);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.Null(result);
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    [Fact]
    public async Task The_worker_signs_in_when_the_day_has_no_token_and_says_so()
    {
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        var notifier = new Notifier();
        using var provider = Services(handler, new DhanSettings { Pin = Pin, TotpSecret = Secret }, current: null, notifier);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.True(result!.Ok);
        Assert.Contains("generateAccessToken", handler.LastUrl);
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Success, sent.Severity);
        Assert.DoesNotContain(Pin, sent.Message);
        Assert.True(provider.GetRequiredService<DhanAutoSignInState>().LastOk);
    }

    [Fact]
    public async Task The_worker_leaves_a_token_that_covers_the_day_alone()
    {
        var handler = new Handler("{}");
        // Signed in at 07:00 IST today: valid until 07:00 tomorrow.
        var current = new BrokerSession { ProviderKey = "dhan", AccessToken = "jwt-live", IsActive = true, UpdatedUtc = Ist(29, 7, 0) };
        using var provider = Services(handler, new DhanSettings { Pin = Pin, TotpSecret = Secret }, current);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.Null(result);
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    // ------------------------------------------------------------ a pasted token

    [Fact]
    public async Task An_expired_pasted_token_is_not_served_and_the_reason_says_when_it_died()
    {
        var api = ApiWithConfiguredToken(Jwt(DateTimeOffset.UtcNow.AddDays(-12)));

        var ex = await Assert.ThrowsAsync<DhanApiException>(() => api.ResolveTokenAsync());

        Assert.Contains("DHAN_ACCESS_TOKEN", ex.Message);
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task A_live_pasted_token_is_served_with_its_end()
    {
        var ends = DateTimeOffset.UtcNow.AddHours(5);
        var api = ApiWithConfiguredToken(Jwt(ends));

        var (_, token) = await api.ResolveTokenAsync();

        Assert.Equal("configuration", token.Source);
        Assert.Equal(ends.ToUnixTimeSeconds(), new DateTimeOffset(token.ExpiresUtc!.Value).ToUnixTimeSeconds());
    }

    // ---------------------------------------------------------------- fixtures

    private static (DhanLoginFlow Flow, Sessions Sessions) Build(Handler handler, string pin = Pin, string secret = Secret)
    {
        var sessions = new Sessions();
        var flow = new DhanLoginFlow(
            new SettingsMonitor(new DhanSettings { Pin = pin, TotpSecret = secret }),
            new Credentials(ClientId),
            sessions,
            new Factory(handler),
            NullLogger<DhanLoginFlow>.Instance,
            new FixedTime(TuesdayMorning));
        return (flow, sessions);
    }

    private static ServiceProvider Services(Handler handler, DhanSettings settings, BrokerSession? current, Notifier? notifier = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<DhanSettings>>(new SettingsMonitor(settings));
        services.AddSingleton<IBrokerCredentialsProvider>(new Credentials(ClientId));
        services.AddSingleton<IBrokerSessionStore>(new Sessions { Current = current });
        services.AddSingleton<IHttpClientFactory>(new Factory(handler));
        services.AddSingleton<ISystemNotifier>(notifier ?? new Notifier());
        services.AddSingleton<TimeProvider>(new FixedTime(TuesdayMorning));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DhanAutoSignInState>();
        services.AddScoped(sp => new DhanLoginFlow(
            sp.GetRequiredService<IOptionsMonitor<DhanSettings>>(), sp.GetRequiredService<IBrokerCredentialsProvider>(),
            sp.GetRequiredService<IBrokerSessionStore>(), sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<DhanLoginFlow>.Instance, sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new DhanAutoSignInService(
            sp.GetRequiredService<DhanLoginFlow>(), sp.GetRequiredService<DhanAutoSignInState>(),
            sp.GetRequiredService<ISystemNotifier>(), NullLogger<DhanAutoSignInService>.Instance, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new DhanAutoSignInWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IOptionsMonitor<DhanSettings>>(),
            sp.GetRequiredService<DhanAutoSignInState>(), NullLogger<DhanAutoSignInWorker>.Instance, sp.GetRequiredService<TimeProvider>()));
        return services.BuildServiceProvider();
    }

    private static DhanApiClient ApiWithConfiguredToken(string token) => new(
        Options.Create(new DhanSettings { AccessToken = token }),
        new Credentials(ClientId),
        new Sessions(),
        new Factory(new Handler("{}")),
        new DhanRateGate(),
        NullLogger<DhanApiClient>.Instance);

    private static string Jwt(DateTimeOffset expires)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"HS512","typ":"JWT"}""")}.{Part($$"""{"iss":"dhan","dhanClientId":"{{ClientId}}","exp":{{expires.ToUnixTimeSeconds()}}}""")}.c2lnbmF0dXJl";
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SettingsMonitor(DhanSettings value) : IOptionsMonitor<DhanSettings>
    {
        public DhanSettings CurrentValue => value;
        public DhanSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<DhanSettings, string?> listener) => null;
    }

    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = string.Empty;
        public string LastMethod { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.OriginalString;
            LastMethod = request.Method.Method;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Credentials(string clientId) : IBrokerCredentialsProvider
    {
        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BrokerCredentials(clientId, "secret-456", string.Empty, null, "test", null, null));

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

    private sealed class Notifier : ISystemNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string Message)> Sent { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((severity, title, message));
            return Task.CompletedTask;
        }
    }
}
