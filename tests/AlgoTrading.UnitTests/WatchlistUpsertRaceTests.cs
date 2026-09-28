using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Two callers adding one symbol to the recording list at once.
/// </summary>
/// <remarks>
/// On 28 Sep, as the 23 runners started at 09:16, two of them posted the same
/// symbol to <c>POST /api/LiveData/watchlist</c> together. Both read "not
/// there", both inserted, and Postgres refused the second on
/// IX_live_watchlist_Symbol (23505): that request answered 500 although the row
/// it asked for existed. The in-memory provider enforces no unique index, so the
/// race is staged: the other runner's upsert runs inside this one's save, and
/// the save then fails the way Postgres fails it.
/// </remarks>
public class WatchlistUpsertRaceTests
{
    private const string Symbol = "NSE:NIFTY2593025000CE";

    [Fact]
    public async Task Two_runners_adding_one_symbol_at_once_both_succeed_and_leave_one_row()
    {
        string name = $"watchlist-race-{Guid.NewGuid():N}";
        LiveWatchlistItem? otherRunners = null;
        var race = new OtherRunnerInsertsFirst(async () =>
        {
            await using var db = NewDb(name);
            otherRunners = await Service(db).UpsertWatchlistItemAsync(Request(priority: 1));
        });
        await using var mine = NewDb(name, race);

        var item = await Service(mine).UpsertWatchlistItemAsync(Request(priority: 5));

        Assert.True(race.Raced);
        await using var check = NewDb(name);
        var row = Assert.Single(await check.LiveWatchlistItems.ToListAsync());
        Assert.Equal(Symbol, row.Symbol);
        Assert.Equal(otherRunners!.Id, row.Id);
        Assert.Equal(row.Id, item.Id);
        Assert.Equal(5, row.Priority);   // the later save wins, as two upserts one after the other would
        // The refused insert is not left for the next save on this context to send again.
        Assert.DoesNotContain(mine.ChangeTracker.Entries(), e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task An_insert_that_fails_for_another_reason_still_fails()
    {
        // No other writer: the re-read finds nothing, so this is not a lost race
        // and must not be answered as a success.
        string name = $"watchlist-fail-{Guid.NewGuid():N}";
        await using var db = NewDb(name, new OtherRunnerInsertsFirst(() => Task.CompletedTask));

        await Assert.ThrowsAsync<DbUpdateException>(() => Service(db).UpsertWatchlistItemAsync(Request(priority: 5)));

        await using var check = NewDb(name);
        Assert.Empty(await check.LiveWatchlistItems.ToListAsync());
    }

    [Fact]
    public void The_race_is_decided_by_the_unique_index_on_the_symbol()
    {
        // The catch path above is only reached because Postgres refuses the
        // second insert. Without the index both rows would be kept and every
        // later upsert of the symbol would pick one of them.
        var entity = IncidentsTableContractTests.EntityFor(IncidentsTableContractTests.Model(), "live_watchlist")!;
        var index = Assert.Single(entity.GetIndexes(), i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(LiveWatchlistItem.Symbol)]));
        Assert.True(index.IsUnique);
        Assert.Equal("IX_live_watchlist_Symbol", index.GetDatabaseName());
    }

    /// <summary>What Postgres raised on 28 Sep, as Npgsql wraps it.</summary>
    internal static DbUpdateException UniqueViolation() => new(
        "An error occurred while saving the entity changes. See the inner exception for details.",
        new PostgresException(
            "duplicate key value violates unique constraint \"IX_live_watchlist_Symbol\"",
            "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

    private static UpsertWatchlistItemRequest Request(int priority) => new()
    {
        Symbol = Symbol,
        DataType = "symbolUpdate",
        IsActive = true,
        Priority = priority,
    };

    private static LiveDataService Service(TradingDbContext db) =>
        new(db, new NoCatalog(), PositionGreeksTests.Sessions());

    private static TradingDbContext NewDb(string name, IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return new TradingDbContext(builder.Options);
    }

    /// <summary>
    /// The first save that inserts a watchlist row lets the other runner's
    /// upsert run to the end, then fails as the losing insert does on Postgres.
    /// </summary>
    private sealed class OtherRunnerInsertsFirst(Func<Task> otherRunner) : SaveChangesInterceptor
    {
        public bool Raced { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool inserting = eventData.Context!.ChangeTracker.Entries<LiveWatchlistItem>()
                .Any(e => e.State == EntityState.Added);
            if (!Raced && inserting)
            {
                Raced = true;
                await otherRunner();
                throw UniqueViolation();
            }

            return result;
        }
    }

    private sealed class NoCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => Array.Empty<ProviderDescriptor>();
        public ProviderDescriptor? Find(string providerKey) => null;
    }
}
