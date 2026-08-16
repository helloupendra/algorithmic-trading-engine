using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Session;

/// <summary>
/// Entity Framework Core implementation of the broker session store.
/// Persists the active broker session (tokens) in the primary PostgreSQL database.
/// </summary>
public class DatabaseBrokerSessionStore : IBrokerSessionStore
{
    private readonly TradingDbContext _dbContext;

    public DatabaseBrokerSessionStore(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.BrokerSessions
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.BrokerSessions
            .OrderByDescending(x => x.UpdatedUtc)
            .FirstOrDefaultAsync(x => x.BrokerName == session.BrokerName, cancellationToken);

        if (existing is null)
        {
            session.CreatedUtc = DateTime.UtcNow;
            session.UpdatedUtc = DateTime.UtcNow;
            session.IsActive = true;

            await _dbContext.BrokerSessions.AddAsync(session, cancellationToken);
        }
        else
        {
            existing.AccessToken = session.AccessToken;
            existing.RefreshToken = session.RefreshToken;
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
}