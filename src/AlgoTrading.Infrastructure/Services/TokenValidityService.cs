using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Persisted login-session validation, with a separate cached account-cutoff compatibility check.
/// </summary>
public class TokenValidityService : ITokenValidityService
{
    /// <summary>
    /// How long a cached answer is trusted.
    /// </summary>
    /// <remarks>
    /// The entry is dropped the moment this process invalidates an account, so a
    /// single API sees the change immediately. The TTL is what bounds staleness
    /// if the platform is ever run as more than one instance — thirty seconds of
    /// residual access instead of the full hour a token would otherwise have.
    /// </remarks>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly TradingDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TokenValidityService> _logger;

    public TokenValidityService(
        TradingDbContext dbContext,
        IMemoryCache cache,
        ILogger<TokenValidityService> logger)
    {
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    private static string CacheKey(long userId) => $"tokenvalidity:{userId}";

    public async Task<bool> IsTokenAcceptableAsync(
        long userId,
        DateTime issuedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var state = await _cache.GetOrCreateAsync(CacheKey(userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheLifetime;

            return await _dbContext.AppUsers
                .AsNoTracking()
                .Where(x => x.Id == userId)
                .Select(x => new AccountTokenState(x.IsActive, x.TokensValidFromUtc))
                .FirstOrDefaultAsync(cancellationToken);
        });

        // No account behind the token at all: refuse rather than trust the claim.
        if (state is null || !state.IsActive) return false;

        if (state.ValidFromUtc is not DateTime cutoff) return true;

        // `iat` is written to whole seconds while the cutoff has sub-second
        // precision, so compare at second resolution. A token issued in the same
        // second as the cutoff is refused: the cost is one extra sign-in, and the
        // alternative — a second of slack — is a hole an automated caller could
        // sit inside.
        var cutoffSecond = new DateTime(
            cutoff.Ticks - (cutoff.Ticks % TimeSpan.TicksPerSecond),
            DateTimeKind.Utc);

        return issuedAtUtc > cutoffSecond;
    }

    /// <summary>Session state is read from the database on every authentication; revocations are never cached.</summary>
    public async Task<bool> IsSessionAcceptableAsync(long userId, Guid sessionId, DateTime issuedAtUtc, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var session = await _dbContext.UserSessions.AsNoTracking()
            .Where(x => x.Id == sessionId && x.UserId == userId)
            .Select(x => new { x.RevokedUtc, x.ExpiresUtc, x.User.IsActive, x.User.TokensValidFromUtc }).SingleOrDefaultAsync(cancellationToken);
        if (session is null || session.RevokedUtc != null || session.ExpiresUtc <= now || !session.IsActive) return false;
        return session.TokensValidFromUtc is not DateTime cutoff ||
            issuedAtUtc > new DateTime(cutoff.Ticks - cutoff.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    public async Task InvalidateExistingTokensAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await AuthTransaction.BeginAsync(_dbContext, userId, cancellationToken);
        var user = await _dbContext.AppUsers.FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null) return;
        var now = DateTime.UtcNow;
        user.TokensValidFromUtc = now;
        var sessions = await _dbContext.UserSessions.Where(x => x.UserId == userId && x.RevokedUtc == null).ToListAsync(cancellationToken);
        foreach (var session in sessions) session.RevokedUtc = now;
        var tokens = await _dbContext.UserRefreshTokens.Where(x => x.UserId == userId && x.RevokedUtc == null).ToListAsync(cancellationToken);
        foreach (var token in tokens) token.RevokedUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _cache.Remove(CacheKey(userId));

        _logger.LogInformation(
            "Every access token held by {UserName} is now refused.", user.UserName);
    }

    private sealed record AccountTokenState(bool IsActive, DateTime? ValidFromUtc);
}
