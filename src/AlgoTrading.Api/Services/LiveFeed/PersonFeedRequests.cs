using System.Collections.Concurrent;
using System.Security.Claims;
using AlgoTrading.Api.Security;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.LiveFeed;

/// <summary>What became of a person's request to put a symbol on the live feed.</summary>
public enum FeedRequestOutcome
{
    /// <summary>Already on the recording list; nothing was changed (in particular not its priority).</summary>
    AlreadyCarried,

    /// <summary>Added to the recording list.</summary>
    Added,

    /// <summary>Refused: the trader used up today's allowance of new symbols.</summary>
    OverDailyLimit,
}

/// <summary>
/// Puts a symbol a person asked about on the live feed: a manual-order ticket's instrument, or a symbol on a trader's
/// own watchlist.
/// </summary>
/// <remarks>
/// <para>
/// From the 24 Nov 2026 security review. Both routes wrote the recording list at the desk's priority with no limit. A
/// trader could fill FYERS's 100-symbol socket with stock futures ahead of every option contract the runs trade (the
/// 17 Nov "FYERS feed full" failure, on purpose).
/// </para>
/// <para>
/// Three rules now apply:
/// </para>
/// <list type="bullet">
/// <item>A symbol already on the list is left alone. Before, an upsert at 10 lowered a run contract's 100.</item>
/// <item>A trader's new symbols go on at <see cref="WatchlistPriorities.PersonRequested"/>, which the feed offers
/// last.</item>
/// <item>A trader adds at most <see cref="PerTraderPerDay"/> new symbols per IST day.</item>
/// </list>
/// <para>
/// Admins and the service account are the desk and keep <see cref="WatchlistPriorities.Desk"/>.
/// </para>
/// </remarks>
public static class PersonFeedRequests
{
    public const int PerTraderPerDay = 25;

    private static readonly ConcurrentDictionary<(long UserId, DateOnly Day), int> Taken = new();

    public static async Task<FeedRequestOutcome> RequestAsync(
        TradingDbContext db,
        ClaimsPrincipal user,
        string symbol,
        Func<int, Task> upsertAtPriority,
        CancellationToken cancellationToken)
    {
        bool carried = await db.LiveWatchlistItems.AsNoTracking()
            .AnyAsync(x => x.Symbol == symbol && x.IsActive, cancellationToken);
        if (carried)
        {
            return FeedRequestOutcome.AlreadyCarried;
        }

        if (IsDesk(user))
        {
            await upsertAtPriority(WatchlistPriorities.Desk);
            return FeedRequestOutcome.Added;
        }

        if (!TryTake(user.GetRequiredUserId(), DateTime.UtcNow))
        {
            return FeedRequestOutcome.OverDailyLimit;
        }

        await upsertAtPriority(WatchlistPriorities.PersonRequested);
        return FeedRequestOutcome.Added;
    }

    public static bool IsDesk(ClaimsPrincipal user) => user.IsAdmin() || user.IsInRole(UserRoles.Service);

    /// <summary>Counts one new symbol against the trader's day; false once the day's allowance is used.</summary>
    public static bool TryTake(long userId, DateTime utcNow)
    {
        var day = DateOnly.FromDateTime(utcNow.AddHours(5.5));
        foreach (var old in Taken.Keys.Where(k => k.Day < day)) Taken.TryRemove(old, out _);

        bool taken = false;
        Taken.AddOrUpdate((userId, day),
            _ => { taken = true; return 1; },
            (_, count) =>
            {
                if (count >= PerTraderPerDay) { taken = false; return count; }
                taken = true;
                return count + 1;
            });
        return taken;
    }
}
