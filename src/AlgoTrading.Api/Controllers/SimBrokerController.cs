using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Providers.SimBroker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// What the console needs to show and test the connection to the simulated
/// OpenFNO broker.
/// </summary>
/// <remarks>
/// Read-only, and admin-only. It reports what is configured, asks the broker
/// which address it sees, and — on request — signs in and reads the account's
/// money and positions. It places no orders: routing live orders through this
/// broker is a later phase, and it has not been proved against a market
/// session. Secrets are never returned, only whether they are set and which
/// ones are missing by the name an operator would fill in.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SimBrokerController : ControllerBase
{
    private readonly SimBrokerClient _client;
    private readonly SimBrokerAccountService _accounts;
    private readonly IUserAdminService _users;
    private readonly SimBrokerSettings _settings;

    public SimBrokerController(
        SimBrokerClient client,
        SimBrokerAccountService accounts,
        IUserAdminService users,
        IOptions<SimBrokerSettings> settings)
    {
        _client = client;
        _accounts = accounts;
        _users = users;
        _settings = settings.Value;
    }

    /// <summary>What is configured, what is missing, and whether a session is held.</summary>
    [HttpGet("status")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult Status() => Ok(new
    {
        provider = SimBrokerProvider.Key,
        displayName = SimBrokerProvider.Descriptor.DisplayName,
        baseUrl = _settings.BaseUrl,
        clientId = _settings.ClientId,
        appId = _settings.AppId,
        staticIp = _settings.StaticIp,
        configured = _settings.IsConfigured,
        missing = _settings.Missing(),
        hasSession = _client.HasSession,
        sessionExpiresAt = _client.SessionExpiresAt,

        // The key itself is never returned — only whether the platform can act
        // as the broker's back office and issue accounts for traders.
        canAdminister = _settings.CanAdminister,
        // Said plainly, because it is what an operator will otherwise assume.
        note = "The platform does not route orders to this broker; this page only proves the connection.",
    });

    /// <summary>
    /// The address the broker sees for this server. No credentials are needed,
    /// so it answers even when nothing is configured — and it is the first
    /// thing to check when an order is refused for the wrong IP.
    /// </summary>
    [HttpGet("ping")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Ping(CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var who = await _client.WhoAmIAsync(cancellationToken);
        var elapsedMs = (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds;

        if (!who.Succeeded)
            return Ok(new { ok = false, baseUrl = _settings.BaseUrl, elapsedMs, code = who.ErrorCode, message = who.ErrorMessage });

        bool whitelisted = string.IsNullOrWhiteSpace(_settings.StaticIp) || _settings.StaticIp == who.Value;
        return Ok(new
        {
            ok = true,
            baseUrl = _settings.BaseUrl,
            elapsedMs,
            seenIp = who.Value,
            whitelistedIp = _settings.StaticIp,
            ipMatches = whitelisted,
            message = whitelisted
                ? $"The broker answered in {elapsedMs} ms and sees this server as {who.Value}."
                : $"The broker sees this server as {who.Value}, but the app is whitelisted for {_settings.StaticIp}: orders would be refused.",
        });
    }

    /// <summary>Sign in and read the account, to prove the credentials end to end.</summary>
    [HttpPost("test")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Test([FromBody] SimBrokerTestRequest? request, CancellationToken cancellationToken)
    {
        if (!_settings.IsConfigured)
            return Ok(new { ok = false, step = "configuration", message = "Set " + string.Join(", ", _settings.Missing()) + "." });

        var login = await _client.LoginAsync(force: request?.ForceLogin ?? false, cancellationToken);
        if (!login.Succeeded || login.Value is null)
            return Ok(new { ok = false, step = "login", code = login.ErrorCode, message = login.ErrorMessage });

        var funds = await _client.GetFundsAsync(cancellationToken);
        if (!funds.Succeeded || funds.Value is null)
            return Ok(new { ok = false, step = "funds", code = funds.ErrorCode, message = funds.ErrorMessage });

        var positions = await _client.GetPositionsAsync(cancellationToken);

        return Ok(new
        {
            ok = true,
            step = "done",
            clientId = login.Value.ClientId,
            sessionExpiresAt = login.Value.ExpiresAt,
            funds = funds.Value,
            positions = positions.Succeeded ? positions.Value : null,
            positionsMessage = positions.Succeeded ? null : positions.ErrorMessage,
            message = $"Signed in as {login.Value.ClientId}; ₹{funds.Value.Available:N2} available until {login.Value.ExpiresAt:HH:mm} on {login.Value.ExpiresAt:dd MMM}.",
        });
    }

    /// <summary>Forget the session, so the next call signs in again.</summary>
    [HttpPost("sign-out")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public IActionResult EndSession()
    {
        _client.ForgetSession();
        return Ok(new { message = "The simulated broker session was forgotten." });
    }

    // ---- Traders' accounts -------------------------------------------
    // One trader, one account at the broker. The platform keeps the link and
    // the credentials; the money, the orders and the positions are read from
    // the broker every time, so nothing here can show a balance it cached.

    /// <summary>Every trader who has an account, without any secret.</summary>
    [HttpGet("accounts")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> ListAccounts(CancellationToken cancellationToken)
        => Ok(new
        {
            canAdminister = _accounts.CanAdminister,
            accounts = await _accounts.ListAsync(cancellationToken),
        });

    /// <summary>One trader's account: money, positions, today's orders, and whether it is stopped.</summary>
    [HttpGet("accounts/{userId:long}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> GetAccount(long userId, [FromQuery] string? day, CancellationToken cancellationToken)
    {
        DateOnly? tradingDate = null;
        if (!string.IsNullOrWhiteSpace(day))
        {
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return BadRequest(new { message = "day must be yyyy-MM-dd (IST)." });
            tradingDate = parsed;
        }

        var snapshot = await _accounts.SnapshotAsync(userId, tradingDate, cancellationToken);
        if (snapshot.Succeeded) return Ok(new { linked = true, account = snapshot.Value });

        // A trader without an account is the ordinary case, not a failure: it
        // is what the page shows an "Open an account" button for. Returning 404
        // made the console retry it in a loop.
        return snapshot.ErrorCode == "NOT_LINKED"
            ? Ok(new { linked = false, account = (object?)null })
            : Failure(snapshot.ErrorCode, snapshot.ErrorMessage);
    }

    /// <summary>
    /// Opens an account at the broker for a trader, issues the app it signs in
    /// with, and pays the opening money in.
    /// </summary>
    /// <remarks>
    /// The broker shows the TOTP secret and the app secret once each; both are
    /// stored, encrypted, so the trader can be given them again. Nothing about
    /// this touches the trader's platform capital — that is a different number,
    /// about a different broker.
    /// </remarks>
    [HttpPost("accounts/{userId:long}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> IssueAccount(long userId, [FromBody] IssueSimBrokerAccountRequest? request, CancellationToken cancellationToken)
    {
        var user = await _users.GetAsync(userId, cancellationToken);
        if (user is null) return NotFound(new { message = "No such user." });

        decimal opening = request?.OpeningFunds ?? 0m;
        if (opening < 0) return BadRequest(new { message = "The opening balance cannot be negative." });

        string name = string.IsNullOrWhiteSpace(request?.Name) ? user.UserName : request!.Name!;
        var issued = await _accounts.IssueAsync(userId, name, opening, User.Identity?.Name ?? "admin", cancellationToken);
        return issued.Succeeded
            ? Ok(issued.Value)
            : Failure(issued.ErrorCode, issued.ErrorMessage);
    }

    /// <summary>Pays money into a trader's account, or out of it with a negative amount.</summary>
    [HttpPost("accounts/{userId:long}/funds")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> MoveFunds(long userId, [FromBody] SimBrokerFundsRequest request, CancellationToken cancellationToken)
    {
        var result = await _accounts.PayAsync(userId, request.Amount, request.Reference, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : Failure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Stops a trader's account at the broker, or starts it again. Stopping
    /// cancels every working order and, with <c>squareOff</c>, closes every
    /// position.
    /// </summary>
    [HttpPost("accounts/{userId:long}/kill-switch")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> SetKillSwitch(long userId, [FromBody] SimBrokerKillSwitchRequest request, CancellationToken cancellationToken)
    {
        var result = await _accounts.SetKillSwitchAsync(userId, request.Active, request.SquareOff, cancellationToken);
        return result.Succeeded ? Ok(result.Value) : Failure(result.ErrorCode, result.ErrorMessage);
    }

    /// <summary>
    /// Whether the platform offers this trader their account. The account at
    /// the broker is untouched: its money and positions are not something a
    /// checkbox should be able to destroy.
    /// </summary>
    [HttpPost("accounts/{userId:long}/enabled")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> SetEnabled(long userId, [FromBody] SimBrokerEnabledRequest request, CancellationToken cancellationToken)
        => await _accounts.SetEnabledAsync(userId, request.Enabled, cancellationToken)
            ? Ok(new { userId, enabled = request.Enabled })
            : NotFound(new { message = "This trader has no account at the simulated broker yet." });

    /// <summary>
    /// A trader's credentials, for handing back to them. Deliberately a POST
    /// and not part of any page's own data, so secrets are never carried by a
    /// request nobody asked for.
    /// </summary>
    [HttpPost("accounts/{userId:long}/credentials")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> RevealCredentials(long userId, CancellationToken cancellationToken)
    {
        var credentials = await _accounts.RevealAsync(userId, cancellationToken);
        return credentials is null
            ? NotFound(new { message = "This trader has no account at the simulated broker yet." })
            : Ok(credentials);
    }

    /// <summary>
    /// A refusal arrives as a refusal. The status says who said no — this
    /// platform, or the broker — and the body carries the broker's own code, so
    /// the console can show why instead of a blank page.
    /// </summary>
    /// <remarks>
    /// This used to answer 200 with <c>ok: false</c>. The console read that as
    /// success, said an account had been opened, and then could not find it:
    /// the one failure mode this project keeps having to fix is a state nobody
    /// knows yet rendered as a fact.
    /// </remarks>
    private IActionResult Failure(string? code, string? message) => code switch
    {
        "NOT_LINKED" => NotFound(new { code, message }),
        "ALREADY_LINKED" => Conflict(new { code, message }),

        // The platform is not set up to act as the broker's back office.
        "NOT_CONFIGURED" => StatusCode(StatusCodes.Status503ServiceUnavailable, new { code, message }),

        // Everything else came from the broker: it refused, or it could not be
        // reached. Either way this server is the wrong place to look.
        _ => StatusCode(StatusCodes.Status502BadGateway, new { code, message }),
    };
}

/// <summary>What an account is opened with.</summary>
public sealed class IssueSimBrokerAccountRequest
{
    /// <summary>The name on the account at the broker. Defaults to the trader's user name.</summary>
    public string? Name { get; set; }

    /// <summary>Rupees to pay in at once. Zero opens an account that cannot trade until it is funded.</summary>
    public decimal OpeningFunds { get; set; }
}

/// <summary>Money in, or out when the amount is negative.</summary>
public sealed class SimBrokerFundsRequest
{
    public decimal Amount { get; set; }

    /// <summary>What the ledger entry should say, for example "Top-up after the September review".</summary>
    public string? Reference { get; set; }
}

public sealed class SimBrokerKillSwitchRequest
{
    public bool Active { get; set; }

    /// <summary>Also close every open position, not only cancel the working orders.</summary>
    public bool SquareOff { get; set; }
}

public sealed class SimBrokerEnabledRequest
{
    public bool Enabled { get; set; }
}

/// <summary>Optional overrides for the connection test.</summary>
public sealed class SimBrokerTestRequest
{
    /// <summary>Sign in again even when a session is held. The broker refuses a TOTP code it has already seen, so this can only be done once every 30 seconds.</summary>
    public bool ForceLogin { get; set; }
}
