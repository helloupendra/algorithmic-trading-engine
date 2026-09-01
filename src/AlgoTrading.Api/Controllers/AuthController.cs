using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Auth;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
    private readonly StartBrokerAuthUseCase _startBrokerAuthUseCase;
    private readonly GenerateAccessTokenUseCase _generateAccessTokenUseCase;
    private readonly IBrokerSessionStore _brokerSessionStore;
    private readonly IBrokerAuthService _brokerAuthService;
    private readonly string _frontendBaseUrl;

    public AuthController(
        StartBrokerAuthUseCase startBrokerAuthUseCase,
        GenerateAccessTokenUseCase generateAccessTokenUseCase,
        IBrokerSessionStore brokerSessionStore,
        IBrokerAuthService brokerAuthService,
        IConfiguration configuration)
    {
        _startBrokerAuthUseCase = startBrokerAuthUseCase;
        _generateAccessTokenUseCase = generateAccessTokenUseCase;
        _brokerSessionStore = brokerSessionStore;
        _brokerAuthService = brokerAuthService;
        _frontendBaseUrl = configuration["Frontend:BaseUrl"]?.TrimEnd('/')
                           ?? "http://localhost:5173";
    }

    /// <summary>
    /// The FYERS hosted-login URL for the web console. The frontend sends the
    /// browser here; FYERS redirects back to our callback, which saves the
    /// token and returns the browser to the console.
    /// </summary>
    [HttpGet("url")]
    public async Task<IActionResult> GetAuthUrl(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(new { authUrl = await _brokerAuthService.GetAuthUrlAsync("webui", cancellationToken) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// The saved FYERS app credentials (secret never returned). Lets each
    /// installation configure its own broker app from the console instead of
    /// editing configuration files.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("broker-config")]
    public async Task<IActionResult> GetBrokerConfig(
        [FromServices] IBrokerCredentialsProvider credentialsProvider,
        CancellationToken cancellationToken)
    {
        var creds = await credentialsProvider.GetFyersAsync(cancellationToken);

        return Ok(new
        {
            broker = "FYERS",
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

    /// <summary>Saves the FYERS app credentials for this installation.</summary>
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

        await credentialsProvider.SaveFyersAsync(
            request.ClientId,
            request.SecretKey,
            request.RedirectUri,
            User.Identity?.Name ?? "admin",
            cancellationToken);

        return Ok(new { message = "FYERS app credentials saved. You can connect now." });
    }

    /// <summary>
    /// True when the caller is a browser navigation (FYERS redirect) rather
    /// than an API client — used to choose redirect vs JSON on the callback.
    /// </summary>
    private bool IsBrowserNavigation()
        => Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private IActionResult FrontendRedirect(bool connected, string? reason = null)
    {
        string url = $"{_frontendBaseUrl}/admin/broker?connected={(connected ? 1 : 0)}";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            url += $"&reason={Uri.EscapeDataString(reason)}";
        }
        return Redirect(url);
    }

    [HttpGet("start")]
    public async Task<IActionResult> Start(CancellationToken cancellationToken)
    {
        await _startBrokerAuthUseCase.ExecuteAsync(cancellationToken);

        return Ok(new
        {
            message = "FYERS auth flow started. Complete login in browser. Access token response will be printed in terminal after callback."
        });
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
        if (string.IsNullOrWhiteSpace(authCode))
        {
            string reason = $"FYERS redirected back without an auth_code (s={status}, code={code}).";
            return IsBrowserNavigation()
                ? FrontendRedirect(connected: false, reason)
                : BadRequest(new { message = reason, state, status, code });
        }

        JObject tokenResponse;
        try
        {
            tokenResponse = await _generateAccessTokenUseCase.ExecuteAsync(authCode, cancellationToken);
        }
        catch (Exception ex)
        {
            return IsBrowserNavigation()
                ? FrontendRedirect(connected: false, $"Token exchange failed: {ex.Message}")
                : StatusCode(502, new { message = $"Token exchange failed: {ex.Message}" });
        }

        string accessToken = tokenResponse["TOKEN"]?.ToString() ?? string.Empty;
        string refreshToken = tokenResponse["refresh_token"]?.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            string reason = tokenResponse["message"]?.ToString() ?? "FYERS returned no access token.";
            return IsBrowserNavigation()
                ? FrontendRedirect(connected: false, reason)
                : StatusCode(502, new { message = reason });
        }

        var session = new BrokerSession
        { 
            BrokerName = "FYERS",
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            CreatedUtc = DateTime.UtcNow
        };

        await _brokerSessionStore.SaveAsync(session, cancellationToken);

        // Never print the token itself — a masked confirmation is enough.
        Console.WriteLine(
            $"FYERS token saved ({accessToken[..Math.Min(6, accessToken.Length)]}… , {accessToken.Length} chars).");

        return IsBrowserNavigation()
            ? FrontendRedirect(connected: true)
            : Ok(new { message = "Access token generated and saved.", isAuthenticated = session.IsAuthenticated, state, status, code });
    }

    [HttpGet("session")]
    public async Task<IActionResult> GetSession(CancellationToken cancellationToken)
    {
        var session = await _brokerSessionStore.GetCurrentAsync(cancellationToken);

        if (session is null)
        {
            return Ok(new
            {
                broker = "FYERS",
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
        await _brokerSessionStore.ClearAsync(cancellationToken);

        return Ok(new
        {
            message = "Broker session cleared successfully."
        });
    }
}