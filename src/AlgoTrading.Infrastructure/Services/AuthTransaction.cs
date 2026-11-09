using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>Serializes session issuance, rotation and revocation for one account across API processes.</summary>
internal sealed class AuthTransaction(IDbContextTransaction? transaction) : IAsyncDisposable
{
    // EF InMemory is only used by unit tests. Relational production uses PostgreSQL row locks.
    private static readonly SemaphoreSlim InMemoryGate = new(1, 1);

    public static async Task<AuthTransaction> BeginAsync(TradingDbContext db, long userId, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await InMemoryGate.WaitAsync(ct);
            return new(null);
        }
        var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.AppUsers.FromSqlInterpolated($"SELECT * FROM app_user WHERE \"Id\" = {userId} FOR UPDATE")
                .AsNoTracking().ToListAsync(ct);
            return new(tx);
        }
        catch { await tx.DisposeAsync(); throw; }
    }

    public Task CommitAsync(CancellationToken ct) => transaction?.CommitAsync(ct) ?? Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if (transaction is null) InMemoryGate.Release();
        else await transaction.DisposeAsync();
    }
}
