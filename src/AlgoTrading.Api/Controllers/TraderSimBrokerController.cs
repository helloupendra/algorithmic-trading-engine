using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Infrastructure.Providers.SimBroker;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// A trader's own account at the simulated broker: what it holds, what it did
/// today, and the credentials to call it from their own code.
/// </summary>
/// <remarks>
/// Every route here reads the signed-in user's id from their token and never
/// takes one from the caller, so a trader can only ever see their own account.
/// Issuing, funding and stopping an account are the admin's, in
/// <see cref="SimBrokerController"/>.
/// </remarks>
[Authorize]
[ApiController]
[Route("api/Trader/simbroker")]
public class TraderSimBrokerController : ControllerBase
{
    private readonly SimBrokerAccountService _accounts;

    public TraderSimBrokerController(SimBrokerAccountService accounts)
    {
        _accounts = accounts;
    }

    /// <summary>
    /// The account: money, positions, the day's orders, and whether it is
    /// stopped. A trader with no account gets an answer saying so, not a 404 —
    /// the page has something to show either way.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? day, CancellationToken cancellationToken)
    {
        DateOnly? tradingDate = null;
        if (!string.IsNullOrWhiteSpace(day))
        {
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                return BadRequest(new { message = "day must be yyyy-MM-dd (IST)." });
            tradingDate = parsed;
        }

        long userId = User.GetRequiredUserId();
        var link = await _accounts.FindAsync(userId, cancellationToken);
        if (link is null || !link.IsEnabled)
            return Ok(new
            {
                linked = false,
                message = "You do not have an account at the simulated broker yet. An administrator opens one for you.",
            });

        var snapshot = await _accounts.SnapshotAsync(userId, tradingDate, cancellationToken);
        return snapshot.Succeeded
            ? Ok(new { linked = true, account = snapshot.Value })
            : Ok(new { linked = true, account = (object?)null, code = snapshot.ErrorCode, message = snapshot.ErrorMessage });
    }

    /// <summary>
    /// The credentials this account signs in with: client id, app id, app
    /// secret, and the TOTP secret for an authenticator app.
    /// </summary>
    /// <remarks>
    /// A POST rather than part of the page above, so secrets travel only when
    /// someone has asked for them. The broker showed each of these once; the
    /// platform stores them encrypted precisely so a trader who loses them is
    /// not left with an account they can never sign in to again.
    /// </remarks>
    [HttpPost("credentials")]
    public async Task<IActionResult> Credentials(CancellationToken cancellationToken)
    {
        long userId = User.GetRequiredUserId();
        var link = await _accounts.FindAsync(userId, cancellationToken);
        if (link is null || !link.IsEnabled)
            return NotFound(new { message = "You do not have an account at the simulated broker." });

        var credentials = await _accounts.RevealAsync(userId, cancellationToken);
        return credentials is null
            ? NotFound(new { message = "You do not have an account at the simulated broker." })
            : Ok(credentials);
    }
}
