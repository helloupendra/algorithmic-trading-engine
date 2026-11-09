using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.Auth;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTrading.UnitTests;

public class BrokerOAuthTests
{
    [Theory]
    [InlineData("url")]
    [InlineData("start")]
    [InlineData("connector")]
    public async Task Every_entry_point_binds_a_random_state_to_the_browser_and_saves_once(string entry)
    {
        using var desk = new Desk();
        var (state, cookie, headers) = await desk.Start(entry);
        Assert.StartsWith("desk-", state);
        Assert.Equal(69, state.Length);
        Assert.DoesNotContain(state, cookie);
        Assert.Contains("httponly", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", headers, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/;", headers, StringComparison.OrdinalIgnoreCase);

        var back = desk.Callback(cookie);
        Assert.IsType<OkObjectResult>(await back.Callback("broker-code", state, "ok", 200, default));
        Assert.Equal(1, desk.Fyers.Exchanges);
        Assert.Equal("fyers", Assert.Single(desk.Saved).ProviderKey);
        Assert.Contains("expires=Thu, 01 Jan 1970", back.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.IsType<BadRequestObjectResult>(await desk.Callback(cookie).Callback("broker-code", state, "ok", 200, default));
        Assert.Equal(1, desk.Fyers.Exchanges);
        Assert.Single(desk.Saved);
    }

    [Theory]
    [InlineData("missing-state")]
    [InlineData("unknown-state")]
    [InlineData("missing-cookie")]
    [InlineData("wrong-cookie")]
    [InlineData("expired")]
    [InlineData("expired-session")]
    [InlineData("restart")]
    public async Task Invalid_callbacks_cannot_exchange_or_replace_the_shared_session(string kind)
    {
        using var desk = new Desk();
        if (kind == "expired-session") desk.TokenExpiry = desk.Time.Now.AddMinutes(1);
        var (state, cookie, _) = await desk.Start();
        if (kind == "missing-state") state = null!;
        if (kind == "unknown-state") state = "desk-" + new string('0', 64);
        if (kind == "missing-cookie") cookie = null!;
        if (kind == "wrong-cookie") cookie = new string('0', 64);
        if (kind == "expired") desk.Time.Now += BrokerOAuthStates.Lifetime;
        if (kind == "expired-session") desk.Time.Now += TimeSpan.FromMinutes(1);
        if (kind == "restart") desk.States = new BrokerOAuthStates(desk.Time);
        Assert.IsType<BadRequestObjectResult>(await desk.Callback(cookie).Callback("broker-code", state, "ok", 200, default));
        Assert.Equal(0, desk.Fyers.Exchanges);
        Assert.Empty(desk.Saved);
    }

    [Fact]
    public async Task Another_browser_does_not_consume_the_owners_flow()
    {
        using var desk = new Desk();
        var (state, cookie, _) = await desk.Start();
        var (_, otherCookie, _) = await desk.Start();
        Assert.IsType<BadRequestObjectResult>(await desk.Callback(otherCookie).Callback("code", state, "ok", 200, default));
        Assert.IsType<OkObjectResult>(await desk.Callback(cookie).Callback("code", state, "ok", 200, default));
        Assert.Equal(1, desk.Fyers.Exchanges);
    }

    [Fact]
    public async Task The_initiating_provider_is_used_even_if_default_routing_changes()
    {
        using var desk = new Desk();
        var (state, cookie, _) = await desk.Start();
        desk.Default = desk.Other;
        Assert.IsType<OkObjectResult>(await desk.Callback(cookie).Callback("code", state, "ok", 200, default));
        Assert.Equal(1, desk.Fyers.Exchanges);
        Assert.Equal(0, desk.Other.Exchanges);
        Assert.Equal("fyers", Assert.Single(desk.Saved).ProviderKey);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("demoted")]
    [InlineData("revoked")]
    [InlineData("revoked-same-second")]
    [InlineData("deleted")]
    public async Task The_initiating_administrator_must_still_be_authorized(string change)
    {
        using var desk = new Desk();
        var (state, cookie, _) = await desk.Start();
        var admin = await desk.Db.AppUsers.SingleAsync();
        if (change == "disabled") admin.IsActive = false;
        if (change == "demoted") admin.Role = UserRoles.Trader;
        if (change == "revoked") admin.TokensValidFromUtc = desk.Time.Now.UtcDateTime.AddSeconds(1);
        if (change == "revoked-same-second") admin.TokensValidFromUtc = desk.Time.Now.UtcDateTime;
        if (change == "deleted") desk.Db.Remove(admin);
        await desk.Db.SaveChangesAsync();
        Assert.IsType<BadRequestObjectResult>(await desk.Callback(cookie).Callback("code", state, "ok", 200, default));
        Assert.Equal(0, desk.Fyers.Exchanges);
        Assert.Empty(desk.Saved);
    }

    [Fact]
    public async Task Console_session_changes_revoke_the_browser_flow()
    {
        using var desk = new Desk();
        var (state, cookie, _) = await desk.Start();
        var context = desk.Callback(cookie).HttpContext;
        desk.States.EndBrowserSession(context);
        Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.IsType<BadRequestObjectResult>(await desk.Callback(cookie).Callback("code", state, "ok", 200, default));
        Assert.Equal(0, desk.Fyers.Exchanges);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    public async Task Console_endpoints_invalidate_pending_broker_flows(string action)
    {
        using var desk = new Desk();
        var (state, cookie, _) = await desk.Start();
        var context = desk.Callback(cookie).ControllerContext;
        var auth = Stub<IAuthService>.Create((name, _) => name switch
        {
            "LoginAsync" => Task.FromResult(new AuthResponse { User = new() { Id = 2, UserName = "next", Role = UserRoles.Admin } }),
            "LogoutAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(name),
        });
        var controller = new UserAuthController(auth, desk.States) { ControllerContext = context };
        if (action == "login") Assert.IsType<OkObjectResult>(await controller.Login(new(), default));
        else Assert.IsType<OkObjectResult>(await controller.Logout(new(), default));
        Assert.Null(desk.States.Consume(state, cookie));
        Assert.Equal(0, desk.Fyers.Exchanges);
    }

    [Fact]
    public async Task Concurrent_callbacks_have_only_one_winner()
    {
        using var desk = new Desk();
        var (state, cookie, _) = await desk.Start();
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => desk.States.Consume(state, cookie))));
        Assert.Single(results, r => r is not null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Missing_code_or_failed_exchange_consumes_the_flow_without_saving(bool missingCode)
    {
        using var desk = new Desk();
        var (state, cookie, _) = await desk.Start();
        desk.Fyers.FailExchange = true;
        await desk.Callback(cookie).Callback(missingCode ? null : "code", state, "error", 400, default);
        Assert.Empty(desk.Saved);
        Assert.Null(desk.States.Consume(state, cookie));
        Assert.Equal(missingCode ? 0 : 1, desk.Fyers.Exchanges);
    }

    [Fact]
    public async Task Portfolio_callbacks_keep_their_separate_validation_path()
    {
        using var desk = new Desk();
        var controller = desk.Callback(null);
        controller.Request.QueryString = new QueryString("?state=pf-test&auth_code=code");
        var result = Assert.IsType<RedirectResult>(await controller.Callback("code", "pf-test", "ok", 200, default));
        Assert.Equal("/api/Portfolio/callback/fyers?state=pf-test&auth_code=code", result.Url);
        Assert.Equal(0, desk.Fyers.Exchanges);
        Assert.Empty(desk.Saved);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Broker(string key) : IBrokerProvider
    {
        public ProviderDescriptor Descriptor { get; } = new(key, key.ToUpperInvariant(), ProviderKind.Both, ProviderAuthKind.OAuthDaily, new());
        public int Exchanges;
        public bool FailExchange;
        public Task<string> GetAuthUrlAsync(string? state = null, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.FromResult($"https://broker.example/authorize?state={state}");
        public Task<BrokerTokenResult> ExchangeAuthCodeAsync(string authCode, long? brokerAccountId = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Exchanges);
            return Task.FromResult(FailExchange ? BrokerTokenResult.Failed("Declined") : new BrokerTokenResult("fixture-broker-access", "fixture-broker-refresh"));
        }
        public Task<BrokerTokenResult> RefreshAccessTokenAsync(string refreshToken, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Desk : IDisposable
    {
        public readonly Clock Time = new();
        public BrokerOAuthStates States;
        public readonly TradingDbContext Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public readonly Broker Fyers = new("fyers"), Other = new("other");
        public Broker Default;
        public DateTimeOffset TokenExpiry;
        public readonly ConcurrentBag<BrokerSession> Saved = [];
        private readonly IProviderRegistry _registry;
        private readonly IProviderRouter _router;
        private readonly IBrokerSessionStore _sessions;
        public Desk()
        {
            States = new(Time);
            Default = Fyers;
            TokenExpiry = Time.Now.AddHours(1);
            Db.AppUsers.Add(new AppUser { Id = 1, UserName = "admin", Role = UserRoles.Admin, IsActive = true });
            Db.SaveChanges();
            _registry = Stub<IProviderRegistry>.Create((name, args) => name == "GetBrokerProvider" ? (args![0] as string == "fyers" ? Fyers : Other) : throw new NotSupportedException(name));
            _router = Stub<IProviderRouter>.Create((name, _) => name == "ResolveBrokerAsync" ? Task.FromResult<IBrokerProvider>(Default) : throw new NotSupportedException(name));
            _sessions = Stub<IBrokerSessionStore>.Create((name, args) =>
            {
                if (name != "SaveAsync") throw new NotSupportedException(name);
                Saved.Add((BrokerSession)args![0]!);
                return Task.CompletedTask;
            });
        }
        private BrokerOAuthFlow Flow() => new(States, _registry, Db);
        public AuthController Callback(string? cookie)
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("openfno.test");
            if (cookie is not null) context.Request.Headers.Cookie = $"{BrokerOAuthStates.CookieName}={cookie}";
            return new AuthController(Flow(), _sessions, _router, new ConfigurationBuilder().Build())
            { ControllerContext = new() { HttpContext = context } };
        }
        public async Task<(string State, string Cookie, string Headers)> Start(string entry = "url")
        {
            var controller = Callback(null);
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, UserRoles.Admin),
                new Claim("iat", Time.Now.ToUnixTimeSeconds().ToString()), new Claim("exp", TokenExpiry.ToUnixTimeSeconds().ToString()),
            ], "test"));
            IActionResult result;
            if (entry == "connector")
            {
                var catalog = Stub<IProviderCatalog>.Create((_, _) => Fyers.Descriptor);
                var providers = new ProvidersController(catalog, _registry, _router, null!, _sessions, Db, NullLogger<ProvidersController>.Instance, [], Flow())
                { ControllerContext = controller.ControllerContext };
                result = await providers.GetAuthUrl("fyers", default);
            }
            else result = entry == "start" ? await controller.Start(default) : await controller.GetAuthUrl(default);
            string url = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value).GetProperty("authUrl").GetString()!;
            string headers = controller.Response.Headers.SetCookie.ToString();
            string cookie = headers.Split(';')[0].Split('=', 2)[1];
            return (url.Split("state=")[1], cookie, headers);
        }
        public void Dispose() => Db.Dispose();
    }

    public class Stub<T> : DispatchProxy where T : class
    {
        private Func<string, object?[]?, object?> _invoke = null!;
        public static T Create(Func<string, object?[]?, object?> invoke)
        {
            T proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy)._invoke = invoke;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _invoke(targetMethod!.Name, args);
    }
}
