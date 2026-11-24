using System.Runtime.CompilerServices;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Users;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// A person changes their own password, knowing the current one.
/// </summary>
/// <remarks>
/// <para>
/// Its own controller because <see cref="UsersController"/> is AdminOnly at the
/// class, and an attribute on a method is added to that, not put in its place.
/// Until 2 Nov this action sat there, so a trader was refused and the documented
/// way to change a password worked for admins alone.
/// </para>
/// <para>
/// Not the Service account, which an admin-only class also kept out: its password
/// is the one in the desk's .env, and every runner signs in with it.
/// </para>
/// <para>
/// A stolen token must not be a way to guess the password behind it, and then
/// take the account by changing it: the attempts are counted per account, as well
/// as per address by the sign-in limiter.
/// </para>
/// </remarks>
[Authorize(Roles = $"{UserRoles.Admin},{UserRoles.Trader}")]
[ApiController]
[Route("api/Users/me/password")]
public class OwnPasswordController : ControllerBase
{
    /// <summary>Attempts one account has in <see cref="AttemptWindow"/>; the correct password ends the count.</summary>
    public const int MaxAttempts = 5;

    public static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(15);

    private readonly IUserAdminService _users;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;

    public OwnPasswordController(IUserAdminService users, IMemoryCache cache, IConfiguration configuration)
    {
        _users = users;
        _cache = cache;
        _configuration = configuration;
    }

    /// <summary>
    /// Whether the caller is the admin whose password the server's .env sets (Bootstrap:AdminPassword). That account's
    /// password cannot be changed here: every start puts the .env password back (AdminBootstrapper), and the desk's
    /// own scripts sign in with it, so a change would lock them out until the next start and then quietly undo itself.
    /// </summary>
    private bool IsConfiguredAdmin() =>
        !string.IsNullOrWhiteSpace(_configuration["Bootstrap:AdminPassword"])
        && User.IsAdmin()
        && string.Equals(User.GetUserName(), _configuration["Bootstrap:AdminUserName"]?.Trim() ?? "admin", StringComparison.Ordinal);

    /// <summary>
    /// Changes the password and ends every session of the account, this one
    /// included: whoever held the old password must not keep a session on it, and
    /// a request cannot say which refresh token is its own. The caller signs in
    /// again with the new one.
    /// </summary>
    [EnableRateLimiting(RateLimitPolicies.SignIn)]
    [HttpPost]
    public async Task<IActionResult> Change(
        [FromBody] ChangeOwnPasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { message = "The new password must be at least 8 characters." });
        }

        if (IsConfiguredAdmin())
        {
            return Conflict(new
            {
                message = "This admin's password is set by ADMIN_PASSWORD in the server's .env, and the desk's own scripts " +
                          "sign in with it. Change it there and restart the API.",
            });
        }

        long userId = User.GetRequiredUserId();
        string attemptsKey = $"own-password-attempts:{userId}";

        var attempts = _cache.GetOrCreate(attemptsKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = AttemptWindow;
            return new StrongBox<int>();
        })!;

        if (Interlocked.Increment(ref attempts.Value) > MaxAttempts)
        {
            Response.Headers.RetryAfter = ((int)AttemptWindow.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"Too many attempts. Try again in {AttemptWindow.TotalMinutes:0} minutes.",
            });
        }

        try
        {
            await _users.ChangeOwnPasswordAsync(userId, request.CurrentPassword, request.NewPassword, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }

        _cache.Remove(attemptsKey);
        return Ok(new { message = "Password changed. Every session of this account has been signed out; sign in again." });
    }
}
