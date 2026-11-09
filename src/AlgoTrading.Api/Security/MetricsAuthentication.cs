using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Prometheus;

namespace AlgoTrading.Api.Security;

/// <summary>A dedicated scrape credential that cannot authenticate a console or trading request.</summary>
public static class MetricsAuthentication
{
    public const string Scheme = "MetricsScrape";
    public const string Path = "/metrics";
    public const string ScrapeClaim = "metrics:scrape";

    public static IEndpointConventionBuilder MapScrapeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMetrics(Path).RequireAuthorization(AuthorizationPolicies.MetricsScrape);

    public static AuthenticationBuilder AddMetricsScraping(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        builder.Services.Configure<MetricsAuthenticationOptions>(Scheme, configuration.GetSection("Metrics"));
        return builder.AddScheme<MetricsAuthenticationOptions, MetricsAuthenticationHandler>(Scheme, _ => { });
    }
}

public sealed class MetricsAuthenticationOptions : AuthenticationSchemeOptions
{
    public string ScrapeToken { get; set; } = string.Empty;
}

public sealed class MetricsAuthenticationHandler(
    IOptionsMonitor<MetricsAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<MetricsAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        Response.Headers.CacheControl = "no-store";

        // No configured credential means no access, including from localhost
        // and trusted proxies. An address or forwarded header is not a token.
        if (string.IsNullOrWhiteSpace(Options.ScrapeToken) || Options.ScrapeToken.Length < 32 || Options.ScrapeToken.Any(char.IsWhiteSpace))
            return Task.FromResult(AuthenticateResult.Fail("Metrics scraping is not configured."));

        if (Request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(Request.Headers.Authorization[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
            return Task.FromResult(AuthenticateResult.NoResult());

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(Options.ScrapeToken));
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter));
        if (!CryptographicOperations.FixedTimeEquals(expected, supplied))
            return Task.FromResult(AuthenticateResult.Fail("The scrape credential was not accepted."));

        var identity = new ClaimsIdentity([new Claim(MetricsAuthentication.ScrapeClaim, "true")], MetricsAuthentication.Scheme);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), MetricsAuthentication.Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.WWWAuthenticate = "Bearer realm=\"metrics\"";
        return Task.CompletedTask;
    }
}
