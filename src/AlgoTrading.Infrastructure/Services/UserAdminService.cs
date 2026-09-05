using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Users;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Account administration and the module-grant check.
/// </summary>
public class UserAdminService : IUserAdminService
{
    private readonly TradingDbContext _dbContext;
    private readonly PasswordHasher<AppUser> _passwordHasher;
    private readonly ITokenValidityService _tokenValidity;
    private readonly ILogger<UserAdminService> _logger;

    public UserAdminService(
        TradingDbContext dbContext,
        PasswordHasher<AppUser> passwordHasher,
        ITokenValidityService tokenValidity,
        ILogger<UserAdminService> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _tokenValidity = tokenValidity;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UserAdminResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        var users = await _dbContext.AppUsers
            .AsNoTracking()
            .Include(x => x.ModuleGrants)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;

        var sessionCounts = await _dbContext.UserRefreshTokens
            .AsNoTracking()
            .Where(x => x.RevokedUtc == null && x.ExpiresUtc > now)
            .GroupBy(x => x.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count, cancellationToken);

        return users.Select(u => Map(u, sessionCounts.GetValueOrDefault(u.Id))).ToList();
    }

    public async Task<UserAdminResponse?> GetAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .Include(x => x.ModuleGrants)
            .Include(x => x.StrategyGrants)
            .Include(x => x.StrategyPackage)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

        if (user is null) return null;

        var now = DateTime.UtcNow;
        int sessions = await _dbContext.UserRefreshTokens
            .CountAsync(x => x.UserId == userId && x.RevokedUtc == null && x.ExpiresUtc > now, cancellationToken);

        return Map(user, sessions);
    }

    public async Task<UserAdminResponse> UpdateAsync(
        long userId,
        UpdateUserRequest request,
        long actingUserId,
        string actingUserName,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .Include(x => x.ModuleGrants)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"No account with id {userId}.");

        if (request.Role is not null)
        {
            string role = UserRoles.Normalize(request.Role)
                ?? throw new InvalidOperationException(
                    $"'{request.Role}' is not a role. Expected one of: {string.Join(", ", UserRoles.All)}.");

            // Locking yourself out is not a decision anyone makes on purpose.
            if (userId == actingUserId && !string.Equals(role, UserRoles.Admin, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("You cannot remove your own admin role.");
            }

            await GuardLastAdminAsync(user, role, user.IsActive, cancellationToken);

            user.Role = role;
        }

        if (request.IsActive is not null)
        {
            if (userId == actingUserId && request.IsActive == false)
            {
                throw new InvalidOperationException("You cannot disable your own account.");
            }

            await GuardLastAdminAsync(user, user.Role, request.IsActive.Value, cancellationToken);

            user.IsActive = request.IsActive.Value;

            if (!user.IsActive)
            {
                // A disabled account must stop being signed in, not merely stop
                // being able to sign in again.
                await RevokeSessionsAsync(userId, cancellationToken);
            }
        }

        if (request.TotalCapital is not null)
        {
            if (request.TotalCapital < 0)
            {
                throw new InvalidOperationException("Capital cannot be negative.");
            }

            user.TotalCapital = request.TotalCapital.Value;
        }

        if (request.StrategyPackageId is not null)
        {
            // -1 means "no package", which means no strategies at all.
            long? packageId = request.StrategyPackageId.Value < 0 ? null : request.StrategyPackageId.Value;

            if (packageId is not null &&
                !await _dbContext.StrategyPackages.AnyAsync(x => x.Id == packageId, cancellationToken))
            {
                throw new InvalidOperationException($"No strategy package with id {packageId}.");
            }

            user.StrategyPackageId = packageId;
        }

        if (request.MaxConcurrentRuns is not null)
        {
            // -1 is the console's way of saying "clear the override".
            user.MaxConcurrentRuns = request.MaxConcurrentRuns.Value < 0
                ? null
                : request.MaxConcurrentRuns.Value;
        }

        user.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Account {UserName} updated by {Actor}.", user.UserName, actingUserName);

        return (await GetAsync(userId, cancellationToken))!;
    }

    /// <summary>
    /// Refuses a change that would leave the platform with no active admin. It is
    /// recoverable only by editing the database by hand, so it must not happen.
    /// </summary>
    private async Task GuardLastAdminAsync(
        AppUser user,
        string newRole,
        bool newIsActive,
        CancellationToken cancellationToken)
    {
        bool wasActiveAdmin = user.IsActive && string.Equals(user.Role, UserRoles.Admin, StringComparison.Ordinal);
        bool willBeActiveAdmin = newIsActive && string.Equals(newRole, UserRoles.Admin, StringComparison.Ordinal);

        if (!wasActiveAdmin || willBeActiveAdmin) return;

        int otherAdmins = await _dbContext.AppUsers
            .CountAsync(x => x.Id != user.Id && x.IsActive && x.Role == UserRoles.Admin, cancellationToken);

        if (otherAdmins == 0)
        {
            throw new InvalidOperationException(
                "This is the only active admin. Promote someone else first, or nobody could administer the platform.");
        }
    }

    public async Task<UserAdminResponse> SetGrantsAsync(
        long userId,
        IReadOnlyList<string> moduleKeys,
        string actingUserName,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .Include(x => x.ModuleGrants)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"No account with id {userId}.");

        var wanted = moduleKeys
            .Select(k => k?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(k => k.Length > 0)
            .Distinct()
            .ToList();

        foreach (var key in wanted)
        {
            if (!PlatformModules.IsKnown(key))
            {
                throw new InvalidOperationException(
                    $"'{key}' is not a module. Expected one of: {string.Join(", ", PlatformModules.All.Select(m => m.Key))}.");
            }
        }

        var existing = user.ModuleGrants.ToList();

        foreach (var grant in existing.Where(g => !wanted.Contains(g.ModuleKey, StringComparer.OrdinalIgnoreCase)))
        {
            _dbContext.UserModuleGrants.Remove(grant);
        }

        var now = DateTime.UtcNow;

        foreach (var key in wanted.Where(k => !existing.Any(g => string.Equals(g.ModuleKey, k, StringComparison.OrdinalIgnoreCase))))
        {
            _dbContext.UserModuleGrants.Add(new UserModuleGrant
            {
                UserId = userId,
                ModuleKey = key,
                GrantedBy = actingUserName,
                GrantedUtc = now,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Grants for {UserName} set to [{Grants}] by {Actor}.",
            user.UserName, string.Join(", ", wanted), actingUserName);

        return (await GetAsync(userId, cancellationToken))!;
    }

    public async Task ResetPasswordAsync(
        long userId,
        string newPassword,
        string actingUserName,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"No account with id {userId}.");

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        user.UpdatedUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Whoever held the old password must not keep a live session on it.
        await RevokeSessionsAsync(userId, cancellationToken);

        _logger.LogInformation("Password for {UserName} reset by {Actor}.", user.UserName, actingUserName);
    }

    public async Task ChangeOwnPasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException("Account not found.");

        var verified = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword);

        if (verified == PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("The current password is not correct.");
        }

        user.PasswordHash = _passwordHasher.HashPassword(user, newPassword);
        user.UpdatedUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RevokeSessionsAsync(long userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var live = await _dbContext.UserRefreshTokens
            .Where(x => x.UserId == userId && x.RevokedUtc == null && x.ExpiresUtc > now)
            .ToListAsync(cancellationToken);

        foreach (var token in live)
        {
            token.RevokedUtc = now;
        }

        if (live.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Refresh tokens are only half of it: without this the access token the
        // account already holds keeps working until it expires.
        await _tokenValidity.InvalidateExistingTokensAsync(userId, cancellationToken);

        return live.Count;
    }

    public async Task<bool> IsModuleAllowedAsync(
        long userId,
        string moduleKey,
        CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.Role, x.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null || !user.IsActive) return false;

        // Admins have every module by definition. A Service account is the
        // platform's own machine user — the Python engine — provisioned by the
        // operator; it is not a person and has no grants to hold. It still cannot
        // reach anything behind the AdminOnly policy.
        if (string.Equals(user.Role, UserRoles.Admin, StringComparison.Ordinal) ||
            string.Equals(user.Role, UserRoles.Service, StringComparison.Ordinal))
        {
            return true;
        }

        return await _dbContext.UserModuleGrants
            .AsNoTracking()
            .AnyAsync(x => x.UserId == userId && x.ModuleKey == moduleKey, cancellationToken);
    }

    private static UserAdminResponse Map(AppUser user, int sessions) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        Email = user.Email,
        Role = user.Role,
        IsActive = user.IsActive,
        TotalCapital = user.TotalCapital,
        MaxConcurrentRuns = user.MaxConcurrentRuns,
        ModuleGrants = user.ModuleGrants.Select(g => g.ModuleKey).OrderBy(x => x).ToList(),
        StrategyPackageId = user.StrategyPackageId,
        StrategyPackageName = user.StrategyPackage?.Name,
        StrategyGrants = user.StrategyGrants.Select(g => g.StrategyName).OrderBy(x => x).ToList(),
        ActiveSessions = sessions,
        CreatedUtc = user.CreatedUtc,
        LastLoginUtc = user.LastLoginUtc,
    };
}
