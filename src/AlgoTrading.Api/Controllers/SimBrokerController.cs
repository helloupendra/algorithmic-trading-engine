using AlgoTrading.Api.Security;
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
    private readonly SimBrokerSettings _settings;

    public SimBrokerController(SimBrokerClient client, IOptions<SimBrokerSettings> settings)
    {
        _client = client;
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
}

/// <summary>Optional overrides for the connection test.</summary>
public sealed class SimBrokerTestRequest
{
    /// <summary>Sign in again even when a session is held. The broker refuses a TOTP code it has already seen, so this can only be done once every 30 seconds.</summary>
    public bool ForceLogin { get; set; }
}
