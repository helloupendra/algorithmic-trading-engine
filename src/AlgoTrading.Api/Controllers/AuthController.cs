using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Application.UseCases.Auth;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
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
    private readonly string? _frontendBaseUrl;

    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public AuthController(
        GenerateAccessTokenUseCase generateAccessTokenUseCase,
        IBrokerSessionStore brokerSessionStore,
        IProviderRouter providerRouter,
        IConfiguration configuration,
        Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _generateAccessTokenUseCase = generateAccessTokenUseCase;
        _brokerSessionStore = brokerSessionStore;
        _providerRouter = providerRouter;
        _cache = cache;
        // An explicit override only. Unset, the redirect goes back to the
        // origin the request came in on (see FrontendRedirect): the API serves
        // the console itself, so that is always a page that exists — on the
        // domain when the broker callback arrives through the tunnel, on
        // localhost in development. A fixed localhost:5173 default sent a
        // phone signing in at 06:00 to a dev server that was not there.
        string? configured = configuration["Frontend:BaseUrl"]?.Trim().TrimEnd('/');
        _frontendBaseUrl = string.IsNullOrEmpty(configured) ? null : configured;
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
            return Ok(new { authUrl = await broker.GetAuthUrlAsync("webui", cancellationToken: cancellationToken) });
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
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
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
            // Whether it is set, never the value.
            hasTradingPin = !string.IsNullOrWhiteSpace(creds.TradingPin),
            source = creds.Source,
            updatedBy = creds.UpdatedBy,
            updatedUtc = creds.UpdatedUtc,
            suggestedRedirectUri = $"{Request.Scheme}://{Request.Host}/api/Auth/callback",
        });
    }

    /// <param name="TradingPin">
    /// Optional. Needed only to refresh an expired token without a person at
    /// the keyboard; omit it and nothing stored changes, send "" to clear it.
    /// </param>
    public record SaveBrokerConfigRequest(string ClientId, string SecretKey, string RedirectUri, string? TradingPin = null);

    /// <summary>
    /// Renews the stored access token from its refresh token.
    /// </summary>
    /// <remarks>
    /// FYERS tokens expire daily, so without this the first thing every trading
    /// morning needs is a person signing in — which makes an unattended start
    /// impossible. Safe to call when the token is still good: the broker simply
    /// issues another one.
    /// </remarks>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken(CancellationToken cancellationToken = default)
    {
        var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);
        var session = await _brokerSessionStore.GetForProviderAsync(broker.Descriptor.Key, cancellationToken);

        if (session is null || string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            return BadRequest(new
            {
                connected = false,
                message = $"No {broker.Descriptor.Key} session to refresh. Sign in once so a refresh token is stored.",
            });
        }

        var result = await broker.RefreshAccessTokenAsync(session.RefreshToken, cancellationToken);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            // A refresh token has its own, longer expiry. When it is gone the
            // only honest answer is "sign in again", and the caller — often an
            // unattended script — needs to be able to say so.
            return StatusCode(502, new { connected = false, message = result.ErrorMessage ?? "The broker declined the refresh." });
        }

        await _brokerSessionStore.SaveAsync(new BrokerSession
        {
            BrokerName = session.BrokerName,
            ProviderKey = broker.Descriptor.Key,
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            CreatedUtc = DateTime.UtcNow,
        }, cancellationToken);

        HttpContext.Describe($"Refreshed the {broker.Descriptor.Key} access token.", "broker", broker.Descriptor.Key);

        return Ok(new { connected = true, message = $"{broker.Descriptor.Key} access token renewed." });
    }

    /// <summary>Saves the broker app credentials for this installation.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPut("broker-config")]
    public async Task<IActionResult> SaveBrokerConfig(
        [FromBody] SaveBrokerConfigRequest request,
        [FromServices] IBrokerCredentialsProvider credentialsProvider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientId) || string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            return BadRequest(new { message = "clientId and redirectUri are required." });
        }

        if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out _))
        {
            return BadRequest(new { message = "redirectUri must be an absolute URL." });
        }

        var broker = await _providerRouter.ResolveBrokerAsync(cancellationToken: cancellationToken);

        // A blank secret keeps the one on file: moving the console to a new
        // domain changes only the redirect, and retyping the app secret for
        // that is a chance to mistype it. The same for the trading PIN.
        var secret = request.SecretKey?.Trim() ?? string.Empty;
        var pin = request.TradingPin;
        if (secret.Length == 0 || string.IsNullOrWhiteSpace(pin))
        {
            var existing = await credentialsProvider.GetAsync(broker.Descriptor.Key, cancellationToken: cancellationToken);
            if (existing.Source == "database" && string.Equals(existing.ClientId, request.ClientId.Trim(), StringComparison.Ordinal))
            {
                if (secret.Length == 0) secret = existing.SecretKey;
                if (string.IsNullOrWhiteSpace(pin)) pin = existing.TradingPin;
            }
        }
        if (secret.Length == 0)
        {
            return BadRequest(new { message = "secretKey is required the first time an app is saved, or when the app id changes." });
        }

        await credentialsProvider.SaveAsync(
            broker.Descriptor.Key,
            request.ClientId.Trim(),
            secret,
            request.RedirectUri.Trim(),
            User.Identity?.Name ?? "admin",
            pin,
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

        string origin = _frontendBaseUrl ?? $"{Request.Scheme}://{Request.Host}";
        string url = $"{origin}{path}?connected={(connected ? 1 : 0)}";
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
            string authUrl = await broker.GetAuthUrlAsync("start", cancellationToken: cancellationToken);

            return IsBrowserNavigation()
                ? Redirect(authUrl)
                : Ok(new { authUrl, message = "Open this URL to complete the broker login." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Anonymous by necessity: the broker's OAuth redirect lands here in the
    /// user's browser and carries no bearer token of ours. The `state` value
    /// is what ties the callback back to the request that started it.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery(Name = "auth_code")] string? authCode,
        [FromQuery] string? state,
        [FromQuery(Name = "s")] string? status,
        [FromQuery] int? code,
        CancellationToken cancellationToken)
    {
        // A trader linking their OWN broker arrives with a one-time state the
        // Account page minted; it names their broker account, and the token is
        // exchanged with that account's app and saved on that account's row.
        // Anything else is the platform's shared account, exactly as before.
        long? accountId = TraderBrokerController.ConsumeState(_cache, state);
        bool traderFlow = state?.StartsWith(TraderBrokerController.StatePrefix, StringComparison.Ordinal) == true;
        if (traderFlow && accountId is null)
        {
            const string stale = "This sign-in link has expired or was already used — open the Account page and press Sign in again.";
            return IsBrowserNavigation() ? TraderRedirect(false, stale) : BadRequest(new { message = stale });
        }

        IBrokerProvider broker;
        try
        {
            broker = await _providerRouter.ResolveBrokerAsync(accountId, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return IsBrowserNavigation() ? TraderRedirect(false, ex.Message) : BadRequest(new { message = ex.Message });
        }
        string brokerName = broker.Descriptor.DisplayName;

        IActionResult Failed(string reason, int statusCode = 502) => IsBrowserNavigation()
            ? (traderFlow ? TraderRedirect(false, reason) : FrontendRedirect(connected: false, reason, broker.Descriptor.Key))
            : StatusCode(statusCode, new { message = reason, state, status, code });

        if (string.IsNullOrWhiteSpace(authCode))
        {
            return Failed($"{brokerName} redirected back without an auth_code (s={status}, code={code}).", 400);
        }

        BrokerTokenResult tokenResult;
        try
        {
            tokenResult = await _generateAccessTokenUseCase.ExecuteAsync(authCode, accountId, cancellationToken);
        }
        catch (Exception ex)
        {
            return Failed($"Token exchange failed: {ex.Message}");
        }

        if (!tokenResult.Succeeded)
        {
            return Failed(tokenResult.ErrorMessage ?? $"{brokerName} returned no access token.");
        }

        string accessToken = tokenResult.AccessToken;

        var session = new BrokerSession
        {
            BrokerName = brokerName,
            ProviderKey = broker.Descriptor.Key,
            BrokerAccountId = accountId,
            AccessToken = accessToken,
            RefreshToken = tokenResult.RefreshToken,
            CreatedUtc = DateTime.UtcNow
        };

        await _brokerSessionStore.SaveAsync(session, cancellationToken);

        // Never print the token itself — a masked confirmation is enough.
        Console.WriteLine(
            $"{brokerName} token saved for {(accountId is null ? "the platform" : $"broker account {accountId}")} " +
            $"({accessToken[..Math.Min(6, accessToken.Length)]}… , {accessToken.Length} chars).");

        if (IsBrowserNavigation())
            return traderFlow ? TraderRedirect(true, null) : FrontendRedirect(connected: true, null, broker.Descriptor.Key);
        return Ok(new { message = "Access token generated and saved.", isAuthenticated = session.IsAuthenticated, state, status, code });
    }

    /// <summary>Back to the trader's Account page, which reads the outcome from the address.</summary>
    private IActionResult TraderRedirect(bool connected, string? reason)
    {
        string origin = _frontendBaseUrl ?? $"{Request.Scheme}://{Request.Host}";
        string url = $"{origin}/trader/account?broker={(connected ? 1 : 0)}";
        if (!string.IsNullOrWhiteSpace(reason)) url += $"&reason={Uri.EscapeDataString(reason)}";
        return Redirect(url);
    }

    /// <summary>
    /// The broker connection's state, and — only for those who need them — its
    /// tokens.
    /// </summary>
    /// <remarks>
    /// The tokens are the keys to the owner's real brokerage account: with them
    /// anyone can place orders on it directly, outside this platform entirely.
    /// They are encrypted at rest, and this endpoint used to hand them to any
    /// signed-in account, which undid that completely — a Trader could read them
    /// with one request.
    /// <para>
    /// The engine genuinely needs them: the ingestor opens the FYERS websocket
    /// with the access token and signs in as the Service account to get it. So
    /// the rule is by role, not by removing the field — Admin and Service see
    /// the tokens, everyone else sees whether the broker is connected, which is
    /// all any screen ever needed.
    /// </para>
    /// </remarks>
    [HttpGet("session")]
    public async Task<IActionResult> GetSession(CancellationToken cancellationToken)
    {
        bool mayReadTokens = User.IsInRole(UserRoles.Admin) || User.IsInRole(UserRoles.Service);

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

        if (!mayReadTokens)
        {
            return Ok(new
            {
                broker = session.BrokerName,
                isAuthenticated = session.IsAuthenticated,
                createdUtc = session.CreatedUtc,
                updatedUtc = session.UpdatedUtc,
                expiresAtUtc = session.ExpiresAtUtc,
                // Shaped the same way on purpose: a caller that only checks
                // whether the broker is linked keeps working unchanged.
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
            expiresAtUtc = session.ExpiresAtUtc,
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