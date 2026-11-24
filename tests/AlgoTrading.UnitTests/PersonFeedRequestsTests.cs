using System.Security.Claims;
using AlgoTrading.Api.Services.LiveFeed;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A person's request to put a symbol on the live feed (a manual-order ticket, a trader's own watchlist). From the
/// 24 Nov 2026 security review: a trader could fill FYERS's 100-symbol socket ahead of every contract the runs trade.
/// </summary>
public sealed class PersonFeedRequestsTests
{
    private static TradingDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static ClaimsPrincipal Caller(long id, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, $"user{id}"), new Claim(ClaimTypes.Role, role)],
        "test"));

    /// <summary>A trader id no other test uses: the daily allowance is kept per process.</summary>
    private static long FreshTrader() => Random.Shared.NextInt64(1_000_000, long.MaxValue);

    [Fact]
    public async Task A_symbol_already_on_the_feed_is_left_alone_so_a_run_contract_keeps_its_priority()
    {
        await using var db = NewDb();
        db.LiveWatchlistItems.Add(new LiveWatchlistItem
        {
            Symbol = "NSE:BANKNIFTY26NOV56000CE", DataType = "symbolUpdate", IsActive = true, Priority = WatchlistPriorities.RunContract,
        });
        await db.SaveChangesAsync();
        var asked = new List<int>();

        var outcome = await PersonFeedRequests.RequestAsync(db, Caller(FreshTrader(), UserRoles.Trader), "NSE:BANKNIFTY26NOV56000CE",
            p => { asked.Add(p); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(FeedRequestOutcome.AlreadyCarried, outcome);
        Assert.Empty(asked);
    }

    [Fact]
    public async Task A_trader_s_new_symbol_goes_on_at_the_lowest_priority_and_the_desk_s_at_its_own()
    {
        await using var db = NewDb();
        var asked = new List<int>();
        Task Upsert(int p) { asked.Add(p); return Task.CompletedTask; }

        await PersonFeedRequests.RequestAsync(db, Caller(FreshTrader(), UserRoles.Trader), "NSE:SBIN26NOVFUT", Upsert, CancellationToken.None);
        await PersonFeedRequests.RequestAsync(db, Caller(1, UserRoles.Admin), "NSE:TCS26NOVFUT", Upsert, CancellationToken.None);
        await PersonFeedRequests.RequestAsync(db, Caller(2, UserRoles.Service), "NSE:INFY26NOVFUT", Upsert, CancellationToken.None);

        Assert.Equal([WatchlistPriorities.PersonRequested, WatchlistPriorities.Desk, WatchlistPriorities.Desk], asked);
    }

    [Fact]
    public async Task A_trader_adds_at_most_the_daily_allowance_of_new_symbols()
    {
        await using var db = NewDb();
        var trader = Caller(FreshTrader(), UserRoles.Trader);
        int added = 0;

        for (int i = 0; i < PersonFeedRequests.PerTraderPerDay; i++)
        {
            var ok = await PersonFeedRequests.RequestAsync(db, trader, $"NSE:STOCK{i}26NOVFUT",
                _ => { added++; return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal(FeedRequestOutcome.Added, ok);
        }

        var refused = await PersonFeedRequests.RequestAsync(db, trader, "NSE:ONEMORE26NOVFUT",
            _ => { added++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(FeedRequestOutcome.OverDailyLimit, refused);
        Assert.Equal(PersonFeedRequests.PerTraderPerDay, added);

        // The next IST day starts a new allowance.
        Assert.True(PersonFeedRequests.TryTake(long.Parse(trader.FindFirstValue(ClaimTypes.NameIdentifier)!), DateTime.UtcNow.AddDays(1)));
    }
}
