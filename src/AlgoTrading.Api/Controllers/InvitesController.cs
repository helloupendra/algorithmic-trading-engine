using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Auth;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Invitations: an admin decides who may join, and the person sets their own
/// password.
/// </summary>
/// <remarks>
/// Signing up is not open. What makes invites safe is not the invite but what it
/// creates — a Trader with no module grants and no strategy package, which can
/// sign in and do nothing until an admin says otherwise.
/// <para>
/// Only the token's hash is stored. The plaintext is returned once, at creation,
/// so a database dump cannot hand anyone a working invite.
/// </para>
/// </remarks>
[ApiController]
[Route("api/Invites")]
public class InvitesController : ControllerBase
{
    private const int DefaultValidDays = 7;

    private readonly TradingDbContext _dbContext;
    private readonly IAuthService _authService;
    private readonly IUserAdminService _users;
    private readonly ILogger<InvitesController> _logger;

    public InvitesController(
        TradingDbContext dbContext,
        IAuthService authService,
        IUserAdminService users,
        ILogger<InvitesController> logger)
    {
        _dbContext = dbContext;
        _authService = authService;
        _users = users;
        _logger = logger;
    }

    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public record CreateInviteRequest(
        string Email,
        string? SuggestedUserName,
        IReadOnlyList<string>? ModuleKeys,
        long? StrategyPackageId,
        int? ValidDays);

    /// <summary>Creates an invite and returns its link. The token is shown only now.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateInviteRequest request,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            return BadRequest(new { message = "A valid email is required." });
        }

        if (await _dbContext.AppUsers.AnyAsync(x => x.Email.ToLower() == email, cancellationToken))
        {
            return BadRequest(new { message = $"An account already exists for {email}." });
        }

        var moduleKeys = (request.ModuleKeys ?? Array.Empty<string>())
            .Select(x => x?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct()
            .ToList();

        foreach (var key in moduleKeys)
        {
            if (!PlatformModules.IsKnown(key))
            {
                return BadRequest(new { message = $"'{key}' is not a module." });
            }
        }

        if (request.StrategyPackageId is long packageId &&
            !await _dbContext.StrategyPackages.AnyAsync(x => x.Id == packageId, cancellationToken))
        {
            return BadRequest(new { message = $"No strategy package with id {packageId}." });
        }

        // 32 random bytes, url-safe. Long enough that guessing is not a strategy.
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

        int validDays = request.ValidDays is > 0 and <= 90 ? request.ValidDays.Value : DefaultValidDays;

        var invite = new UserInvite
        {
            TokenHash = Hash(token),
            Email = email,
            SuggestedUserName = (request.SuggestedUserName ?? email.Split('@')[0]).Trim(),
            ModuleKeysCsv = string.Join(",", moduleKeys),
            StrategyPackageId = request.StrategyPackageId,
            CreatedBy = User.GetUserName() ?? "admin",
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(validDays),
        };

        _dbContext.UserInvites.Add(invite);
        await _dbContext.SaveChangesAsync(cancellationToken);

        string frontend = configuration["Frontend:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:5173";

        _logger.LogInformation("Invite for {Email} created by {Actor}.", email, invite.CreatedBy);

        return Ok(new
        {
            invite.Id,
            invite.Email,
            invite.ExpiresUtc,
            Link = $"{frontend}/invite/{token}",
            Message = "Copy the link now — it is not shown again, and the token is not recoverable.",
        });
    }

    /// <summary>Every invite, with its state. Tokens are never returned.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var invites = await _dbContext.UserInvites
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return Ok(invites.Select(x => new
        {
            x.Id,
            x.Email,
            x.SuggestedUserName,
            ModuleKeys = x.ModuleKeysCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            x.StrategyPackageId,
            x.CreatedBy,
            x.CreatedUtc,
            x.ExpiresUtc,
            x.AcceptedUtc,
            x.RevokedUtc,
            Status = x.AcceptedUtc is not null
                ? "accepted"
                : x.RevokedUtc is not null
                    ? "revoked"
                    : x.ExpiresUtc <= now
                        ? "expired"
                        : "pending",
        }));
    }

    /// <summary>Cancels an invite that has not been used.</summary>
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    [HttpPost("{id:long}/revoke")]
    public async Task<IActionResult> Revoke(long id, CancellationToken cancellationToken)
    {
        var invite = await _dbContext.UserInvites.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (invite is null) return NotFound(new { message = $"No invite with id {id}." });

        if (invite.AcceptedUtc is not null)
        {
            return BadRequest(new { message = "That invite has already been used." });
        }

        invite.RevokedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = $"Invite for {invite.Email} revoked." });
    }

    /// <summary>
    /// What the invitee sees before accepting: enough to know the invite is real,
    /// and nothing about the platform's contents.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{token}")]
    public async Task<IActionResult> Preview(string token, CancellationToken cancellationToken)
    {
        var invite = await _dbContext.UserInvites
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == Hash(token), cancellationToken);

        // One message for every failure: a different answer for "expired" versus
        // "never existed" would let someone probe for valid tokens.
        if (invite is null || !invite.IsUsable(DateTime.UtcNow))
        {
            return NotFound(new { message = "This invitation is not valid. Ask for a new one." });
        }

        return Ok(new { invite.Email, invite.SuggestedUserName, invite.ExpiresUtc });
    }

    public record AcceptInviteRequest(string UserName, string Password);

    /// <summary>
    /// Creates the account. The invitee chooses their own password; it never
    /// passes through the admin.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("{token}/accept")]
    public async Task<IActionResult> Accept(
        string token,
        [FromBody] AcceptInviteRequest request,
        CancellationToken cancellationToken)
    {
        var invite = await _dbContext.UserInvites
            .FirstOrDefaultAsync(x => x.TokenHash == Hash(token), cancellationToken);

        if (invite is null || !invite.IsUsable(DateTime.UtcNow))
        {
            return NotFound(new { message = "This invitation is not valid. Ask for a new one." });
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return BadRequest(new { message = "Choose a password of at least 8 characters." });
        }

        string userName = (request.UserName ?? invite.SuggestedUserName).Trim();

        if (string.IsNullOrWhiteSpace(userName))
        {
            return BadRequest(new { message = "A username is required." });
        }

        AuthResponse created;

        try
        {
            created = await _authService.RegisterAsync(
                new RegisterRequest
                {
                    UserName = userName,
                    Email = invite.Email,
                    Password = request.Password,
                },
                cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // A taken username is the invitee's problem to fix, not a server fault.
            return BadRequest(new { message = ex.Message });
        }

        var account = await _dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.Email.ToLower() == invite.Email, cancellationToken);

        if (account is not null)
        {
            // Whatever the admin chose when inviting. Both may be empty, and that
            // is the safe default: the account exists and can do nothing yet.
            var moduleKeys = invite.ModuleKeysCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (moduleKeys.Count > 0)
            {
                await _users.SetGrantsAsync(account.Id, moduleKeys, invite.CreatedBy, cancellationToken);
            }

            if (invite.StrategyPackageId is not null)
            {
                account.StrategyPackageId = invite.StrategyPackageId;
            }

            invite.AcceptedUserId = account.Id;
        }

        invite.AcceptedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Invite for {Email} accepted as {UserName}.", invite.Email, userName);

        return Ok(created);
    }
}
