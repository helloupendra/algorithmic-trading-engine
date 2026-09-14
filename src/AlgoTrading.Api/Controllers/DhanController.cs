using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The things about Dhan that are its own rather than every vendor's: whether the
/// account's token and data plan are alive, its option chain, and its instrument
/// master. Credentials, routing and history go through the shared connector
/// paths like every other vendor's.
/// </summary>
/// <remarks>
/// Authorization is per action, not on the class. ASP.NET requires every
/// attribute to pass, so an AdminOnly policy on the class would silently refuse
/// the Service account on <see cref="Resolve"/> even with the role listed there,
/// and the live feed runs as that account.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class DhanController : ControllerBase
{
    private readonly DhanApiClient _api;
    private readonly DhanOptionChainClient _chain;
    private readonly DhanInstrumentImporter _importer;
    private readonly ISymbolMapper _symbolMapper;
    private readonly DhanLoginFlow _login;
    private readonly DhanChainPollerState _pollerState;

    public DhanController(DhanApiClient api, DhanOptionChainClient chain, DhanInstrumentImporter importer, ISymbolMapper symbolMapper, DhanLoginFlow login, DhanChainPollerState pollerState)
    {
        _api = api;
        _chain = chain;
        _importer = importer;
        _symbolMapper = symbolMapper;
        _login = login;
        _pollerState = pollerState;
    }

    /// <summary>
    /// Where Dhan sends the browser after the daily sign-in: the Redirect URL
    /// registered with the API key. Open to the browser, because Dhan's redirect
    /// carries no platform login; what it can do is bounded by the flow itself —
    /// the tokenId must be consumable with this platform's API secret, and the
    /// account signed in must be the configured client id.
    /// </summary>
    [HttpGet("callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback([FromQuery] string? tokenId, CancellationToken cancellationToken)
    {
        try
        {
            await _login.CompleteAsync(tokenId ?? string.Empty, cancellationToken);
            return Redirect($"/admin/broker/{DhanProvider.Key}?connected=1");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"/admin/broker/{DhanProvider.Key}?connected=0&reason={Uri.EscapeDataString(ex.Message)}");
        }
    }

    /// <summary>
    /// The client id and token the live feed connects with. A credential, so
    /// only admins and the Service account the feed runs as may read it.
    /// </summary>
    [HttpGet("session")]
    [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Service}")]
    public async Task<IActionResult> Session(CancellationToken cancellationToken)
    {
        try
        {
            var (clientId, token) = await _api.ResolveTokenAsync(cancellationToken);
            return Ok(new { clientId, accessToken = token.Value, source = token.Source, expiresUtc = token.ExpiresUtc });
        }
        catch (DhanApiException ex)
        {
            return NotFound(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Is the token accepted, until when, and is the data plan active? Answered
    /// from Dhan's own profile, so "configured" is never mistaken for "working".
    /// </summary>
    [HttpGet("status")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        try
        {
            var (_, token) = await _api.ResolveTokenAsync(cancellationToken);
            using var profile = await _api.GetAsync("/profile", DhanRateClass.Data, cancellationToken);
            var root = profile.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var v) ? v.ToString() : null;

            return Ok(new
            {
                ok = true,
                // "sign-in" (the daily Connect) or "configuration" (a pasted token).
                tokenSource = token.Source,
                signInExpiresUtc = token.ExpiresUtc,
                tokenValidity = Read("tokenValidity"),
                dataPlan = Read("dataPlan"),
                dataValidity = Read("dataValidity"),
                activeSegment = Read("activeSegment"),
            });
        }
        catch (DhanApiException ex)
        {
            // The problem is the answer: shown as state, not as a failed request.
            return Ok(new { ok = false, authFailure = ex.IsAuthFailure, notSubscribed = ex.IsNotSubscribed, error = ex.Message });
        }
    }

    /// <summary>Expiries Dhan lists for an index underlying.</summary>
    [HttpGet("expiries")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Expiries([FromQuery] string underlying, CancellationToken cancellationToken)
    {
        try
        {
            var expiries = await _chain.GetExpiriesAsync(underlying, cancellationToken);
            return Ok(new { underlying = underlying.Trim().ToUpperInvariant(), expiries = expiries.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) });
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or DhanApiException)
        {
            return Failure(ex);
        }
    }

    /// <summary>One underlying's option chain with OI, OI change, volume, IV and greeks.</summary>
    /// <param name="underlying">"NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX", "BANKEX".</param>
    /// <param name="expiry">yyyy-MM-dd.</param>
    [HttpGet("chain")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Chain([FromQuery] string underlying, [FromQuery] string expiry, CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiryDate))
            return BadRequest(new { message = "expiry must be yyyy-MM-dd." });

        try
        {
            var chain = await _chain.GetChainAsync(underlying, expiryDate, cancellationToken);
            return Ok(new
            {
                underlying = chain.Underlying,
                expiry = chain.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                underlyingPrice = chain.UnderlyingPrice,
                strikes = chain.Rows.Count,
                peakCallOiStrike = chain.PeakCallOiStrike,
                peakPutOiStrike = chain.PeakPutOiStrike,
                putCallOiRatio = chain.PutCallOiRatio,
                rows = chain.Rows.Select(r => new { strike = r.Strike, call = Side(r.Call), put = Side(r.Put) }),
            });
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or DhanApiException)
        {
            return Failure(ex);
        }
    }

    /// <summary>
    /// Downloads Dhan's instrument master and maps every live platform instrument
    /// to its Dhan segment and security id. Takes tens of seconds.
    /// </summary>
    [HttpPost("instruments/import")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> ImportInstruments(CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _importer.ImportAsync(cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message });
        }
    }

    public sealed record ResolveRequest(IReadOnlyList<string>? Symbols);

    /// <summary>
    /// Dhan's "SEGMENT:SECURITYID:INSTRUMENT" for canonical symbols: what the
    /// live feed subscribes with. Open to the Service account the feed runs as.
    /// </summary>
    [HttpPost("instruments/resolve")]
    [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Service}")]
    public async Task<IActionResult> Resolve([FromBody] ResolveRequest? request, CancellationToken cancellationToken)
    {
        var symbols = (request?.Symbols ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (symbols.Count > 5000) return BadRequest(new { message = "At most 5000 symbols per request." });

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<string>();
        foreach (var symbol in symbols)
        {
            if (DhanInstruments.Indices.TryGetValue(symbol, out var index)) resolved[symbol] = index.ToVendorSymbol();
            else rest.Add(symbol);
        }

        if (rest.Count > 0)
        {
            var mapped = await _symbolMapper.ToVendorManyAsync(rest, DhanProvider.Key, cancellationToken);
            foreach (var symbol in rest)
            {
                if (mapped.TryGetValue(symbol, out var vendor) && DhanInstrument.Parse(vendor) is not null)
                    resolved[symbol] = vendor;
            }
        }

        return Ok(new { resolved, unresolved = symbols.Where(s => !resolved.ContainsKey(s)).ToList() });
    }

    /// <summary>
    /// What the Dhan feed streams beyond the watchlist: indices, nearest futures,
    /// at-the-money options. Read by the feed every few minutes, so it follows
    /// the market through the day.
    /// </summary>
    [HttpGet("universe")]
    [Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Service}")]
    public async Task<IActionResult> Universe([FromServices] DhanUniverseBuilder builder, CancellationToken cancellationToken)
    {
        var universe = await builder.GetAsync(cancellationToken);
        return Ok(new
        {
            symbols = universe.Symbols,
            generatedUtc = universe.GeneratedUtc,
            counts = universe.Counts,
            spots = universe.Spots,
            warnings = universe.Warnings,
        });
    }

    /// <summary>Is the chain recorder on, and what did each underlying's last round do?</summary>
    [HttpGet("chain-poller")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult ChainPollerStatus([FromServices] Microsoft.Extensions.Options.IOptions<DhanSettings> settings) => Ok(PollerView(settings.Value));

    /// <summary>Turns the recorder on until the API restarts; Dhan:ChainPoller:Enabled decides after that.</summary>
    [HttpPost("chain-poller/start")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult StartChainPoller([FromServices] Microsoft.Extensions.Options.IOptions<DhanSettings> settings)
    {
        _pollerState.Enabled = true;
        return Ok(PollerView(settings.Value));
    }

    [HttpPost("chain-poller/stop")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult StopChainPoller([FromServices] Microsoft.Extensions.Options.IOptions<DhanSettings> settings)
    {
        _pollerState.Enabled = false;
        return Ok(PollerView(settings.Value));
    }

    /// <summary>
    /// One round now, for the named underlyings or the configured ones. Markets
    /// that are closed are skipped unless <paramref name="evenIfClosed"/>: a closed
    /// market's chain is its last close, and it would be stored stamped now.
    /// </summary>
    [HttpPost("chain-poller/capture")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> CaptureChain(
        [FromServices] DhanChainRecorder recorder,
        [FromServices] Microsoft.Extensions.Options.IOptions<DhanSettings> settings,
        [FromQuery] string? underlyings,
        [FromQuery] bool evenIfClosed,
        CancellationToken cancellationToken)
    {
        var names = string.IsNullOrWhiteSpace(underlyings)
            ? settings.Value.ChainPoller.UnderlyingList
            : underlyings.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outcomes = await recorder.RecordAsync(names, onlyOpenMarkets: !evenIfClosed, cancellationToken);
        return Ok(new { outcomes });
    }

    private object PollerView(DhanSettings settings) => new
    {
        enabled = _pollerState.Enabled,
        configuredEnabled = settings.ChainPoller.Enabled,
        intervalSeconds = settings.ChainPoller.IntervalSeconds,
        underlyings = settings.ChainPoller.UnderlyingList,
        lastRoundStartedUtc = _pollerState.LastRoundStartedUtc,
        lastRoundFinishedUtc = _pollerState.LastRoundFinishedUtc,
        outcomes = _pollerState.Outcomes,
    };

    private IActionResult Failure(Exception ex) => ex switch
    {
        NotSupportedException or ArgumentException => BadRequest(new { message = ex.Message }),
        // Never 401 here: the console reads 401 as "your own session expired".
        _ => StatusCode(StatusCodes.Status502BadGateway, new { message = ex.Message }),
    };

    private static object? Side(DhanChainSide? s) => s is null ? null : new
    {
        securityId = s.SecurityId,
        lastPrice = s.LastPrice,
        previousClose = s.PreviousClose,
        averagePrice = s.AveragePrice,
        bid = s.Bid,
        bidQuantity = s.BidQuantity,
        ask = s.Ask,
        askQuantity = s.AskQuantity,
        openInterest = s.OpenInterest,
        previousOpenInterest = s.PreviousOpenInterest,
        openInterestChange = s.OpenInterestChange,
        volume = s.Volume,
        previousVolume = s.PreviousVolume,
        impliedVolatility = s.ImpliedVolatility,
        greeks = s.Greeks is null ? null : new { delta = s.Greeks.Delta, theta = s.Greeks.Theta, gamma = s.Greeks.Gamma, vega = s.Greeks.Vega },
    };
}
