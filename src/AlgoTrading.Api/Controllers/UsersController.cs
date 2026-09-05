using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Users;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Accounts: who exists, what they may do, and how much rope they have.
/// </summary>
/// <remarks>
/// Admin-only, except <c>me/password</c>, which is how anyone changes their own.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/Users")]
public class UsersController : ControllerBase
{
    private readonly IUserAdminService _users;

    public UsersController(IUserAdminService users)
    {
        _users = users;
    }

    /// <summary>The modules a trader can be granted.</summary>
    [HttpGet("modules")]
    public ActionResult<IReadOnlyList<PlatformModuleResponse>> GetModules()
        => Ok(PlatformModules.All.Select(m => new PlatformModuleResponse
        {
            Key = m.Key,
            Name = m.Name,
            Description = m.Description,
        }).ToList());

    /// <summary>The roles an account can hold.</summary>
    [HttpGet("roles")]
    public ActionResult<IReadOnlyList<string>> GetRoles() => Ok(UserRoles.All);

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserAdminResponse>>> List(CancellationToken cancellationToken)
        => Ok(await _users.ListAsync(cancellationToken));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id, CancellationToken cancellationToken)
    {
        var user = await _users.GetAsync(id, cancellationToken);
        return user is null ? NotFound(new { message = $"No account with id {id}." }) : Ok(user);
    }

    [HttpPatch("{id:long}")]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] UpdateUserRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _users.UpdateAsync(
                id,
                request,
                User.GetRequiredUserId(),
                User.GetUserName() ?? "admin",
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            // Refusing to strand the platform without an admin is a fact about the
            // request, not a server fault.
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id:long}/grants")]
    public async Task<IActionResult> SetGrants(
        long id,
        [FromBody] SetGrantsRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _users.SetGrantsAsync(
                id,
                request.ModuleKeys,
                User.GetUserName() ?? "admin",
                cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:long}/password")]
    public async Task<IActionResult> ResetPassword(
        long id,
        [FromBody] ResetPasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { message = "The new password must be at least 8 characters." });
        }

        try
        {
            await _users.ResetPasswordAsync(
                id,
                request.NewPassword,
                User.GetUserName() ?? "admin",
                cancellationToken);

            return Ok(new { message = "Password reset. Every session for that account has been signed out." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:long}/revoke-sessions")]
    public async Task<IActionResult> RevokeSessions(long id, CancellationToken cancellationToken)
    {
        int revoked = await _users.RevokeSessionsAsync(id, cancellationToken);
        return Ok(new { message = $"Signed out {revoked} session(s)." });
    }

    /// <summary>Anyone may change their own password, knowing the current one.</summary>
    [Authorize]
    [HttpPost("me/password")]
    public async Task<IActionResult> ChangeOwnPassword(
        [FromBody] ChangeOwnPasswordRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { message = "The new password must be at least 8 characters." });
        }

        try
        {
            await _users.ChangeOwnPasswordAsync(
                User.GetRequiredUserId(),
                request.CurrentPassword,
                request.NewPassword,
                cancellationToken);

            return Ok(new { message = "Password changed." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
