using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AlgoTrading.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Prometheus;

namespace AlgoTrading.UnitTests;

public sealed class MetricsAuthenticationTests
{
    private static readonly string Token = "metrics_" + new string('m', 43);
    private static readonly SymmetricSecurityKey JwtKey = new(Encoding.UTF8.GetBytes(new string('j', 64)));
    private static readonly Counter Probe = Metrics.CreateCounter("metrics_auth_test_probe_total", "Scrape authorization test probe.");

    [Fact]
    public async Task The_scraper_receives_real_metrics_with_its_own_token()
    {
        await using var host = await Host.Start(Token);
        using var response = await host.Send("/metrics", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("metrics_auth_test_probe_total", await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("/metrics")]
    [InlineData("/metrics/")]
    [InlineData("/METRICS")]
    public async Task Anonymous_requests_are_refused_even_from_loopback(string path)
    {
        await using var host = await Host.Start(Token);
        using var response = await host.Send(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.DoesNotContain("metrics_auth_test_probe_total", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("                                ")]
    [InlineData("12345678901234567890123456789012 ")]
    public async Task An_unconfigured_or_invalid_credential_never_opens_metrics(string? configured)
    {
        await using var host = await Host.Start(configured);
        using var response = await host.Send("/metrics", configured ?? Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_token_is_refused()
    {
        await using var host = await Host.Start(Token);
        using var response = await host.Send("/metrics", Token.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Trader")]
    [InlineData("Service")]
    public async Task A_console_token_cannot_replace_the_scrape_credential(string role)
    {
        await using var host = await Host.Start(Token);
        var jwt = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            claims: [new Claim(ClaimTypes.Role, role), new Claim(MetricsAuthentication.ScrapeClaim, "true")],
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(JwtKey, SecurityAlgorithms.HmacSha256)));

        using var control = await host.Send("/api/private", jwt);
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        using var metrics = await host.Send("/metrics", jwt);
        Assert.Equal(HttpStatusCode.Unauthorized, metrics.StatusCode);
    }

    [Fact]
    public async Task The_scrape_token_cannot_authenticate_an_application_request()
    {
        await using var host = await Host.Start(Token);
        using var response = await host.Send("/api/private", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_strings_and_proxy_headers_do_not_grant_access()
    {
        await using var host = await Host.Start(Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics?access_token=" + Token);
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("CF-Connecting-IP", "127.0.0.1");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Multiple_authorization_values_are_refused()
    {
        await using var host = await Host.Start(Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        request.Headers.TryAddWithoutValidation("Authorization", ["Bearer " + Token, "Bearer another-token"]);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reloading_the_credential_revokes_the_previous_one()
    {
        await using var host = await Host.Start(Token);
        using var before = await host.Send("/metrics", Token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        var replacement = "metrics_" + new string('r', 43);
        host.Configuration["Metrics:ScrapeToken"] = replacement;
        ((IConfigurationRoot)host.Configuration).Reload();

        using var old = await host.Send("/metrics", Token);
        using var current = await host.Send("/metrics", replacement);
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    private sealed class Host(WebApplication app, IConfiguration configuration) : IAsyncDisposable
    {
        public HttpClient Client { get; } = app.GetTestClient();
        public IConfiguration Configuration { get; } = configuration;

        public static async Task<Host> Start(string? token)
        {
            Probe.Inc();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Metrics:ScrapeToken"] = token });
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(o =>
                {
                    o.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false, ValidateAudience = false,
                        ValidateIssuerSigningKey = true, IssuerSigningKey = JwtKey
                    };
                    o.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = c =>
                        {
                            if (c.Request.Path.StartsWithSegments(MetricsAuthentication.Path, StringComparison.OrdinalIgnoreCase)) c.NoResult();
                            return Task.CompletedTask;
                        }
                    };
                })
                .AddMetricsScraping(builder.Configuration);
            builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();

            var app = builder.Build();
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return next();
            });
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapScrapeEndpoint();
            app.MapGet("/api/private", () => "private");
            await app.StartAsync();
            return new Host(app, builder.Configuration);
        }

        public Task<HttpResponseMessage> Send(string path, string? token = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (token is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}
