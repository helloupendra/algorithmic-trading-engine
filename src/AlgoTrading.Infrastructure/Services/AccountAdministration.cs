using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>Serializes changes to the set of active administrators across API instances.</summary>
internal sealed class AccountAdministration : IAsyncDisposable
{
    private static readonly SemaphoreSlim InMemoryGate = new(1, 1);
    private readonly IDbContextTransaction? _transaction;
    private AccountAdministration(IDbContextTransaction? transaction) => _transaction = transaction;

    public static async Task<AccountAdministration> AcquireAsync(TradingDbContext db, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
        {
            await InMemoryGate.WaitAsync(ct);
            return new(null);
        }
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Shared by demotion, deactivation and legacy deletion. Released by commit/rollback.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(8831, 11)", ct);
            return new(transaction);
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public static async Task GuardAsync(TradingDbContext db, AppUser user, string role, bool active, CancellationToken ct)
    {
        if (!user.IsActive || user.Role != UserRoles.Admin || (active && role == UserRoles.Admin)) return;
        if (!await db.AppUsers.AnyAsync(x => x.Id != user.Id && x.IsActive && x.Role == UserRoles.Admin, ct))
            throw new InvalidOperationException("This is the only active admin. Promote someone else first, or nobody could administer the platform.");
    }

    public Task CommitAsync(CancellationToken ct) => _transaction?.CommitAsync(ct) ?? Task.CompletedTask;
    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null) await _transaction.DisposeAsync();
        else InMemoryGate.Release();
    }
}
