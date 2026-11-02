using System.Collections.Concurrent;
using System.Net;
using AlgoTrading.Infrastructure.Providers.Angel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// With a stale PIN or TOTP, the Movers page used to send about 400 wrong sign-ins an
/// hour (2 Nov 2026 review), and "Invalid Token" (AG8001) never cleared the session.
/// </summary>
public class AngelSignInGuardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_refusal_holds_the_next_sign_in_and_the_third_stops_them()
    {
        var guard = new AngelSignInGuard();
        Assert.Null(guard.HeldBecause(T0));

        Assert.Equal(TimeSpan.FromMinutes(15), guard.NoteRefused("Invalid PIN", T0));
        Assert.Contains("before 09:45 IST", guard.HeldBecause(T0.AddMinutes(14)));
        Assert.Contains("Invalid PIN", guard.HeldBecause(T0.AddMinutes(14)));
        Assert.Null(guard.HeldBecause(T0.AddMinutes(15)));

        var second = T0.AddMinutes(15);
        Assert.Equal(TimeSpan.FromHours(1), guard.NoteRefused("Invalid PIN", second));
        Assert.NotNull(guard.HeldBecause(second.AddMinutes(59)));
        Assert.Null(guard.HeldBecause(second.AddHours(1)));

        Assert.Null(guard.NoteRefused("Invalid PIN", second.AddHours(1)));
        Assert.Contains("refused 3 sign-ins in a row", guard.HeldBecause(second.AddDays(30)));
    }

    [Fact]
    public void A_refused_pin_cannot_be_tried_more_than_three_times_however_often_the_page_asks()
    {
        var guard = new AngelSignInGuard();
        int attempts = 0;

        // Every 45 s for a week, six calls a snapshot, as the Movers page asks.
        for (var now = T0; now < T0.AddDays(7); now = now.AddSeconds(45))
        {
            for (int call = 0; call < 6; call++)
            {
                if (guard.HeldBecause(now) is not null) continue;
                attempts++;
                guard.NoteRefused("Invalid PIN", now);
            }
        }

        Assert.Equal(AngelSignInGuard.MaxRefused, attempts);
    }

    [Fact]
    public void A_failure_that_is_not_a_refusal_backs_off_and_never_stops()
    {
        var guard = new AngelSignInGuard();
        var waits = Enumerable.Range(0, 7).Select(_ => guard.NoteFailed("Could not reach Angel One", T0).TotalMinutes).ToList();

        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 30, 30 }, waits);
        Assert.Null(guard.HeldBecause(T0.AddMinutes(30)));
        Assert.Equal(0, guard.Refused);
    }

    [Fact]
    public void Signing_in_clears_everything()
    {
        var guard = new AngelSignInGuard();
        for (int i = 0; i < AngelSignInGuard.MaxRefused; i++) guard.NoteRefused("Invalid PIN", T0);
        Assert.NotNull(guard.HeldBecause(T0));

        guard.NoteSignedIn();

        Assert.Null(guard.HeldBecause(T0));
        Assert.Equal(TimeSpan.FromMinutes(15), guard.NoteRefused("Invalid PIN", T0));
    }

    [Fact]
    public void A_session_rejected_as_it_started_holds_the_next_sign_in()
    {
        var guard = new AngelSignInGuard();
        guard.NoteYoungSessionRejected("Invalid Token [AG8001]", T0);

        Assert.Contains("Invalid Token", guard.HeldBecause(T0.AddMinutes(4)));
        Assert.Null(guard.HeldBecause(T0.AddMinutes(5)));
    }
}

/// <summary>
/// The real client against a scripted SmartAPI. It keeps its session in statics (it is
/// created per request), so these run one at a time and put the statics back.
/// </summary>
[Collection(nameof(AngelClientStatics))]
public sealed class AngelSignInClientTests : IDisposable
{
    private const string Refused = """{"status":false,"message":"Invalid PIN","errorcode":"AB1011","data":null}""";
    private const string SignedIn = """{"status":true,"message":"SUCCESS","errorcode":"","data":{"jwtToken":"jwt-1"}}""";
    private const string InvalidToken = """{"status":false,"message":"Invalid Token","errorcode":"AG8001","data":""}""";
    private const string Fine = """{"status":true,"message":"SUCCESS","errorcode":"","data":{"ltp":1}}""";

    private readonly Script _angel = new();
    private readonly Clock _time = new(new DateTimeOffset(2026, 11, 2, 4, 0, 0, TimeSpan.Zero));
    private readonly AngelApiClient _client;

    public AngelSignInClientTests()
    {
        Reset();
        _client = new AngelApiClient(new Factory(_angel),
            Options.Create(new AngelSettings
            {
                ApiKey = "k",
                ClientCode = "A1",
                Pin = "1234",
                TotpSecret = "GEZDGNBVGY3TQOJQ",
                RootUrl = "https://angel.invalid",
            }),
            NullLogger<AngelApiClient>.Instance, _time);
    }

    public void Dispose() => Reset();

    private static void Reset()
    {
        AngelApiClient.ForgetSession();
        AngelApiClient.SignInGuard.NoteSignedIn();
    }

    private Task<AngelResult> Ltp() => _client.LtpAsync("NSE", "Nifty 50", "99926000", CancellationToken.None);

    [Fact]
    public async Task A_refused_sign_in_is_not_repeated_by_the_next_calls()
    {
        _angel.Answers.Enqueue((HttpStatusCode.OK, Refused));

        var first = await Ltp();
        var second = await Ltp();
        var third = await _client.CallAsync("/x", new { }, CancellationToken.None);

        Assert.Contains("Invalid PIN", first.Message);
        Assert.StartsWith("Not signing in to Angel One again before", second.Message);
        Assert.StartsWith("Not signing in to Angel One again before", third.Message);
        Assert.Single(_angel.Posts);
    }

    [Fact]
    public async Task The_held_sign_in_goes_again_when_its_wait_is_over()
    {
        _angel.Answers.Enqueue((HttpStatusCode.OK, Refused));
        _angel.Answers.Enqueue((HttpStatusCode.OK, SignedIn));

        await _client.LoginAsync(false, CancellationToken.None);
        _time.Advance(AngelSignInGuard.RefusedHolds[0]);
        var login = await _client.LoginAsync(false, CancellationToken.None);

        Assert.True(login.Ok);
        Assert.Equal(2, _angel.Posts.Count);
        Assert.Equal(0, AngelApiClient.SignInGuard.Refused);
    }

    [Fact]
    public async Task A_person_can_still_force_a_sign_in_and_its_success_clears_the_hold()
    {
        _angel.Answers.Enqueue((HttpStatusCode.OK, Refused));
        _angel.Answers.Enqueue((HttpStatusCode.OK, SignedIn));

        await _client.LoginAsync(false, CancellationToken.None);
        Assert.False((await _client.LoginAsync(false, CancellationToken.None)).Ok);

        var forced = await _client.LoginAsync(true, CancellationToken.None);

        Assert.True(forced.Ok);
        Assert.Null(AngelApiClient.SignInGuard.HeldBecause(_time.GetUtcNow()));
    }

    [Fact]
    public async Task A_failure_that_is_not_a_refusal_does_not_count_against_the_account()
    {
        _angel.Answers.Enqueue((HttpStatusCode.BadGateway, "<html>bad gateway</html>"));

        var login = await _client.LoginAsync(false, CancellationToken.None);

        Assert.False(login.Ok);
        Assert.False(login.Refused);
        Assert.Equal(0, AngelApiClient.SignInGuard.Refused);
        Assert.NotNull(AngelApiClient.SignInGuard.HeldBecause(_time.GetUtcNow()));
    }

    [Fact]
    public async Task Invalid_token_forgets_the_session_and_the_next_call_signs_in_after_its_wait()
    {
        _angel.Answers.Enqueue((HttpStatusCode.OK, SignedIn));
        _angel.Answers.Enqueue((HttpStatusCode.OK, InvalidToken));

        Assert.True((await _client.LoginAsync(false, CancellationToken.None)).Ok);
        Assert.True(_client.HasSession);

        var call = await Ltp();

        Assert.False(call.Ok);
        Assert.Contains("the session was rejected", call.Message);
        Assert.False(_client.HasSession);

        // It had only just started, so the next call does not sign straight in again.
        var next = await Ltp();
        Assert.StartsWith("Not signing in to Angel One again before", next.Message);
        Assert.Equal(2, _angel.Posts.Count);
    }

    [Fact]
    public async Task An_http_401_forgets_the_session_too()
    {
        _angel.Answers.Enqueue((HttpStatusCode.OK, SignedIn));
        _angel.Answers.Enqueue((HttpStatusCode.Unauthorized, "Unauthorized"));

        await _client.LoginAsync(false, CancellationToken.None);
        await Ltp();

        Assert.False(_client.HasSession);
    }

    [Theory]
    [InlineData(200, "AG8001", true)]
    [InlineData(200, "ag8002", true)]
    [InlineData(200, "AG8003", true)]
    [InlineData(200, "AB1010", true)]
    [InlineData(200, "AB8050", true)]
    [InlineData(200, "AB8051", true)]
    [InlineData(401, "", true)]
    [InlineData(200, "AB1011", false)]
    [InlineData(200, null, false)]
    public void What_counts_as_a_rejected_session(int status, string? code, bool rejected)
    {
        Assert.Equal(rejected, AngelApiClient.IsRejectedSession(status, code));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }

    private sealed class Script : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpStatusCode Status, string Body)> Answers { get; } = new();
        public ConcurrentQueue<string> Posts { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Posts.Enqueue(request.RequestUri!.AbsolutePath);
            var (status, body) = Answers.TryDequeue(out var answer) ? answer : (HttpStatusCode.OK, Fine);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

[CollectionDefinition(nameof(AngelClientStatics), DisableParallelization = true)]
public sealed class AngelClientStatics;
