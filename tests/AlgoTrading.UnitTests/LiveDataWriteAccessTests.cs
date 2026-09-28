using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The price and feed-health writes are for the feeds (the Service account)
/// and admins; a trader is refused before anything is stored or pushed.
/// </summary>
/// <remarks>
/// Until 28 Sep any signed-in trader could post a price for any contract,
/// stamped in 2030 so every real tick after it read as older and was
/// refused; every account's paper fills and risk marks were priced from that
/// row, and the dispatcher pushed it to every browser following the symbol.
/// These requests go through the API's own policies and the controller's own
/// attributes, in an in-process host.
/// </remarks>
public class LiveDataWriteAccessTests
{
    private const long AdminId = 1;
    private const long ServiceId = 5;
    private const long TraderId = 8;

    private const string Tick = """{"symbol":"NSE:NIFTY26SEP25000CE","lastTradedPrice":142.5,"exchangeTimestampUtc":"2030-01-01T00:00:00Z"}""";

    public static TheoryData<string, string> Writes => new()
    {
        { "/api/LiveData/latest/upsert", """{"symbol":"NSE:NIFTY26SEP25000CE","lastTradedPrice":142.5}""" },
        { "/api/LiveData/heartbeat", """{"sourceName":"python-dhan-feed","feedKey":"dhan","status":"Running"}""" },
        { "/api/LiveData/ticks/upsert", Tick },
        { "/api/LiveData/ticks/upsert-batch", $"[{Tick}]" },
    };

    [Theory]
    [MemberData(nameof(Writes))]
    public async Task A_trader_is_refused_and_the_feed_and_an_admin_are_let_through(string path, string body)
    {
        await using var host = await WriteHost.StartAsync();

        Assert.Equal(HttpStatusCode.Forbidden, await host.PostAsync(path, body, TraderId));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.PostAsync(path, body, userId: null));
        Assert.Equal(HttpStatusCode.OK, await host.PostAsync(path, body, ServiceId));
        Assert.Equal(HttpStatusCode.OK, await host.PostAsync(path, body, AdminId));
    }

    [Fact]
    public async Task A_refused_tick_reaches_no_browser()
    {
        await using var host = await WriteHost.StartAsync();
        host.Subscriptions.Connect("everything", AdminId, isAdmin: true);
        host.Subscriptions.SetAll("everything", true);

        Assert.Equal(HttpStatusCode.Forbidden, await host.PostAsync("/api/LiveData/ticks/upsert-batch", $"[{Tick}]", TraderId));
        Assert.Equal(HttpStatusCode.Forbidden, await host.PostAsync("/api/LiveData/ticks/upsert", Tick, TraderId));

        Assert.Equal(0, host.Dispatcher.Flush());
        Assert.Empty(host.Hub.All);

        // The feed's own post is pushed as before.
        Assert.Equal(HttpStatusCode.OK, await host.PostAsync("/api/LiveData/ticks/upsert-batch", $"[{Tick}]", ServiceId));
        Assert.Equal(1, host.Dispatcher.Flush());
    }

    [Fact]
    public async Task The_reads_stay_open_to_a_trader()
    {
        await using var host = await WriteHost.StartAsync();

        foreach (var path in new[] { "/api/LiveData/latest/all", "/api/LiveData/status/all", "/api/LiveData/watchlist" })
        {
            var status = await host.GetAsync(path, TraderId);
            Assert.True((int)status is >= 200 and < 300, $"{path} answered {(int)status} to a trader");
        }
    }

    /// <summary>
    /// LiveDataController alone, with the API's policies, over use cases whose
    /// store does nothing. A caller is named by the X-Test-User header.
    /// </summary>
    private sealed class WriteHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private WriteHost(WebApplication app, RecordingHubContext hub)
        {
            _app = app;
            _client = app.GetTestClient();
            Hub = hub;
        }

        public RecordingHubContext Hub { get; }

        public LiveFeedSubscriptions Subscriptions => _app.Services.GetRequiredService<LiveFeedSubscriptions>();

        public LiveTickDispatcher Dispatcher => _app.Services.GetRequiredService<LiveTickDispatcher>();

        public static async Task<WriteHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddControllers()
                .AddApplicationPart(typeof(LiveDataController).Assembly)
                .ConfigureApplicationPartManager(parts =>
                {
                    foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                        parts.FeatureProviders.Remove(provider);
                    parts.FeatureProviders.Add(new Only<LiveDataController>());
                });
            builder.Services.AddAuthentication(HeaderAuth.SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuth>(HeaderAuth.SchemeName, null);
            builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();

            builder.Services.AddSingleton(RecapClockTests.Inert<ILiveDataService>.Create());
            builder.Services.AddSingleton(RecapClockTests.Inert<IRedisPublisherService>.Create());
            builder.Services.AddSingleton(RecapClockTests.Inert<IProcessSettingsStore>.Create());
            foreach (var useCase in typeof(GetWatchlistUseCase).Assembly.GetTypes()
                         .Where(t => t.Namespace == typeof(GetWatchlistUseCase).Namespace && t.IsClass && !t.IsAbstract && t.Name.EndsWith("UseCase", StringComparison.Ordinal)))
            {
                builder.Services.AddTransient(useCase);
            }

            var hub = new RecordingHubContext();
            builder.Services.AddSingleton<LiveFeedSubscriptions>();
            builder.Services.AddSingleton(sp => new LiveTickDispatcher(
                sp.GetRequiredService<LiveFeedSubscriptions>(), hub, Options.Create(new LiveFeedOptions()), NullLogger<LiveTickDispatcher>.Instance));

            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapControllers();
            await app.StartAsync();
            return new WriteHost(app, hub);
        }

        public Task<HttpStatusCode> GetAsync(string path, long? userId) => SendAsync(HttpMethod.Get, path, null, userId);

        public Task<HttpStatusCode> PostAsync(string path, string body, long? userId) => SendAsync(HttpMethod.Post, path, body, userId);

        private async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, string? body, long? userId)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (userId is { } id) request.Headers.Add(HeaderAuth.UserHeader, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await _client.SendAsync(request);
            return response.StatusCode;
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
                new(ClaimTypes.Role, role),
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
