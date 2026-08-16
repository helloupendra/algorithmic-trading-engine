// src/AlgoTrading.Infrastructure/Services/AuthService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Application.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Auth;
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

    public AuthService(
        TradingDbContext dbContext,
        PasswordHasher<AppUser> passwordHasher,
        IOptions<JwtOptions> jwtOptions)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _jwtOptions = jwtOptions.Value;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRegisterRequest(request);

        string userName = request.UserName.Trim();
        string email = request.Email.Trim().ToLowerInvariant();

        bool userNameExists = await _dbContext.AppUsers.AnyAsync(x => x.UserName == userName, cancellationToken);
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

        return await CreateAuthResponseAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.UserNameOrEmail))
            throw new InvalidOperationException("Username or email is required.");

        if (string.IsNullOrWhiteSpace(request.Password))
            throw new InvalidOperationException("Password is required.");

        string value = request.UserNameOrEmail.Trim();
        string valueLower = value.ToLowerInvariant();

        var user = await _dbContext.AppUsers
            .FirstOrDefaultAsync(x =>
                x.UserName == value ||
                x.Email == valueLower,
                cancellationToken);

        if (user is null || !user.IsActive)
            throw new InvalidOperationException("Invalid credentials.");

        var verifyResult = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verifyResult == PasswordVerificationResult.Failed)
            throw new InvalidOperationException("Invalid credentials.");

        user.LastLoginUtc = DateTime.UtcNow;
        user.UpdatedUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await CreateAuthResponseAsync(user, cancellationToken);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshTokenRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            throw new InvalidOperationException("Refresh token is required.");

        string refreshTokenHash = ComputeSha256(request.RefreshToken.Trim());

        var existing = await _dbContext.UserRefreshTokens
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.TokenHash == refreshTokenHash, cancellationToken);

        if (existing is null || existing.User is null)
            throw new InvalidOperationException("Invalid refresh token.");

        if (!existing.IsActive || !existing.User.IsActive)
            throw new InvalidOperationException("Refresh token is expired or revoked.");

        // rotate token
        existing.RevokedUtc = DateTime.UtcNow;

        string newRefreshToken = GenerateSecureToken();
        string newRefreshTokenHash = ComputeSha256(newRefreshToken);

        existing.ReplacedByTokenHash = newRefreshTokenHash;

        var replacement = new UserRefreshToken
        {
            UserId = existing.UserId,
            TokenHash = newRefreshTokenHash,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(_jwtOptions.RefreshTokenDays)
        };

        await _dbContext.UserRefreshTokens.AddAsync(replacement, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        string accessToken = GenerateAccessToken(existing.User);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = newRefreshToken,
            ExpiresInSeconds = _jwtOptions.AccessTokenMinutes * 60,
            User = new AuthUserResponse
            {
                Id = existing.User.Id,
                UserName = existing.User.UserName,
                Email = existing.User.Email
            }
        };
    }

    public async Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
            throw new InvalidOperationException("Refresh token is required.");

        string refreshTokenHash = ComputeSha256(request.RefreshToken.Trim());

        var existing = await _dbContext.UserRefreshTokens
            .FirstOrDefaultAsync(x => x.TokenHash == refreshTokenHash, cancellationToken);

        if (existing is null)
            return;

        if (!existing.IsRevoked)
        {
            existing.RevokedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<MeResponse?> GetMeAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);

        if (user is null)
            return null;

        return new MeResponse
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email
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
                Email = user.Email
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

        return new MeResponse
        {
            Id = user.Id,
            UserName = user.UserName,
            Email = user.Email
        };
    }

    public async Task<bool> DeleteUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .FirstOrDefaultAsync(x => x.UserName.ToLower() == username.ToLower(), cancellationToken);

        if (user is null)
            return false;

        _dbContext.AppUsers.Remove(user);
        await _dbContext.SaveChangesAsync(cancellationToken);
        
        return true;
    }

    private async Task<AuthResponse> CreateAuthResponseAsync(AppUser user, CancellationToken cancellationToken)
    {
        string accessToken = GenerateAccessToken(user);
        string rawRefreshToken = GenerateSecureToken();
        string refreshTokenHash = ComputeSha256(rawRefreshToken);

        var refreshToken = new UserRefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshTokenHash,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(_jwtOptions.RefreshTokenDays)
        };

        await _dbContext.UserRefreshTokens.AddAsync(refreshToken, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = rawRefreshToken,
            ExpiresInSeconds = _jwtOptions.AccessTokenMinutes * 60,
            User = new AuthUserResponse
            {
                Id = user.Id,
                UserName = user.UserName,
                Email = user.Email
            }
        };
    }

    private string GenerateAccessToken(AppUser user)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName),
            new Claim(JwtRegisteredClaimNames.Email, user.Email)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expires = DateTime.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes);

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