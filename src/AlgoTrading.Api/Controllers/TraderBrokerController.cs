using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.Trader;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// A trader's own broker: save their FYERS app, sign in to it, and read their
/// funds, holdings, positions and orders with their own token.
/// </summary>
/// <remarks>
/// Everything here is scoped to the signed-in user's <see cref="BrokerAccount"/>.
/// The platform's shared account (the feed, the strategies) lives in
/// <see cref="AuthController"/> and is never touched from here; the session
/// store keeps the two apart by <c>BrokerAccountId</c>.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/Trader/broker")]
public class TraderBrokerController : ControllerBase
{
    public const string ProviderKey = "fyers";
    public const string StatePrefix = "acct-";
    private const string StateCachePrefix = "trader-broker-state:";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    private readonly TradingDbContext _db;
    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IBrokerSessionStore _sessions;
    private readonly IProviderRouter _router;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<TraderBrokerController> _logger;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;

    public TraderBrokerController(
        TradingDbContext db,
        IBrokerCredentialsProvider credentials,
        IBrokerSessionStore sessions,
        IProviderRouter router,
        IMemoryCache cache,
        IHttpClientFactory http,
        ILogger<TraderBrokerController> logger,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        _configuration = configuration;
        _db = db;
        _credentials = credentials;
        _sessions = sessions;
        _router = router;
        _cache = cache;
        _http = http;
        _logger = logger;
    }

    /// <summary>The account a sign-in state token belongs to, or null when it is unknown or already used.</summary>
    public static long? ConsumeState(IMemoryCache cache, string? state)
    {
        if (string.IsNullOrWhiteSpace(state) || !state.StartsWith(StatePrefix, StringComparison.Ordinal)) return null;
        var key = StateCachePrefix + state;
        if (!cache.TryGetValue(key, out long accountId)) return null;
        cache.Remove(key);
        return accountId;
    }

    // The address FYERS must send the trader back to. Behind the tunnel the
    // request arrives on the domain with the forwarded scheme, so this is the
    // public URL; an explicit Frontend:BaseUrl wins when the operator set one.
    private string CallbackUrl()
    {
        var configured = _configuration["Frontend:BaseUrl"]?.Trim().TrimEnd('/');
        var origin = string.IsNullOrEmpty(configured) ? $"{Request.Scheme}://{Request.Host}" : configured;
        return $"{origin}/api/auth/callback";
    }

    private Task<BrokerAccount?> FindAccountAsync(long userId, CancellationToken ct)
        => _db.BrokerAccounts.FirstOrDefaultAsync(a => a.UserId == userId && a.ProviderKey == ProviderKey && a.IsEnabled, ct);

    [HttpGet]
    public async Task<ActionResult<TraderBrokerStatus>> Status(CancellationToken ct)
    {
        var userId = User.GetRequiredUserId();
        var status = new TraderBrokerStatus { CallbackUrl = CallbackUrl() };
        var account = await FindAccountAsync(userId, ct);
        if (account is null) return Ok(status);

        var config = await _db.BrokerConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BrokerAccountId == account.Id, ct);
        if (config is not null)
        {
            status.Configured = !string.IsNullOrWhiteSpace(config.ClientId);
            status.ClientId = config.ClientId;
            status.RedirectUri = config.RedirectUri;
            status.HasTradingPin = !string.IsNullOrWhiteSpace(config.TradingPinEncrypted);
        }

        var session = await _sessions.GetForAccountAsync(account.Id, ct);
        if (session is not null)
        {
            status.IsAuthenticated = session.IsAuthenticated;
            status.SignedInUtc = session.UpdatedUtc;
            status.ExpiresAtUtc = session.ExpiresAtUtc;
        }
        return Ok(status);
    }

    [HttpPut("credentials")]
    public async Task<IActionResult> SaveCredentials([FromBody] SaveTraderBrokerRequest request, CancellationToken ct)
    {
        var userId = User.GetRequiredUserId();
        var clientId = (request.ClientId ?? string.Empty).Trim();
        var secret = (request.SecretKey ?? string.Empty).Trim();
        if (clientId.Length == 0 || secret.Length == 0)
            return BadRequest(new { message = "App ID and secret key are both required." });

        var account = await FindAccountAsync(userId, ct);
        if (account is null)
        {
            account = new BrokerAccount
            {
                ProviderKey = ProviderKey,
                UserId = userId,
                Label = $"{User.GetUserName() ?? userId.ToString()} · FYERS",
                IsEnabled = true,
                CreatedBy = User.GetUserName() ?? userId.ToString(),
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow,
            };
            _db.BrokerAccounts.Add(account);
            await _db.SaveChangesAsync(ct);
        }

        // The redirect must be this server's callback for the sign-in to come
        // back here at all; a trader's typo would send FYERS somewhere else.
        var redirect = string.IsNullOrWhiteSpace(request.RedirectUri) ? CallbackUrl() : request.RedirectUri.Trim();
        var pin = string.IsNullOrWhiteSpace(request.TradingPin) ? null : request.TradingPin.Trim();
        await _credentials.SaveAsync(ProviderKey, clientId, secret, redirect, User.GetUserName() ?? userId.ToString(), pin, account.Id, ct);

        // New app credentials invalidate whatever token the old ones produced.
        await _sessions.ClearAccountAsync(account.Id, ct);
        _logger.LogInformation("Trader {UserId} saved FYERS app credentials for broker account {AccountId}.", userId, account.Id);
        return Ok(new { message = "Saved. Now sign in to FYERS to link the account.", accountId = account.Id });
    }

    [HttpGet("auth-url")]
    public async Task<IActionResult> AuthUrl(CancellationToken ct)
    {
        var userId = User.GetRequiredUserId();
        var account = await FindAccountAsync(userId, ct);
        if (account is null) return BadRequest(new { message = "Save your FYERS app credentials first." });

        // A one-time state ties the broker's redirect back to THIS account.
        // Without it the callback could not tell whose token it was handed.
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var state = StatePrefix + nonce;
        _cache.Set(StateCachePrefix + state, account.Id, StateLifetime);

        try
        {
            var broker = await _router.ResolveBrokerAsync(account.Id, ct);
            var url = await broker.GetAuthUrlAsync(state, account.Id, ct);
            return Ok(new { authUrl = url });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        var account = await FindAccountAsync(User.GetRequiredUserId(), ct);
        if (account is not null) await _sessions.ClearAccountAsync(account.Id, ct);
        return Ok(new { message = "Signed out of your broker." });
    }

    [HttpDelete]
    public async Task<IActionResult> Remove(CancellationToken ct)
    {
        var account = await FindAccountAsync(User.GetRequiredUserId(), ct);
        if (account is null) return Ok(new { message = "Nothing to remove." });
        await _sessions.ClearAccountAsync(account.Id, ct);
        var configs = await _db.BrokerConfigs.Where(c => c.BrokerAccountId == account.Id).ToListAsync(ct);
        _db.BrokerConfigs.RemoveRange(configs);
        _db.BrokerAccounts.Remove(account);
        await _db.SaveChangesAsync(ct);
        return Ok(new { message = "Broker account removed." });
    }

    // ------------------------------------------------------------ their data
    // Read straight from FYERS with the trader's own token and returned as the
    // broker sends it: the console shows the broker's figures, not a re-telling.

    private static readonly (string Name, string Path)[] Resources =
    {
        ("profile", "/api/v3/profile"),
        ("funds", "/api/v3/funds"),
        ("holdings", "/api/v3/holdings"),
        ("positions", "/api/v3/positions"),
        ("orders", "/api/v3/orders"),
        ("trades", "/api/v3/tradebook"),
    };

    [HttpGet("{resource}")]
    public async Task<IActionResult> Resource(string resource, CancellationToken ct)
    {
        var path = Resources.FirstOrDefault(r => r.Name == resource.ToLowerInvariant()).Path;
        if (path is null) return NotFound(new { message = $"Unknown broker resource '{resource}'." });

        var account = await FindAccountAsync(User.GetRequiredUserId(), ct);
        if (account is null) return Conflict(new { message = "No broker account — save your FYERS app credentials first." });
        var session = await _sessions.GetForAccountAsync(account.Id, ct);
        if (session is null || !session.IsAuthenticated)
            return Conflict(new { message = "Your broker is not signed in. Sign in to FYERS on the Account page." });
        var creds = await _credentials.GetAsync(ProviderKey, account.Id, ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api-t1.fyers.in" + path);
        request.Headers.TryAddWithoutValidation("Authorization", $"{creds.ClientId}:{session.AccessToken}");
        try
        {
            using var response = await _http.CreateClient(nameof(TraderBrokerController)).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return new ContentResult { Content = body, ContentType = "application/json", StatusCode = (int)response.StatusCode };
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { message = $"FYERS did not answer: {ex.Message}" });
        }
    }
}
