using System.Security.Cryptography;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Session;

/// <summary>
/// Entity Framework Core implementation of the broker session store.
/// Persists the active broker session in the primary PostgreSQL database with
/// the tokens encrypted at rest (ASP.NET Data Protection): a database dump or
/// backup no longer contains usable broker credentials.
///
/// Rows written before encryption existed are read transparently (legacy
/// plaintext fallback) and become encrypted on the next save.
/// </summary>
public class DatabaseBrokerSessionStore : IBrokerSessionStore
{
    private readonly TradingDbContext _dbContext;
    private readonly IDataProtector _protector;

    public DatabaseBrokerSessionStore(
        TradingDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector("BrokerSession.Tokens.v1");
    }

    public async Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var session = await _dbContext.BrokerSessions
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (session is null) return null;

        // Callers (token exchange, history client, the Python ingestor via
        // /api/Auth/session) always see plaintext; storage is what's protected.
        session.AccessToken = Unprotect(session.AccessToken);
        session.RefreshToken = Unprotect(session.RefreshToken);
        return session;
    }

    public async Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default)
    {
        string accessToken = Protect(session.AccessToken);
        string? refreshToken = string.IsNullOrEmpty(session.RefreshToken)
            ? session.RefreshToken
            : Protect(session.RefreshToken);

        var existing = await _dbContext.BrokerSessions
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(x => x.BrokerName == session.BrokerName, cancellationToken);

        if (existing is null)
        {
            session.AccessToken = accessToken;
            session.RefreshToken = refreshToken;
            session.CreatedUtc = DateTime.UtcNow;
            session.UpdatedUtc = DateTime.UtcNow;
            session.IsActive = true;

            await _dbContext.BrokerSessions.AddAsync(session, cancellationToken);
        }
        else
        {
            existing.AccessToken = accessToken;
            existing.RefreshToken = refreshToken;
            existing.UpdatedUtc = DateTime.UtcNow;
            existing.IsActive = true;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var activeSessions = await _dbContext.BrokerSessions
            .Where(x => x.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var session in activeSessions)
        {
            session.IsActive = false;
            session.UpdatedUtc = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private string Protect(string value)
        => string.IsNullOrEmpty(value) ? value : _protector.Protect(value);

    /// <summary>Decrypts a stored value; legacy plaintext rows pass through.</summary>
    private string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            // Written before encryption existed — treat as plaintext.
            return value;
        }
    }
}
