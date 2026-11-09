using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Application.UseCases.Auth;
using AlgoTrading.Application.UseCases.Equities;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Application.UseCases.MarketData;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The desk's shared state is written only by the accounts that legitimately
/// write it: the feed's recording list, the option chain, the broker history
/// fetch (the Service account and an admin), and the platform's FYERS session
/// (an admin). A trader is refused before anything is stored or sent.
/// </summary>
/// <remarks>
/// Until 2 Nov each of these needed only a signed-in account, any account: a
/// trader could delete the watchlist rows a live run's contracts were
/// subscribed through, post an option-chain row that the chain view then served
/// to the strategies trading on it, sign the desk out of FYERS, and spend the
/// desk's broker history limits. The requests go through the API's own
/// policies and the controllers' own attributes, in an in-process host.
/// </remarks>
public class DeskWriteAccessTests
{
    internal const long AdminId = 1;
    internal const long ServiceId = 5;
    internal const long TraderId = 8;

    public static TheoryData<string, string, string?> RecordingListWrites => new()
    {
        { "POST", "/api/LiveData/watchlist", """{"symbol":"NSE:SBIN-EQ","dataType":"symbolUpdate","isActive":true,"priority":0}""" },
        { "DELETE", "/api/LiveData/watchlist/1", null },
        { "POST", "/api/LiveData/watchlist/prune", """{"ids":[]}""" },
    };

    [Theory]
    [MemberData(nameof(RecordingListWrites))]
    public async Task A_trader_cannot_change_the_feeds_recording_list(string method, string path, string? body)
    {
        await using var host = await Host.StartAsync<LiveDataController>(LiveDataServices);

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync(method, path, body, TraderId));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync(method, path, body, userId: null));
        Assert.Empty(host.Calls);

        // The runners, chain tracker and tools sign in as Service; the Data pages and the morning job as admin.
        AssertSucceeded(await host.SendAsync(method, path, body, ServiceId), path);
        AssertSucceeded(await host.SendAsync(method, path, body, AdminId), path);
    }

    [Fact]
    public async Task A_trader_who_deletes_a_watchlist_row_reaches_neither_the_store_nor_the_feed()
    {
        await using var host = await Host.StartAsync<LiveDataController>(LiveDataServices);

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("DELETE", "/api/LiveData/watchlist/1", null, TraderId));
        Assert.DoesNotContain("ILiveDataService.RemoveWatchlistItemAsync", host.Calls);
        Assert.DoesNotContain("IRedisPublisherService.PublishWatchlistUpdateAsync", host.Calls);

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("DELETE", "/api/LiveData/watchlist/1", null, ServiceId));
        Assert.Contains("ILiveDataService.RemoveWatchlistItemAsync", host.Calls);
    }

    [Fact]
    public async Task The_recording_list_reads_stay_open_to_a_trader()
    {
        await using var host = await Host.StartAsync<LiveDataController>(LiveDataServices);

        foreach (var path in new[] { "/api/LiveData/watchlist", "/api/LiveData/watchlist/stale" })
        {
            AssertSucceeded(await host.SendAsync("GET", path, null, TraderId), path);
        }
    }

    [Fact]
    public async Task A_trader_cannot_add_an_equity_group_to_the_recording_list()
    {
        const string path = "/api/Equities/live/watchlist/group";
        const string body = """{"groupName":"NIFTY50","dataType":"symbolUpdate","onlyEnabledMembers":true}""";
        await using var host = await Host.StartAsync<EquitiesController>(s =>
        {
            s.AddFake<IEquityGroupService>();
            s.AddFake<IEquityLiveSnapshotService>();
            s.AddFake<ILiveDataService>();
            s.AddFake<IRedisPublisherService>();
            s.AddTransient<UpsertWatchlistItemUseCase>();
            s.AddTransient<AddEquityGroupToWatchlistUseCase>();
        });

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, body, TraderId));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", path, body, userId: null));
        Assert.Empty(host.Calls);

        // Past the policy the action looks the group up (the fake knows none, so it answers 400).
        foreach (var caller in new[] { ServiceId, AdminId })
        {
            host.Calls.Clear();
            AssertAuthorized(await host.SendAsync("POST", path, body, caller), path);
            Assert.Contains("IEquityGroupService.GetGroupByNameAsync", host.Calls);
        }
    }

    [Fact]
    public async Task A_trader_cannot_write_an_option_chain_snapshot()
    {
        const string path = "/api/OptionChain/snapshots";
        const string body = """{"rows":[{"underlying":"NIFTY","expiryDate":"2026-11-04","strikePrice":25000,"optionType":"CE","symbol":"NSE:NIFTY26N0425000CE","spotPrice":25010,"lastTradedPrice":1,"openInterest":999999999}]}""";
        await using var host = await Host.StartAsync<OptionChainController>(OptionChainServices($"chain-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, body, TraderId));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", path, body, userId: null));
        Assert.Equal(0, await host.CountChainRowsAsync());

        // The chain poller signs in as Service.
        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", path, body, ServiceId));
        Assert.Equal(1, await host.CountChainRowsAsync());
        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", path, body, AdminId));
    }

    [Fact]
    public async Task The_option_chain_view_stays_open_to_a_trader()
    {
        await using var host = await Host.StartAsync<OptionChainController>(OptionChainServices($"chain-{Guid.NewGuid():N}"));

        AssertAuthorized(await host.SendAsync("GET", "/api/OptionChain/expiries/recorded?underlying=NIFTY", null, TraderId), "expiries/recorded");
    }

    [Fact]
    public async Task Only_an_admin_can_sign_the_desk_out_of_its_broker()
    {
        const string path = "/api/Auth/logout";
        await using var host = await Host.StartAsync<AuthController>(AuthServices);

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, null, TraderId));
        // Nothing in the engine signs the desk out; the Service account has no business here either.
        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, null, ServiceId));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", path, null, userId: null));
        Assert.DoesNotContain("IBrokerSessionStore.ClearAsync", host.Calls);

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", path, null, AdminId));
        Assert.Contains("IBrokerSessionStore.ClearAsync", host.Calls);
    }

    [Theory]
    [InlineData("/api/Auth/url")]
    [InlineData("/api/Auth/start")]
    public async Task Only_an_admin_can_begin_the_desks_broker_sign_in(string path)
    {
        await using var host = await Host.StartAsync<AuthController>(AuthServices);

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("GET", path, null, TraderId));
        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("GET", path, null, ServiceId));
        Assert.Empty(host.Calls);

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("GET", path, null, AdminId));
        Assert.Contains("IProviderRouter.ResolveBrokerAsync", host.Calls);
    }

    [Fact]
    public async Task A_trader_cannot_spend_the_desks_broker_history_limits()
    {
        const string path = "/api/MarketData/history/sync";
        const string body = """{"symbol":"NSE:NIFTY50-INDEX","resolution":"5","fromDate":"2026-10-01","toDate":"2026-10-02"}""";
        await using var host = await Host.StartAsync<MarketDataController>(s =>
        {
            s.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase($"md-{Guid.NewGuid():N}"));
            s.AddMemoryCache();
            s.AddFake<IMarketDataService>();
            s.AddTransient<SyncHistoryUseCase>();
            s.AddTransient<GetStoredCandlesUseCase>();
        });

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, body, TraderId));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", path, body, userId: null));
        Assert.DoesNotContain("IMarketDataService.SyncHistoryAsync", host.Calls);

        // The backtest feed (core/api_client.py) signs in as Service.
        AssertSucceeded(await host.SendAsync("POST", path, body, ServiceId), path);
        Assert.Contains("IMarketDataService.SyncHistoryAsync", host.Calls);
        AssertSucceeded(await host.SendAsync("POST", path, body, AdminId), path);
    }

    // ------------------------------------------------------------------

    private static void AssertSucceeded(HttpStatusCode status, string path)
        => Assert.True((int)status is >= 200 and < 300, $"{path} answered {(int)status}");

    /// <summary>Past authentication and authorization: whatever the action itself answered.</summary>
    private static void AssertAuthorized(HttpStatusCode status, string path)
        => Assert.True(status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{path} answered {(int)status}");

    private static void LiveDataServices(IServiceCollection s)
    {
        s.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase($"live-{Guid.NewGuid():N}"));
        s.AddFake<ILiveDataService>();
        s.AddFake<IRedisPublisherService>();
        s.AddFake<IProcessSettingsStore>();
        foreach (var useCase in typeof(GetWatchlistUseCase).Assembly.GetTypes()
                     .Where(t => t.Namespace == typeof(GetWatchlistUseCase).Namespace && t.IsClass && !t.IsAbstract && t.Name.EndsWith("UseCase", StringComparison.Ordinal)))
        {
            s.AddTransient(useCase);
        }

        s.AddScoped<WatchlistPruneService>();
        s.AddSingleton<LiveFeedSubscriptions>();
        s.AddSingleton(sp => new LiveTickDispatcher(
            sp.GetRequiredService<LiveFeedSubscriptions>(), new RecordingHubContext(), Options.Create(new LiveFeedOptions()), NullLogger<LiveTickDispatcher>.Instance));
    }

    private static Action<IServiceCollection> OptionChainServices(string database) => s =>
    {
        s.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(database));
        s.AddScoped(sp => new OptionChainService(sp.GetRequiredService<TradingDbContext>()));
        // Only stored by the controller; no endpoint under test starts or reads the poller.
        s.AddSingleton(_ => (ChainPollerSupervisor)RuntimeHelpers.GetUninitializedObject(typeof(ChainPollerSupervisor)));
    };

    private static void AuthServices(IServiceCollection s)
    {
        s.AddFake<IBrokerSessionStore>();
        s.AddFake<IProviderRouter>();
        s.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase($"oauth-{Guid.NewGuid():N}"));
        s.AddFake<IProviderRegistry>();
        s.AddSingleton<BrokerOAuthStates>();
        s.AddScoped<BrokerOAuthFlow>();
    }

    /// <summary>
    /// One controller with the API's policies, over fakes that record every
    /// call into <see cref="Calls"/>. A caller is named by the X-Test-User header.
    /// </summary>
    internal sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private Host(WebApplication app, ConcurrentQueue<string> calls)
        {
            _app = app;
            _client = app.GetTestClient();
            Calls = calls;
        }

        public ConcurrentQueue<string> Calls { get; }

        public IServiceProvider Services => _app.Services;

        public static async Task<Host> StartAsync<TController>(Action<IServiceCollection> register)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(TController).Assembly)
                .ConfigureApplicationPartManager(parts =>
                {
                    foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                        parts.FeatureProviders.Remove(provider);
                    parts.FeatureProviders.Add(new Only<TController>());
                });
            builder.Services.AddAuthentication(HeaderAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, null);
            builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();

            var calls = new ConcurrentQueue<string>();
            builder.Services.AddSingleton(calls);
            register(builder.Services);

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return new Host(app, calls);
        }

        public async Task<HttpStatusCode> SendAsync(string method, string path, string? body, long? userId, CancellationToken cancellationToken = default)
        {
            using var response = await ExchangeAsync(method, path, body, userId, cancellationToken);
            return response.StatusCode;
        }

        /// <summary>The whole response, for a test that reads more than its status.</summary>
        public async Task<HttpResponseMessage> ExchangeAsync(string method, string path, string? body, long? userId, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (userId is { } id) request.Headers.Add(HeaderAuth.UserHeader, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return await _client.SendAsync(request, cancellationToken);
        }

        public async Task<int> CountChainRowsAsync()
        {
            using var scope = _app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<TradingDbContext>().OptionChainSnapshots.CountAsync();
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.DisposeAsync();
        }

        private sealed class Only<T> : ControllerFeatureProvider
        {
            protected override bool IsController(TypeInfo typeInfo) => typeInfo.AsType() == typeof(T);
        }
    }

    /// <summary>Signs a request in as X-Test-User, with the role its account holds: 1 Admin, 5 Service, anyone else Trader.</summary>
    private sealed class HeaderAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string UserHeader = "X-Test-User";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var value) || !long.TryParse(value, out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            string role = userId switch { AdminId => UserRoles.Admin, ServiceId => UserRoles.Service, _ => UserRoles.Trader };
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new(ClaimTypes.Name, $"user{userId}"),
                new(ClaimTypes.Role, role),
                new("sid", Guid.NewGuid().ToString()),
                new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
                new("exp", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString()),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}

/// <summary>
/// A stand-in for one of the platform's interfaces that does nothing and
/// records each call ("IType.Method") in the host's shared queue. A method
/// returning another platform interface answers with a fake of that one too,
/// so a resolved broker can be asked for its sign-in URL.
/// </summary>
public class RecordingFake<T> : DispatchProxy where T : class
{
    private ConcurrentQueue<string> _calls = new();

    public static T Create(ConcurrentQueue<string> calls)
    {
        var proxy = DispatchProxy.Create<T, RecordingFake<T>>();
        ((RecordingFake<T>)(object)proxy)._calls = calls;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        _calls.Enqueue($"{typeof(T).Name}.{targetMethod!.Name}");
        return Answer(targetMethod.ReturnType, _calls);
    }

    internal static object? Answer(Type type, ConcurrentQueue<string> calls)
    {
        if (type == typeof(ProviderDescriptor)) return AlgoTrading.Infrastructure.Providers.Fyers.FyersProvider.Descriptor;
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [Answer(inner, calls)]);
        }

        if (type.IsInterface && type.Namespace?.StartsWith("AlgoTrading", StringComparison.Ordinal) == true)
        {
            return typeof(RecordingFake<>).MakeGenericType(type).GetMethod(nameof(Create))!.Invoke(null, [calls]);
        }

        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>
/// An <see cref="IUserAdminService"/> that says yes to every module: a caller who
/// holds the grant, so a refusal can only be the check under test.
/// </summary>
public class EveryModuleGranted : DispatchProxy
{
    public static IUserAdminService Create() => DispatchProxy.Create<IUserAdminService, EveryModuleGranted>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod!.Name == nameof(IUserAdminService.IsModuleAllowedAsync)
            ? Task.FromResult(true)
            : RecordingFake<IUserAdminService>.Answer(targetMethod.ReturnType, new ConcurrentQueue<string>());
}

internal static class RecordingFakeRegistration
{
    /// <summary>Registers a <see cref="RecordingFake{T}"/> that records into the host's queue.</summary>
    public static IServiceCollection AddFake<T>(this IServiceCollection services) where T : class
        => services.AddSingleton(sp => RecordingFake<T>.Create(sp.GetRequiredService<ConcurrentQueue<string>>()));

    /// <summary>A type only stored by the controller under test, and never called by the endpoints it exercises.</summary>
    public static IServiceCollection AddUnused<T>(this IServiceCollection services) where T : class
        => services.AddSingleton(_ => (T)RuntimeHelpers.GetUninitializedObject(typeof(T)));
}
