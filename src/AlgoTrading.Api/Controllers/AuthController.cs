using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Application.UseCases.Auth;
using Microsoft.AspNetCore.Mvc;
using AlgoTrading.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;

namespace AlgoTrading.Api.Controllers;

    /// <summary>
    /// Exposes endpoints to trigger the OAuth login flow, handle callbacks, and check the current active broker session.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
    private readonly GenerateAccessTokenUseCase _generateAccessTokenUseCase;
    private readonly IBrokerSessionStore _brokerSessionStore;
    private readonly IProviderRouter _providerRouter;
    private readonly string _frontendBaseUrl;

    public AuthController(
        GenerateAccessTokenUseCase generateAccessTokenUseCase,
        IBrokerSessionStore brokerSessionStore,
        IProviderRouter providerRouter,
        IConfiguration configuration)
    {
        _generateAccessTokenUseCase = generateAccessTokenUseCase;
        _brokerSessionStore = brokerSessionStore;
        _providerRouter = providerRouter;
        _frontendBaseUrl = configuration["Frontend:BaseUrl"]?.TrimEnd('/')
                           ?? "http://localhost:5173";
    }

    /// <summary>
    /// The broker's hosted-login URL for the web console. The frontend sends the
    /// browser here; the broker redirects back to our callback, which saves the
    /// token and returns the browser to the console.
    /// </summary>
    [HttpGet("url")]
    public async Task<IActionResult> GetAuthUrl(CancellationToken cancellationToken)
    {
        try
        {
            var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);
            return Ok(new { authUrl = await broker.GetAuthUrlAsync("webui", cancellationToken) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// The saved broker app credentials (secret never returned). Lets each
    /// installation configure its own broker app from the console instead of
    /// editing configuration files.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("broker-config")]
    public async Task<IActionResult> GetBrokerConfig(
        [FromServices] IBrokerCredentialsProvider credentialsProvider,
        CancellationToken cancellationToken)
    {
        var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);
        var creds = await credentialsProvider.GetAsync(
            broker.Descriptor.Key,
            cancellationToken: cancellationToken);

        return Ok(new
        {
            broker = broker.Descriptor.DisplayName,
            providerKey = broker.Descriptor.Key,
            clientId = creds.ClientId,
            redirectUri = creds.RedirectUri,
            hasSecret = !string.IsNullOrWhiteSpace(creds.SecretKey),
            source = creds.Source,
            updatedBy = creds.UpdatedBy,
            updatedUtc = creds.UpdatedUtc,
            suggestedRedirectUri = $"{Request.Scheme}://{Request.Host}/api/Auth/callback",
        });
    }

    public record SaveBrokerConfigRequest(string ClientId, string SecretKey, string RedirectUri);

    /// <summary>Saves the broker app credentials for this installation.</summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPut("broker-config")]
    public async Task<IActionResult> SaveBrokerConfig(
        [FromBody] SaveBrokerConfigRequest request,
        [FromServices] IBrokerCredentialsProvider credentialsProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.SecretKey) ||
            string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            return BadRequest(new { message = "clientId, secretKey and redirectUri are all required." });
        }

        if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out _))
        {
            return BadRequest(new { message = "redirectUri must be an absolute URL." });
        }

        var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);

        await credentialsProvider.SaveAsync(
            broker.Descriptor.Key,
            request.ClientId,
            request.SecretKey,
            request.RedirectUri,
            User.Identity?.Name ?? "admin",
            cancellationToken: cancellationToken);

        return Ok(new { message = $"{broker.Descriptor.DisplayName} app credentials saved. You can connect now." });
    }

    /// <summary>
    /// True when the caller is a browser navigation (FYERS redirect) rather
    /// than an API client — used to choose redirect vs JSON on the callback.
    /// </summary>
    private bool IsBrowserNavigation()
        => Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private IActionResult FrontendRedirect(bool connected, string? reason = null, string? providerKey = null)
    {
        // Back to the connector's own page when we know which one it was, so the
        // operator lands where they pressed Connect.
        string path = string.IsNullOrWhiteSpace(providerKey)
            ? "/admin/broker"
            : $"/admin/broker/{providerKey}";

        string url = $"{_frontendBaseUrl}{path}?connected={(connected ? 1 : 0)}";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            url += $"&reason={Uri.EscapeDataString(reason)}";
        }
        return Redirect(url);
    }

    /// <summary>
    /// Sends the caller's browser straight to the broker's hosted login. The
    /// previous implementation asked the vendor SDK to open a browser on the
    /// <em>server</em>, which does nothing for an operator sitting at the console.
    /// </summary>
    [HttpGet("start")]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        try
        {
            var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);
            string authUrl = await broker.GetAuthUrlAsync("start", cancellationToken);

            return IsBrowserNavigation()
                ? Redirect(authUrl)
                : Ok(new { authUrl, message = "Open this URL to complete the broker login." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery(Name = "auth_code")] string? authCode,
        [FromQuery] string? state,
        [FromQuery(Name = "s")] string? status,
        [FromQuery] int? code,
        CancellationToken cancellationToken)
    {
        var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);
        string brokerName = broker.Descriptor.DisplayName;

        if (string.IsNullOrWhiteSpace(authCode))
        {
            string reason = $"{brokerName} redirected back without an auth_code (s={status}, code={code}).";
            return IsBrowserNavigation()
                ? FrontendRedirect(connected: false, reason, broker.Descriptor.Key)
                : BadRequest(new { message = reason, state, status, code });
        }

        BrokerTokenResult tokenResult;
        try
        {
            tokenResult = await _generateAccessTokenUseCase.ExecuteAsync(
                authCode,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            return IsBrowserNavigation()
                ? FrontendRedirect(connected: false, $"Token exchange failed: {ex.Message}", broker.Descriptor.Key)
                : StatusCode(502, new { message = $"Token exchange failed: {ex.Message}" });
        }

        if (!tokenResult.Succeeded)
        {
            string reason = tokenResult.ErrorMessage ?? $"{brokerName} returned no access token.";
            return IsBrowserNavigation()
                ? FrontendRedirect(connected: false, reason, broker.Descriptor.Key)
                : StatusCode(502, new { message = reason });
        }

        string accessToken = tokenResult.AccessToken;

        var session = new BrokerSession
        {
            BrokerName = brokerName,
            ProviderKey = broker.Descriptor.Key,
            AccessToken = accessToken,
            RefreshToken = tokenResult.RefreshToken,
            CreatedUtc = DateTime.UtcNow
        };

        await _brokerSessionStore.SaveAsync(session, cancellationToken);

        // Never print the token itself — a masked confirmation is enough.
        Console.WriteLine(
            $"{brokerName} token saved ({accessToken[..Math.Min(6, accessToken.Length)]}… , {accessToken.Length} chars).");

        return IsBrowserNavigation()
            ? FrontendRedirect(connected: true, null, broker.Descriptor.Key)
            : Ok(new { message = "Access token generated and saved.", isAuthenticated = session.IsAuthenticated, state, status, code });
    }

    [HttpGet("session")]
    public async Task<IActionResult> GetSession(CancellationToken cancellationToken)
    {
        var session = await _brokerSessionStore.GetCurrentAsync(cancellationToken);

        if (session is null)
        {
            var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);

            return Ok(new
            {
                broker = broker.Descriptor.DisplayName,
                isAuthenticated = false,
                accessToken = string.Empty,
                refreshToken = string.Empty
            });
        }

        return Ok(new
        {
            broker = session.BrokerName,
            isAuthenticated = session.IsAuthenticated,
            createdUtc = session.CreatedUtc,
            updatedUtc = session.UpdatedUtc,
            accessToken = session.AccessToken,
            refreshToken = session.RefreshToken
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        await _brokerSessionStore.ClearAsync(cancellationToken: cancellationToken);

        return Ok(new
        {
            message = "Broker session cleared successfully."
        });
    }
}