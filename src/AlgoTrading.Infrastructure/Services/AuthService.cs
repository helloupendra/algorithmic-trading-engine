// src/AlgoTrading.Infrastructure/Services/AuthService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Application.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Auth;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AlgoTrading.Infrastructure.Services;

public class AuthService : IAuthService
{
    private readonly TradingDbContext _dbContext;
    private readonly PasswordHasher<AppUser> _passwordHasher;
    private readonly JwtOptions _jwtOptions;
    private readonly ILiveConnectionCloser? _connections;
    private readonly TimeProvider _time;
    public static readonly TimeSpan RefreshRaceGrace = TimeSpan.FromSeconds(10);

    public AuthService(
        TradingDbContext dbContext,
        PasswordHasher<AppUser> passwordHasher,
        IOptions<JwtOptions> jwtOptions,
        ILiveConnectionCloser? connections = null,
        TimeProvider? time = null)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _jwtOptions = jwtOptions.Value;
        _connections = connections;
        _time = time ?? TimeProvider.System;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRegisterRequest(request);

        string userName = request.UserName.Trim();
        string email = request.Email.Trim().ToLowerInvariant();

        // Taken in any case. The unique index on the column is case-sensitive, and
        // until 2 Nov an invitee could accept as "ADMIN" beside "admin"; people,
        // scripts and the morning job read names in any case, so two accounts
        // that differ only in case are never created. Sign-in stays exact.
        string folded = userName.ToLowerInvariant();
        bool userNameExists = await _dbContext.AppUsers.AnyAsync(x => x.UserName.ToLower() == folded, cancellationToken);
        if (userNameExists)
            throw new InvalidOperationException("Username is already taken.");

        bool emailExists = await _dbContext.AppUsers.AnyAsync(x => x.Email == email, cancellationToken);
        if (emailExists)
            throw new InvalidOperationException("Email is already registered.");

        var user = new AppUser
        {
            UserName = userName,
            Email = email,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        await _dbContext.AppUsers.AddAsync(user, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await using var transaction = await AuthTransaction.BeginAsync(_dbContext, user.Id, cancellationToken);
        var response = await CreateAuthResponseAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserNameOrEmail))
            throw new InvalidOperationException("Username or email is required.");

        if (string.IsNullOrWhiteSpace(request.Password))
            throw new InvalidOperationException("Password is required.");

        string value = request.UserNameOrEmail.Trim();
        string valueLower = value.ToLowerInvariant();

        var user = await _dbContext.AppUsers.AsNoTracking()
            .FirstOrDefaultAsync(x =>
                x.UserName == value ||
                x.Email == valueLower,
                cancellationToken);

        if (user is null || !user.IsActive)
            throw new InvalidOperationException("Invalid credentials.");

        await using var transaction = await AuthTransaction.BeginAsync(_dbContext, user.Id, cancellationToken);
        user = await _dbContext.AppUsers.SingleAsync(x => x.Id == user.Id, cancellationToken);
        await _dbContext.Entry(user).ReloadAsync(cancellationToken);
        if (!user.IsActive) throw new InvalidOperationException("Invalid credentials.");
        var verifyResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verifyResult == PasswordVerificationResult.Failed)
            throw new InvalidOperationException("Invalid credentials.");

        user.LastLoginUtc = DateTime.UtcNow;
        user.UpdatedUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var response = await CreateAuthResponseAsync(user, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken)) throw Refused();
        string hash = ComputeSha256(request.RefreshToken.Trim());
        var owner = await _dbContext.UserRefreshTokens.AsNoTracking()
            .Where(x => x.TokenHash == hash).Select(x => new { x.UserId, x.SessionId }).SingleOrDefaultAsync(cancellationToken);
        // Pre-migration tokens have no family and cannot mint a session-bound JWT.
        if (owner?.SessionId is null) throw Refused();

        await using var transaction = await AuthTransaction.BeginAsync(_dbContext, owner.UserId, cancellationToken);
        var existing = await _dbContext.UserRefreshTokens.Include(x => x.User).Include(x => x.Session)
            .SingleAsync(x => x.TokenHash == hash, cancellationToken);
        await _dbContext.Entry(existing).ReloadAsync(cancellationToken);
        if (existing.User is not null) await _dbContext.Entry(existing.User).ReloadAsync(cancellationToken);
        if (existing.Session is not null) await _dbContext.Entry(existing.Session).ReloadAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var session = existing.Session;
        if (session is null || session.UserId != existing.UserId || existing.User is null || !existing.User.IsActive || session.RevokedUtc != null || session.ExpiresUtc <= now ||
            (existing.User.TokensValidFromUtc is { } cutoff && session.CreatedUtc <= cutoff)) throw Refused();

        if (existing.RevokedUtc is { } consumed)
        {
            if (existing.ReplacedByTokenHash is not null && now - consumed < RefreshRaceGrace)
                throw new RefreshTokenException("refresh_already_used", "This refresh was already completed. Use the current session credentials.", retryable: true);
            await RevokeFamilyAsync(session, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _connections?.CloseConnectionsOfSession(session.Id);
            throw new RefreshTokenException("refresh_reused", "An old refresh token was reused. This session has ended; sign in again.");
        }
        if (existing.ExpiresUtc <= now) throw Refused();

        string raw = GenerateSecureToken();
        existing.RevokedUtc = now;
        existing.ReplacedByTokenHash = ComputeSha256(raw);
        // Release the unique active-token slot before inserting its successor.
        // Both saves share the transaction; a failure restores the original parent.
        await _dbContext.SaveChangesAsync(cancellationToken);
        session.ExpiresUtc = now.AddDays(_jwtOptions.RefreshTokenDays);
        _dbContext.UserRefreshTokens.Add(new UserRefreshToken
        {
            UserId = existing.UserId, SessionId = session.Id, TokenHash = existing.ReplacedByTokenHash,
            CreatedUtc = now, ExpiresUtc = session.ExpiresUtc,
        });
        var response = ResponseFor(existing.User, session.Id, raw);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task LogoutAsync(LogoutRequest request, long? callerUserId = null, Guid? callerSessionId = null, CancellationToken cancellationToken = default)
    {
        long userId;
        Guid sessionId;
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            string hash = ComputeSha256(request.RefreshToken.Trim());
            var token = await _dbContext.UserRefreshTokens.AsNoTracking().Where(x => x.TokenHash == hash)
                .Select(x => new { x.UserId, x.SessionId }).SingleOrDefaultAsync(cancellationToken);
            if (token?.SessionId is not Guid id) return; // Idempotent; do not disclose whether a credential exists.
            if ((callerUserId.HasValue && callerUserId != token.UserId) || (callerSessionId.HasValue && callerSessionId != id))
                throw new InvalidOperationException("The refresh token does not belong to the current session.");
            userId = token.UserId;
            sessionId = id;
        }
        else if (callerUserId is long uid && callerSessionId is Guid sid)
        {
            userId = uid;
            sessionId = sid;
        }
        else throw new InvalidOperationException("A refresh token or authenticated session is required.");

        await using var transaction = await AuthTransaction.BeginAsync(_dbContext, userId, cancellationToken);
        var session = await _dbContext.UserSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, cancellationToken);
        if (session is null) return;
        await _dbContext.Entry(session).ReloadAsync(cancellationToken);
        await RevokeFamilyAsync(session, _time.GetUtcNow().UtcDateTime, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _connections?.CloseConnectionsOfSession(session.Id);
    }

    private async Task RevokeFamilyAsync(UserSession session, DateTime now, CancellationToken ct)
    {
        session.RevokedUtc ??= now;
        var tokens = await _dbContext.UserRefreshTokens.Where(x => x.SessionId == session.Id && x.RevokedUtc == null).ToListAsync(ct);
        foreach (var token in tokens) token.RevokedUtc = now;
        await _dbContext.SaveChangesAsync(ct);
    }

    private static RefreshTokenException Refused() => new("session_ended", "Refresh token is invalid, expired or revoked. Sign in again.");

    public async Task<MeResponse?> GetMeAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);

        if (user is null)
            return null;

        // Admins (and the Service user) hold every module by role; a trader
        // holds what was granted, the same rows RequireModule checks.
        List<string>? grants = null;
        if (!string.Equals(user.Role, UserRoles.Admin, StringComparison.Ordinal) &&
            !string.Equals(user.Role, UserRoles.Service, StringComparison.Ordinal))
        {
            grants = await _dbContext.UserModuleGrants
                .AsNoTracking()
                .Where(x => x.UserId == user.Id)
                .Select(x => x.ModuleKey)
                .OrderBy(x => x)
                .ToListAsync(cancellationToken);
        }

        return new MeResponse
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            Role = user.Role,
            TotalCapital = user.TotalCapital,
            IsActive = user.IsActive,
            CreatedUtc = user.CreatedUtc,
            LastLoginUtc = user.LastLoginUtc,
            ModuleGrants = grants
        };
    }

    public async Task<List<MeResponse>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.AppUsers
            .AsNoTracking()
            .Select(user => new MeResponse
            {
                Id = user.Id,
                UserName = user.UserName,
                Email = user.Email,
                Role = user.Role,
                TotalCapital = user.TotalCapital,
                IsActive = user.IsActive,
                CreatedUtc = user.CreatedUtc,
                LastLoginUtc = user.LastLoginUtc
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<MeResponse?> GetUserByIdAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

        if (user is null)
            return null;

        // Admins (and the Service user) hold every module by role; a trader
        // holds what was granted, the same rows RequireModule checks.
        List<string>? grants = null;
        if (!string.Equals(user.Role, UserRoles.Admin, StringComparison.Ordinal) &&
            !string.Equals(user.Role, UserRoles.Service, StringComparison.Ordinal))
        {
            grants = await _dbContext.UserModuleGrants
                .AsNoTracking()
                .Where(x => x.UserId == user.Id)
                .Select(x => x.ModuleKey)
                .OrderBy(x => x)
                .ToListAsync(cancellationToken);
        }

        return new MeResponse
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email,
            Role = user.Role,
            TotalCapital = user.TotalCapital,
            IsActive = user.IsActive,
            CreatedUtc = user.CreatedUtc,
            LastLoginUtc = user.LastLoginUtc,
            ModuleGrants = grants
        };
    }

    public async Task<bool> DeleteUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var administration = await AccountAdministration.AcquireAsync(_dbContext, cancellationToken);
        // The exact name only: matched in any case, with no order, deleting
        // "ADMIN" could delete "admin".
        var user = await _dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.UserName == username, cancellationToken);

        if (user is null)
            return false;

        await _dbContext.Entry(user).ReloadAsync(cancellationToken);
        await AccountAdministration.GuardAsync(_dbContext, user, user.Role, false, cancellationToken);
        if (await _dbContext.SimulationRuns.AnyAsync(x => x.UserId == user.Id, cancellationToken)
            || await _dbContext.ManualOrderReceipts.AnyAsync(x => x.UserId == user.Id, cancellationToken)
            || await _dbContext.PortfolioSnapshots.AnyAsync(x => x.UserId == user.Id, cancellationToken))
            throw new InvalidOperationException("This account has trading history. Disable it from People instead of deleting it.");
        _dbContext.AppUsers.Remove(user);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await administration.CommitAsync(cancellationToken);
        _connections?.CloseConnectionsOf(user.Id);
        
        return true;
    }

    private async Task<AuthResponse> CreateAuthResponseAsync(AppUser user, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var session = new UserSession { UserId = user.Id, CreatedUtc = now, ExpiresUtc = now.AddDays(_jwtOptions.RefreshTokenDays) };
        string raw = GenerateSecureToken();
        _dbContext.UserSessions.Add(session);
        _dbContext.UserRefreshTokens.Add(new UserRefreshToken
        {
            UserId = user.Id, SessionId = session.Id, TokenHash = ComputeSha256(raw), CreatedUtc = now, ExpiresUtc = session.ExpiresUtc,
        });
        var response = ResponseFor(user, session.Id, raw);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return response;
    }

    private AuthResponse ResponseFor(AppUser user, Guid sessionId, string rawRefreshToken) => new()
    {
        AccessToken = GenerateAccessToken(user, sessionId), RefreshToken = rawRefreshToken,
        ExpiresInSeconds = _jwtOptions.AccessTokenMinutes * 60,
        User = new AuthUserResponse { Id = user.Id, UserName = user.UserName, Email = user.Email, Role = user.Role },
    };

    private string GenerateAccessToken(AppUser user, Guid sessionId)
    {
        // ClaimTypes.Role is what [Authorize(Roles = ...)] reads by default. Fall back
        // to Trader so a row predating the role column can never be treated as Admin.
        var role = AlgoTrading.Domain.Constants.UserRoles.Normalize(user.Role)
                   ?? AlgoTrading.Domain.Constants.UserRoles.Trader;

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim("sid", sessionId.ToString("D")),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role, role),
            // Duplicated under the short name so non-.NET consumers (the web client)
            // can read the role straight off the decoded token payload.
            new Claim("role", role),

            // When this token was issued, so a later cutoff on the account can
            // refuse it without waiting for it to expire.
            new Claim(
                JwtRegisteredClaimNames.Iat,
                _time.GetUtcNow().ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expires = _time.GetUtcNow().UtcDateTime.AddMinutes(_jwtOptions.AccessTokenMinutes);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string GenerateSecureToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(bytes);
    }

    private static string ComputeSha256(string value)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private static void ValidateRegisterRequest(RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.UserName))
            throw new InvalidOperationException("Username is required.");

        if (string.IsNullOrWhiteSpace(request.Email))
            throw new InvalidOperationException("Email is required.");

        if (string.IsNullOrWhiteSpace(request.Password))
            throw new InvalidOperationException("Password is required.");

        if (request.Password.Length < 8)
            throw new InvalidOperationException("Password must be at least 8 characters long.");
    }
}