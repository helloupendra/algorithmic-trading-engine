using System.Security.Cryptography;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Guarantees exactly one usable administrator exists so the platform can be
/// bootstrapped, without ever shipping a well-known default credential.
///
/// On startup:
///   - if any Admin already exists, does nothing;
///   - otherwise creates one from Bootstrap:Admin* configuration;
///   - if no password is configured, generates a strong one and logs it ONCE.
///
/// The generated password is logged deliberately: it is the only moment it is
/// recoverable, it appears only on the operator's own console, and the
/// alternative — a hardcoded default — is far worse.
/// </summary>
public class AdminBootstrapper
{
    private readonly TradingDbContext _dbContext;
    private readonly PasswordHasher<AppUser> _passwordHasher;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminBootstrapper> _logger;

    public AdminBootstrapper(
        TradingDbContext dbContext,
        PasswordHasher<AppUser> passwordHasher,
        IConfiguration configuration,
        ILogger<AdminBootstrapper> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task EnsureAdminAsync(CancellationToken cancellationToken = default)
    {
        string userName = _configuration["Bootstrap:AdminUserName"]?.Trim() ?? "admin";
        string email = _configuration["Bootstrap:AdminEmail"]?.Trim().ToLowerInvariant()
                       ?? "admin@localhost";
        string? configuredPassword = _configuration["Bootstrap:AdminPassword"];

        bool adminExists = await _dbContext.AppUsers
            .AnyAsync(x => x.Role == UserRoles.Admin && x.IsActive, cancellationToken);

        if (adminExists)
        {
            // An admin is already provisioned. When ADMIN_PASSWORD is explicitly
            // set, keep the stored hash in step with it — that is the recovery path
            // if the one-time generated password was lost, and it lets an operator
            // pin a chosen password without touching the database. Leaving
            // ADMIN_PASSWORD empty never modifies an existing account.
            if (!string.IsNullOrWhiteSpace(configuredPassword))
            {
                await SyncConfiguredAdminPasswordAsync(userName, configuredPassword, cancellationToken);
            }
            return;
        }

        bool generated = string.IsNullOrWhiteSpace(configuredPassword);
        string password = generated ? GeneratePassword() : configuredPassword!;

        // An account may already exist under this name with a non-admin role or no
        // password (for example one created by the users.json seed). Promote it
        // rather than failing on the unique-username index.
        var existing = await _dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.UserName.ToLower() == userName.ToLower(), cancellationToken);

        if (existing is not null)
        {
            existing.Role = UserRoles.Admin;
            existing.IsActive = true;
            existing.UpdatedUtc = DateTime.UtcNow;

            if (string.IsNullOrEmpty(existing.PasswordHash))
            {
                existing.PasswordHash = _passwordHasher.HashPassword(existing, password);
            }
            else
            {
                // Never silently overwrite a password the operator already set.
                generated = false;
                password = "(unchanged)";
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogWarning("Promoted existing user '{UserName}' to Admin.", existing.UserName);
        }
        else
        {
            var admin = new AppUser
            {
                UserName = userName,
                Email = email,
                Role = UserRoles.Admin,
                IsActive = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            admin.PasswordHash = _passwordHasher.HashPassword(admin, password);

            await _dbContext.AppUsers.AddAsync(admin, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        if (generated)
        {
            _logger.LogWarning(
                "\n" +
                "==============================================================\n" +
                "  ADMIN ACCOUNT CREATED\n" +
                "    username : {UserName}\n" +
                "    password : {Password}\n" +
                "\n" +
                "  This password is shown ONCE and is not recoverable.\n" +
                "  Store it now, then set Bootstrap:AdminPassword in .env\n" +
                "  (or change it from the admin panel) to pin it.\n" +
                "==============================================================",
                userName, password);
        }
        else
        {
            _logger.LogInformation("Admin account '{UserName}' ensured.", userName);
        }
    }

    /// <summary>
    /// Brings the configured admin account's password in line with
    /// Bootstrap:AdminPassword, creating the account if the configured username
    /// does not yet exist (for example when a different account holds Admin).
    /// </summary>
    private async Task SyncConfiguredAdminPasswordAsync(
        string userName, string password, CancellationToken cancellationToken)
    {
        var account = await _dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.UserName.ToLower() == userName.ToLower(), cancellationToken);

        if (account is null)
        {
            account = new AppUser
            {
                UserName = userName,
                Email = _configuration["Bootstrap:AdminEmail"]?.Trim().ToLowerInvariant()
                        ?? $"{userName}@localhost",
                Role = UserRoles.Admin,
                IsActive = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            account.PasswordHash = _passwordHasher.HashPassword(account, password);
            await _dbContext.AppUsers.AddAsync(account, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogWarning("Created additional admin '{UserName}' from configuration.", userName);
            return;
        }

        bool changed = false;

        if (account.Role != UserRoles.Admin)
        {
            account.Role = UserRoles.Admin;
            changed = true;
        }

        if (!account.IsActive)
        {
            account.IsActive = true;
            changed = true;
        }

        var check = _passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password);
        if (check == PasswordVerificationResult.Failed || string.IsNullOrEmpty(account.PasswordHash))
        {
            account.PasswordHash = _passwordHasher.HashPassword(account, password);
            changed = true;
            _logger.LogWarning(
                "Reset admin '{UserName}' password from Bootstrap:AdminPassword.", userName);
        }

        if (changed)
        {
            account.UpdatedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Ensures the Python engine's service account exists.
    ///
    /// The engine is a machine client: it authenticates as a normal Trader account
    /// rather than being exempted from authorization, so its access is bounded by
    /// the same rules as any other trader and can be revoked by deactivating it.
    /// </summary>
    public async Task EnsureServiceAccountAsync(CancellationToken cancellationToken = default)
    {
        string userName = _configuration["Bootstrap:ServiceUserName"]?.Trim() ?? "engine-service";
        string? password = _configuration["Bootstrap:ServicePassword"];

        if (string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning(
                "ENGINE_SERVICE_PASSWORD is not set, so the '{UserName}' account was not " +
                "provisioned. The Python engine cannot call the API until you set it in .env " +
                "and restart.", userName);
            return;
        }

        var existing = await _dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.UserName.ToLower() == userName.ToLower(), cancellationToken);

        if (existing is null)
        {
            var account = new AppUser
            {
                UserName = userName,
                Email = $"{userName}@localhost",
                Role = UserRoles.Trader,
                IsActive = true,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            account.PasswordHash = _passwordHasher.HashPassword(account, password);

            await _dbContext.AppUsers.AddAsync(account, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Created engine service account '{UserName}'.", userName);
            return;
        }

        // Keep the stored hash in step with .env, so rotating the password there is
        // all that is required.
        var check = _passwordHasher.VerifyHashedPassword(existing, existing.PasswordHash, password);
        if (check == PasswordVerificationResult.Failed)
        {
            existing.PasswordHash = _passwordHasher.HashPassword(existing, password);
            existing.UpdatedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated engine service account '{UserName}' password from configuration.", userName);
        }
    }

    /// <summary>
    /// 24 URL-safe random characters, well beyond the 8-character minimum enforced
    /// on registration.
    /// </summary>
    private static string GeneratePassword()
    {
        const string alphabet =
            "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789-_";
        var chars = new char[24];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }
        return new string(chars);
    }
}
