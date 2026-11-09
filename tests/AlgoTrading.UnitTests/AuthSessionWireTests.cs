using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Auth;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AlgoTrading.UnitTests;

/// <summary>Actual JWT middleware, controllers and SignalR sockets against persisted login families.</summary>
public sealed class AuthSessionWireTests
{
    [Fact]
    public async Task Logout_rejects_the_access_token_and_closes_only_its_sessions_socket()
    {
        await using var host = await Host.Start();
        var first = await host.Login();
        var second = await host.Login();
        await using var socket = await host.Connect(first);
        await using var other = await host.Connect(second);
        Assert.Equal(HttpStatusCode.OK, await host.Probe(first.AccessToken));
        using var response = await host.Post("logout", new LogoutRequest { RefreshToken = first.RefreshToken }, first.AccessToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.Probe(first.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await host.Probe(second.AccessToken));
        Assert.True(await socket.ClosedWithinAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(host.Subscriptions.Snapshot());
        Assert.Equal(Sid(second), host.Subscriptions.Snapshot()[0].SessionId);
        using var refresh = await host.Post("refresh", new RefreshTokenRequest { RefreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task A_refresh_credential_can_end_its_family_after_the_access_token_expires()
    {
        await using var host = await Host.Start();
        var login = await host.Login();
        string expired = host.Token(Sid(login), expires: DateTime.UtcNow.AddMinutes(-10));
        using var response = await host.Post("logout", new LogoutRequest { RefreshToken = login.RefreshToken }, expired);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await host.Probe(login.AccessToken));
        // Repeating the revocation remains harmless.
        using var repeat = await host.Post("logout", new LogoutRequest { RefreshToken = login.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
    }

    [Fact]
    public async Task Missing_or_wrong_session_claims_are_rejected_even_with_a_valid_signature()
    {
        await using var host = await Host.Start();
        var login = await host.Login();
        Assert.Equal(HttpStatusCode.Unauthorized, await host.Probe(host.Token(null)));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.Probe(host.Token(Guid.NewGuid())));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.Probe(host.Token(Sid(login), userId: 999)));
        Assert.Equal(HttpStatusCode.OK, await host.Probe(login.AccessToken));
    }

    [Fact]
    public async Task A_duplicate_refresh_is_a_conflict_then_reuse_ends_only_that_family()
    {
        await using var host = await Host.Start();
        var first = await host.Login();
        var second = await host.Login();
        using var rotated = await host.Post("refresh", new RefreshTokenRequest { RefreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var child = (await rotated.Content.ReadFromJsonAsync<AuthResponse>())!;
        using var duplicate = await host.Post("refresh", new RefreshTokenRequest { RefreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Contains("refresh_already_used", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, await host.Probe(child.AccessToken));
        host.Time.Now += AuthService.RefreshRaceGrace;
        using var reused = await host.Post("refresh", new RefreshTokenRequest { RefreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Contains("refresh_reused", await reused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await host.Probe(child.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await host.Probe(second.AccessToken));
    }

    [Fact]
    public async Task Revocation_by_another_process_is_seen_on_HTTP_and_the_next_socket_check()
    {
        await using var host = await Host.Start();
        var first = await host.Login();
        var second = await host.Login();
        await using var socket = await host.Connect(first);
        await using var other = await host.Connect(second);
        using (var scope = host.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            (await db.UserSessions.SingleAsync(x => x.Id == Sid(first))).RevokedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await host.Probe(first.AccessToken));
        await host.App.Services.GetRequiredService<LiveSessionMonitor>().CheckAsync(default);
        Assert.True(await socket.ClosedWithinAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(host.Subscriptions.Snapshot());
        Assert.Equal(HttpStatusCode.OK, await host.Probe(second.AccessToken));
    }

    [Fact]
    public async Task An_authenticated_caller_cannot_log_out_a_different_login_using_its_refresh_token()
    {
        await using var host = await Host.Start();
        var first = await host.Login();
        var second = await host.Login();
        using var response = await host.Post("logout", new LogoutRequest { RefreshToken = second.RefreshToken }, first.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await host.Probe(first.AccessToken));
        Assert.Equal(HttpStatusCode.OK, await host.Probe(second.AccessToken));
    }

    private static Guid Sid(AuthResponse response) => Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken).Claims.Single(c => c.Type == "sid").Value);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Host(WebApplication app, Clock time, JwtOptions jwt) : IAsyncDisposable
    {
        public WebApplication App => app;
        public Clock Time => time;
        public LiveFeedSubscriptions Subscriptions => app.Services.GetRequiredService<LiveFeedSubscriptions>();
        private readonly HttpClient _client = app.GetTestClient();
        public static async Task<Host> Start()
        {
            var time = new Clock();
            var jwt = new JwtOptions { Issuer = "wire-test", Audience = "wire-test", SecretKey = new string('k', 64) };
            string database = $"auth-wire-{Guid.NewGuid()}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(database));
            builder.Services.AddMemoryCache();
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddSingleton(Options.Create(jwt));
            builder.Services.AddSingleton<PasswordHasher<AppUser>>();
            builder.Services.AddScoped<IAuthService, AuthService>();
            builder.Services.AddScoped<ITokenValidityService, TokenValidityService>();
            builder.Services.AddScoped<IUserAdminService, UserAdminService>();
            builder.Services.AddSingleton<BrokerOAuthStates>();
            builder.Services.AddLiveFeedHub(builder.Configuration);
            builder.Services.AddSingleton<LiveSessionMonitor>();
            builder.Services.AddControllers().AddApplicationPart(typeof(UserAuthController).Assembly);
            builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();
            builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter(RateLimitPolicies.SignIn, l => { l.PermitLimit = 1000; l.Window = TimeSpan.FromMinutes(1); }));
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            {
                o.TokenValidationParameters = new()
                {
                    ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)), ClockSkew = TimeSpan.Zero,
                };
                o.Events = new()
                {
                    OnTokenValidated = SessionAuthentication.ValidateAsync,
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Path.StartsWithSegments("/hubs")) context.Token = context.Request.Query["access_token"];
                        return Task.CompletedTask;
                    },
                };
            });
            var app = builder.Build();
            app.UseRouting(); app.UseAuthentication(); app.UseRateLimiter(); app.UseAuthorization();
            app.MapControllers(); app.MapLiveFeedHub();
            app.MapGet("/probe", () => "ok").RequireAuthorization();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                var user = new AppUser { Id = 7, UserName = "trader", Email = "trader@example.invalid" };
                user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, "session-test-password");
                db.AppUsers.Add(user); await db.SaveChangesAsync();
            }
            await app.StartAsync();
            return new(app, time, jwt);
        }
        public async Task<AuthResponse> Login()
        {
            using var response = await Post("login", new LoginRequest { UserNameOrEmail = "trader", Password = "session-test-password" });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        }
        public async Task<HttpResponseMessage> Post(string action, object body, string? token = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/UserAuth/{action}") { Content = JsonContent.Create(body) };
            if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await _client.SendAsync(request);
        }
        public async Task<HttpStatusCode> Probe(string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await _client.SendAsync(request);
            return response.StatusCode;
        }
        public async Task<LiveFeedHubWireTests.HubWire> Connect(AuthResponse login)
        {
            var client = app.GetTestServer().CreateWebSocketClient();
            var socket = await client.ConnectAsync(new Uri($"ws://localhost/hubs/livefeed?v=2&access_token={login.AccessToken}"), default);
            var wire = new LiveFeedHubWireTests.HubWire(socket);
            await wire.HandshakeAsync();
            // A completed invocation also ensures OnConnectedAsync finished registering the session.
            await wire.InvokeAsync("Subscribe", new object[] { new[] { "TEST" } });
            return wire;
        }
        public string Token(Guid? sid, long userId = 7, DateTime? expires = null)
        {
            var claims = new List<Claim> { new("sub", userId.ToString()), new("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64) };
            if (sid is Guid id) claims.Add(new("sid", id.ToString()));
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims,
                expires: expires ?? DateTime.UtcNow.AddMinutes(5), signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)), SecurityAlgorithms.HmacSha256)));
        }
        public async ValueTask DisposeAsync() { _client.Dispose(); await app.DisposeAsync(); }
    }
}
