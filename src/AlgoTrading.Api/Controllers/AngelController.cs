using AlgoTrading.Api.Security;
using AlgoTrading.Infrastructure.Providers.Angel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// What the console needs to show and test the Angel One connector.
/// </summary>
/// <remarks>
/// Read-only: it reports what is configured and, on request, signs in and asks
/// for one price. Nothing is stored, no feed is started, and no other
/// connector is touched. Secrets are never returned — only whether they are
/// set, and which ones are missing by the name an operator would fill in.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AngelController : ControllerBase
{
    private readonly AngelApiClient _client;
    private readonly AngelMarketMovers _movers;
    private readonly AngelSettings _settings;

    public AngelController(AngelApiClient client, AngelMarketMovers movers, IOptions<AngelSettings> settings)
    {
        _client = client;
        _movers = movers;
        _settings = settings.Value;
    }

    /// <summary>What is configured, what is missing, and whether a session is held.</summary>
    [HttpGet("status")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult Status() => Ok(new
    {
        provider = AngelProvider.Key,
        displayName = AngelProvider.Descriptor.DisplayName,
        rootUrl = _settings.RootUrl,
        clientCode = _settings.ClientCode,
        staticIp = _settings.StaticIp,
        configured = _settings.IsConfigured,
        missing = _settings.Missing(),
        hasSession = _client.HasSession,
        sessionStartedUtc = _client.SessionStartedUtc,
        // Said plainly, because it is the refusal an operator will hit first.
        note = "The SmartAPI app answers only from the static IP it was registered with.",
    });

    /// <summary>Sign in and fetch one price, to prove the credentials and the IP.</summary>
    [HttpPost("test")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Test([FromBody] AngelTestRequest? request, CancellationToken cancellationToken)
    {
        var login = await _client.LoginAsync(force: request?.ForceLogin ?? false, cancellationToken);
        if (!login.Ok) return Ok(new { ok = false, step = "login", message = login.Message });

        // NIFTY 50's token is stable and needs no instrument master to try.
        string exchange = string.IsNullOrWhiteSpace(request?.Exchange) ? "NSE" : request!.Exchange!;
        string symbol = string.IsNullOrWhiteSpace(request?.TradingSymbol) ? "Nifty 50" : request!.TradingSymbol!;
        string token = string.IsNullOrWhiteSpace(request?.Token) ? "99926000" : request!.Token!;

        var quote = await _client.LtpAsync(exchange, symbol, token, cancellationToken);
        return Ok(new
        {
            ok = quote.Ok,
            step = quote.Ok ? "quote" : "quote-failed",
            message = quote.Ok ? $"{symbol}: signed in and priced in {quote.ElapsedMs} ms." : quote.Message,
            elapsedMs = quote.ElapsedMs,
            data = quote.Data,
        });
    }

    /// <summary>
    /// The market-wide screens: top gainers and losers, the open-interest
    /// build-up behind them, and the put-call ratio per underlying.
    /// </summary>
    /// <remarks>
    /// One snapshot is shared by every caller for 45 seconds — six SmartAPI
    /// calls go into it and the vendor refuses a burst. <c>force=true</c> asks
    /// for a fresh one anyway.
    /// </remarks>
    [HttpGet("movers")]
    public async Task<IActionResult> Movers([FromQuery] string expiry = "NEAR", [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (!_settings.IsConfigured)
            return Ok(new { configured = false, missing = _settings.Missing(),
                            message = "Angel One is not configured: set " + string.Join(", ", _settings.Missing()) + "." });

        var snapshot = await _movers.GetAsync(expiry, force, cancellationToken);
        return Ok(new
        {
            configured = true,
            asOfUtc = snapshot.AsOfUtc,
            expiryType = snapshot.ExpiryType,
            priceGainers = snapshot.PriceGainers,
            priceLosers = snapshot.PriceLosers,
            buildUp = snapshot.BuildUp,
            pcr = snapshot.Pcr,
            warnings = snapshot.Warnings,
        });
    }

    /// <summary>Forget the session, so the next call signs in again.</summary>
    [HttpPost("sign-out")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult SignOut()
    {
        AngelApiClient.ForgetSession();
        return Ok(new { message = "The Angel One session was forgotten." });
    }
}

/// <summary>Optional overrides for the connection test; the defaults price NIFTY 50.</summary>
public sealed class AngelTestRequest
{
    public bool ForceLogin { get; set; }
    public string? Exchange { get; set; }
    public string? TradingSymbol { get; set; }
    public string? Token { get; set; }
}
