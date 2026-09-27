using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
            return Redirect($"/system/connectors/{DhanProvider.Key}?connected=1");
        }
        catch (InvalidOperationException ex)
        {
            return Redirect($"/system/connectors/{DhanProvider.Key}?connected=0&reason={Uri.EscapeDataString(ex.Message)}");
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

    /// <summary>
    /// The automatic PIN + TOTP sign-in: whether it is set up, when it runs, and
    /// what it last did. Names what is missing, never a value.
    /// </summary>
    [HttpGet("auto-sign-in")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> AutoSignInStatus(
        [FromServices] IOptionsMonitor<DhanSettings> settings,
        [FromServices] IBrokerCredentialsProvider credentials,
        [FromServices] DhanAutoSignInState state,
        CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var creds = await credentials.GetAsync(DhanProvider.Key, cancellationToken: cancellationToken);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(creds.ClientId)) missing.Add("DHAN_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(s.Pin)) missing.Add("DHAN_PIN");
        if (string.IsNullOrWhiteSpace(s.TotpSecret)) missing.Add("DHAN_TOTP_SECRET");

        return Ok(new
        {
            configured = missing.Count == 0,
            enabled = s.AutoSignIn.Enabled,
            missing,
            morningFromIst = s.AutoSignIn.MorningFromIst.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            morningUntilIst = s.AutoSignIn.MorningUntilIst.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            stoppedForToday = state.StoppedFor(DateTime.UtcNow),
            lastAttemptUtc = state.LastAttemptUtc,
            lastOk = state.LastOk,
            lastTrigger = state.LastTrigger,
            lastMessage = state.LastMessage,
            lastExpiresUtc = state.LastExpiresUtc,
        });
    }

    /// <summary>
    /// Sign in to Dhan now with the PIN and a TOTP code. <paramref name="trigger"/>
    /// is "console" for the button (always tries) or "morning job" for the 08:45
    /// script, which is held back like the worker with a 409 (switched off,
    /// stopped for today even across a restart, or pausing after a failure) and,
    /// within two minutes of a sign-in, answered with that one, not a new code.
    /// A failure is an error status, not 200 with ok:false: a caller that only
    /// checks the status must not read a refused PIN as a sign-in.
    /// </summary>
    /// <remarks>
    /// Takes no cancellation token. The morning job's curl gives up after 30
    /// seconds; hanging up after Dhan had issued the token would have cancelled
    /// the save and lost it, and spent the code. The sign-in is bounded without
    /// it: 30 seconds for Dhan's answer, at most one TOTP step of waiting for a
    /// fresh code, and one sign-in at a time.
    /// </remarks>
    [HttpPost("auto-sign-in")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> AutoSignInNow(
        [FromServices] DhanAutoSignInService service,
        [FromServices] IOptionsMonitor<DhanSettings> settings,
        [FromQuery] string? trigger)
    {
        string who = trigger == "morning job" ? "morning job" : "console";

        // The worker obeys this switch; the morning job's call used to go
        // straight past it and sign in with the PIN anyway.
        if (who != "console" && !settings.CurrentValue.AutoSignIn.Enabled)
        {
            return Conflict(new
            {
                ok = false,
                message = "Not tried: the automatic Dhan sign-in is switched off (Dhan:AutoSignIn:Enabled is false). Press Connect, or Sign in now on the Dhan page.",
            });
        }

        var result = await service.SignInAsync(who, who == "console" ? $"asked from the console by {User.Identity?.Name ?? "an admin"}" : "asked by the morning job", CancellationToken.None);
        if (result.Ok) return Ok(new { ok = true, message = result.Message, expiresUtc = result.ExpiresUtc });
        if (!result.Tried) return Conflict(new { ok = false, message = $"{result.Message} Press Connect, or Sign in now on the Dhan page." });

        // 400 when it is not set up or Dhan said no; 502 when Dhan was not reached.
        int status = result.Failure == DhanSignInFailure.Unreachable
            ? StatusCodes.Status502BadGateway
            : StatusCodes.Status400BadRequest;
        return StatusCode(status, new { ok = false, message = result.Message });
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

    /// <summary>
    /// Turns the recorder on until the end of the IST day, across API restarts;
    /// Dhan:ChainPoller:Enabled decides from the next day. The morning job calls
    /// this every trading day.
    /// </summary>
    [HttpPost("chain-poller/start")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> StartChainPoller([FromServices] Microsoft.Extensions.Options.IOptions<DhanSettings> settings)
    {
        await _pollerState.SwitchAsync(on: true, User.Identity?.Name);
        return Ok(PollerView(settings.Value));
    }

    /// <summary>Turns the recorder off until the end of the IST day, across API restarts.</summary>
    [HttpPost("chain-poller/stop")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> StopChainPoller([FromServices] Microsoft.Extensions.Options.IOptions<DhanSettings> settings)
    {
        // wasRunning, like the other daemons' stops: market-close.sh reads it,
        // and without it the nightly report always said the recorder "was not
        // running", including on the nights it was switched off.
        bool wasRunning = _pollerState.Enabled;
        await _pollerState.SwitchAsync(on: false, User.Identity?.Name);
        var s = settings.Value;
        return Ok(new
        {
            wasRunning,
            enabled = _pollerState.Enabled,
            configuredEnabled = s.ChainPoller.Enabled,
            intervalSeconds = s.ChainPoller.IntervalSeconds,
            underlyings = s.ChainPoller.UnderlyingList,
            lastRoundStartedUtc = _pollerState.LastRoundStartedUtc,
            lastRoundFinishedUtc = _pollerState.LastRoundFinishedUtc,
            outcomes = _pollerState.Outcomes,
        });
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

    /// <summary>An expired options import. Offsets as [-3,…,3] or "-3..3"; types default to CE and PE.</summary>
    public sealed record OptionHistoryImportRequest(
        string? Underlying,
        string? From,
        string? To,
        System.Text.Json.JsonElement? StrikeOffsets,
        IReadOnlyList<string>? OptionTypes,
        string? ExpiryFlag,
        int? ExpiryCode,
        string? Interval);

    /// <summary>
    /// Queues an import of Dhan's expired options history into option_history_bars
    /// and answers at once with the job id. Windows already stored
    /// are skipped, so posting the same import again resumes it.
    /// </summary>
    [HttpPost("option-history/import")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult ImportOptionHistory(
        [FromBody] OptionHistoryImportRequest? request,
        [FromServices] DhanOptionHistoryJobs jobs)
    {
        if (request is null) return BadRequest(new { message = "A JSON body is required." });

        DhanOptionHistoryRequest validated;
        try
        {
            var todayIst = AlgoTrading.Infrastructure.Services.IstTime.DateOf(DateTime.UtcNow);
            validated = DhanOptionHistoryRequest.Create(
                request.Underlying, request.From, request.To, request.StrikeOffsets, request.OptionTypes,
                request.ExpiryFlag, request.ExpiryCode, request.Interval, todayIst);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return BadRequest(new { message = ex.Message });
        }

        var job = jobs.Enqueue(validated);
        int windows = DhanRollingOptions.Windows(validated.From, validated.To).Count * validated.Series.Count;
        return Accepted($"/api/Dhan/option-history/jobs/{job.Id}", new
        {
            jobId = job.Id,
            windows,
            // The most this can ask of Dhan: one request per window, before
            // skipping what is stored and what fell on no trading day.
            maxRequests = windows,
            job = job.View(),
        });
    }

    /// <summary>Every import this process has run or queued, newest first.</summary>
    [HttpGet("option-history/jobs")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult OptionHistoryJobs([FromServices] DhanOptionHistoryJobs jobs) =>
        Ok(new { jobs = jobs.All().Select(j => j.View()) });

    /// <summary>One import's progress: windows done of total, rows, errors.</summary>
    [HttpGet("option-history/jobs/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult OptionHistoryJob(Guid id, [FromServices] DhanOptionHistoryJobs jobs) =>
        jobs.Get(id) is { } job
            ? Ok(job.View())
            : NotFound(new { message = "No such import in this process. Jobs do not survive a restart; posting the import again resumes it." });

    /// <summary>Stops an import after the requests in flight.</summary>
    [HttpPost("option-history/jobs/{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult CancelOptionHistoryJob(Guid id, [FromServices] DhanOptionHistoryJobs jobs)
    {
        if (jobs.Get(id) is not { } job) return NotFound(new { message = "No such import in this process." });
        if (!job.IsFinished) job.Cancellation.Cancel();
        return Ok(job.View());
    }

    /// <summary>
    /// What option_history_bars holds for an underlying: per series, the days
    /// stored as runs of consecutive weekdays, so any missing trading day (or
    /// holiday) shows as a break.
    /// </summary>
    [HttpGet("option-history/coverage")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> OptionHistoryCoverage(
        [FromQuery] string underlying,
        [FromServices] DhanOptionHistoryImporter importer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying)) return BadRequest(new { message = "underlying is required." });

        var rows = await importer.CoverageAsync(underlying, cancellationToken);
        string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var series = rows
            .GroupBy(r => (r.ExpiryFlag, r.ExpiryCode, r.Resolution, r.StrikeOffset, r.OptionType))
            .Select(g => new
            {
                expiryFlag = g.Key.ExpiryFlag,
                expiryCode = g.Key.ExpiryCode,
                resolution = g.Key.Resolution,
                strikeOffset = g.Key.StrikeOffset,
                optionType = g.Key.OptionType,
                days = g.Count(),
                bars = g.Sum(r => r.Bars),
                first = Day(g.Min(r => r.Day)),
                last = Day(g.Max(r => r.Day)),
                runs = DhanRollingOptions.Runs(g.Select(r => r.Day)).Select(run => new[] { Day(run.From), Day(run.To) }),
            })
            .ToList();

        return Ok(new
        {
            underlying = underlying.Trim().ToUpperInvariant(),
            series = series.Count,
            bars = series.Sum(s => s.bars),
            coverage = series,
        });
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
