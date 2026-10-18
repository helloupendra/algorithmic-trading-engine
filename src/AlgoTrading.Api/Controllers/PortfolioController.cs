using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.Portfolio;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The Portfolio page: a signed-in user links their own FYERS, Dhan or Angel One account, read only, and reads a
/// factual report on what it holds. Never advice: no buy, sell, hold, exit, target or stop-loss suggestion.
/// </summary>
/// <remarks>
/// <para>
/// Every route takes the user from the sign-in token (<see cref="ClaimsPrincipalExtensions.GetRequiredUserId"/>) and
/// never from the request, so a user reads and changes only their own links and snapshots; there is no route that
/// takes another user's id. An admin gets counts per broker and nothing else.
/// </para>
/// <para>
/// No response carries a broker token. The activity log records each connect, disconnect and sync with no amounts.
/// docs/modules/portfolio.md describes each broker's link and every figure.
/// </para>
/// </remarks>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PortfolioController : ControllerBase
{
    private readonly PortfolioLinkService _links;
    private readonly PortfolioSyncService _sync;
    private readonly PortfolioReportService _reports;
    private readonly string? _frontendBaseUrl;

    public PortfolioController(
        PortfolioLinkService links,
        PortfolioSyncService sync,
        PortfolioReportService reports,
        IConfiguration configuration)
    {
        _links = links;
        _sync = sync;
        _reports = reports;
        string? configured = configuration["Frontend:BaseUrl"]?.Trim().TrimEnd('/');
        _frontendBaseUrl = string.IsNullOrEmpty(configured) ? null : configured;
    }

    /// <summary>Every broker, linked or not, with how each one links.</summary>
    [HttpGet("connections")]
    public async Task<ActionResult<PortfolioConnectionsResponse>> Connections(CancellationToken cancellationToken)
        => Ok(await _links.ListAsync(User.GetRequiredUserId(), cancellationToken));

    /// <summary>
    /// Starts a link: the broker's sign-in page for FYERS and Angel One (with a one-time state, also set as a cookie on
    /// this browser), or the fields to paste for Dhan.
    /// </summary>
    [HttpPost("connections/{broker}/start")]
    public async Task<ActionResult<PortfolioStartResponse>> Start(string broker, CancellationToken cancellationToken)
    {
        broker = broker.ToLowerInvariant();
        if (!PortfolioBrokers.IsKnown(broker)) return NotFound(new { message = $"Unknown broker \"{broker}\"." });

        var outcome = await _links.StartAsync(User.GetRequiredUserId(), broker, cancellationToken);
        if (outcome.Response is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = outcome.Error });

        if (outcome.State is string state)
        {
            Response.Cookies.Append(PortfolioLinkStates.CookieName(broker), state, LinkCookie(PortfolioLinkStates.Lifetime));
        }

        HttpContext.Describe($"Started linking {PortfolioBrokers.NameOf(broker)} to the Portfolio page.", "portfolio", broker);
        return Ok(outcome.Response);
    }

    /// <summary>
    /// Anonymous by necessity: the broker's redirect lands here in the user's browser and carries no bearer token of
    /// ours. The one-time state, which must also be in this browser's cookie, says whose account it is; anything else
    /// is refused. FYERS links made with the platform's own app arrive through <c>api/Auth/callback</c>, which passes a
    /// portfolio state on to here. Always ends in a redirect to the Portfolio page.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("callback/{broker}")]
    public async Task<IActionResult> Callback(
        string broker,
        [FromQuery] string? state,
        [FromQuery(Name = "auth_code")] string? authCode,
        [FromQuery(Name = "s")] string? status,
        [FromQuery(Name = "auth_token")] string? authToken,
        CancellationToken cancellationToken)
    {
        broker = broker.ToLowerInvariant();
        if (!PortfolioBrokers.IsKnown(broker)) return PageRedirect(null, "Unknown broker.");

        string cookie = PortfolioLinkStates.CookieName(broker);
        string? cookieState = Request.Cookies[cookie];
        Response.Cookies.Delete(cookie, LinkCookie(null));

        var outcome = await _links.CompleteRedirectAsync(broker, new PortfolioRedirect(state, authCode, status, authToken), cookieState, cancellationToken);
        if (outcome.UserId is long userId)
        {
            string name = PortfolioBrokers.NameOf(broker);
            await _links.RecordAsync(userId, $"connect:{broker}",
                outcome.Ok ? $"Connected {name} to the Portfolio page: {outcome.Message}" : $"Could not connect {name} to the Portfolio page: {outcome.Message}",
                outcome.Ok, Request.Path.Value ?? string.Empty, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        }

        return outcome.Ok ? PageRedirect(broker, null) : PageRedirect(null, outcome.Message);
    }

    /// <summary>Links a broker with a token the user generated (Dhan): checked by one read before it is saved.</summary>
    [HttpPost("connections/{broker}/token")]
    public async Task<ActionResult<PortfolioConnectionView>> Token(string broker, [FromBody] PortfolioTokenRequest request, CancellationToken cancellationToken)
    {
        broker = broker.ToLowerInvariant();
        if (!PortfolioBrokers.IsKnown(broker)) return NotFound(new { message = $"Unknown broker \"{broker}\"." });

        var (view, error) = await _links.SaveTokenAsync(User.GetRequiredUserId(), broker, request.Fields, cancellationToken);
        if (view is null)
        {
            HttpContext.Describe($"Could not connect {PortfolioBrokers.NameOf(broker)} to the Portfolio page.", "portfolio", broker);
            return BadRequest(new { message = error });
        }

        HttpContext.Describe($"Connected {PortfolioBrokers.NameOf(broker)} ({view.AccountLabel}) to the Portfolio page.", "portfolio", broker);
        return Ok(view);
    }

    /// <summary>Disconnects a broker and deletes its token; <c>forget=true</c> deletes its snapshots too.</summary>
    [HttpDelete("connections/{broker}")]
    public async Task<IActionResult> Disconnect(string broker, [FromQuery] bool forget = false, CancellationToken cancellationToken = default)
    {
        broker = broker.ToLowerInvariant();
        if (!PortfolioBrokers.IsKnown(broker)) return NotFound(new { message = $"Unknown broker \"{broker}\"." });

        await _links.DisconnectAsync(User.GetRequiredUserId(), broker, forget, cancellationToken);
        HttpContext.Describe(
            $"Disconnected {PortfolioBrokers.NameOf(broker)} from the Portfolio page{(forget ? " and deleted its snapshots" : string.Empty)}.",
            "portfolio", broker);
        return NoContent();
    }

    /// <summary>Reads every linked broker now. Once a minute per user.</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<PortfolioSyncResponse>> Sync(CancellationToken cancellationToken)
    {
        var outcome = await _sync.SyncAsync(User.GetRequiredUserId(), cancellationToken);
        if (outcome.Response is null)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(outcome.RetryAfter.TotalSeconds));
            Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new { message = $"The portfolio was synced less than a minute ago. Try again in {seconds} s.", retryAfterSeconds = seconds });
        }

        int ok = outcome.Response.Results.Count(r => r.Ok);
        HttpContext.Describe($"Synced the portfolio: {ok} of {outcome.Response.Results.Count} brokers read.", "portfolio");
        return Ok(outcome.Response);
    }

    /// <summary>The report, from each linked broker's newest snapshot; 404 until the first sync.</summary>
    [HttpGet("report")]
    public async Task<ActionResult<PortfolioReport>> Report(CancellationToken cancellationToken)
    {
        var report = await _reports.ReportAsync(User.GetRequiredUserId(), cancellationToken);
        return report is null
            ? NotFound(new { message = "No portfolio snapshot yet: connect a broker and press Sync." })
            : Ok(report);
    }

    /// <summary>Invested and current value by day, from the snapshots kept (90 days).</summary>
    [HttpGet("history")]
    public async Task<ActionResult<IReadOnlyList<PortfolioHistoryPoint>>> History([FromQuery] int days = 90, CancellationToken cancellationToken = default)
        => Ok(await _reports.HistoryAsync(User.GetRequiredUserId(), days, cancellationToken));

    /// <summary>
    /// Admin-only: how many users linked each broker, by status. No amounts, no holdings, no names: an admin has no
    /// route to another user's portfolio.
    /// </summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpGet("admin/connections")]
    public async Task<ActionResult<IReadOnlyList<PortfolioBrokerCount>>> AdminCounts(CancellationToken cancellationToken)
        => Ok(await _links.CountsAsync(cancellationToken));

    /// <summary>
    /// The cookie that ties a broker's redirect to this browser: HttpOnly, sent only to the callbacks, and Lax so the
    /// broker's top-level redirect back carries it.
    /// </summary>
    private CookieOptions LinkCookie(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = PortfolioLinkStates.CookiePath,
        MaxAge = maxAge,
        IsEssential = true,
    };

    /// <summary>Back to the Portfolio page, which reads the outcome from the address.</summary>
    private RedirectResult PageRedirect(string? connected, string? error)
    {
        string origin = _frontendBaseUrl ?? string.Empty;
        string query = connected is not null
            ? $"connected={Uri.EscapeDataString(connected)}"
            : $"error={Uri.EscapeDataString(Short(error))}";
        return Redirect($"{origin}/portfolio?{query}");
    }

    private static string Short(string? message)
    {
        string text = string.IsNullOrWhiteSpace(message) ? "The broker sign-in did not finish." : message.Trim();
        return text.Length <= 200 ? text : text[..199] + "…";
    }
}
