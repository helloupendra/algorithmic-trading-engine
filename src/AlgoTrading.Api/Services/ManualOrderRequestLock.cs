using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>Keeps validation refusals from racing a still-committing request with the same key.</summary>
internal sealed class ManualOrderRequestLock(TradingDbContext db, long key, bool relational) : IAsyncDisposable
{
    private static readonly SemaphoreSlim MemoryGate = new(1, 1);

    public static async Task<ManualOrderRequestLock> AcquireAsync(TradingDbContext db, long user, Guid order, CancellationToken ct)
    {
        long key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"manual-order:{user}:{order:N}")));
        if (!db.Database.IsRelational()) {
            await MemoryGate.WaitAsync(ct);
            return new(db, key, false);
        }
        // Hold the session lock across validation AND the paper service's transaction.
        // Keeping the connection open also ensures unlock executes on the same backend.
        await db.Database.OpenConnectionAsync(ct);
        try {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({key})", ct);
            return new(db, key, true);
        }
        catch {
            // Cancellation can arrive immediately after the server granted the lock.
            try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({key})", CancellationToken.None); }
            finally { await db.Database.CloseConnectionAsync(); }
            throw;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!relational) { MemoryGate.Release(); return; }
        try { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({key})", CancellationToken.None); }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
