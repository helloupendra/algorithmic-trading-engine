using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The days the chain page can be rewound to. The chain table is some 15 GB
/// with no index on the day of a capture, so the list is walked off the
/// (underlying, expiry, time) index a read per day and is kept in memory, only
/// its newest end read again.
/// </summary>
public class OptionChainRecordedDaysTests
{
    private static readonly DateOnly Nearest = new(2026, 11, 3);
    private static readonly DateOnly Next = new(2026, 11, 10);
    private static readonly DateOnly Monthly = new(2026, 11, 23);

    [Fact]
    public async Task Every_day_with_a_capture_is_listed_newest_first_whichever_expiry_it_was()
    {
        await using var db = Db();
        // Thursday 29 Oct to Tuesday 3 Nov the nearest is the 3rd, with the
        // next weekly and the monthly captured beside it from Friday.
        Capture(db, Nearest, At(2026, 10, 29, 9, 15));
        Capture(db, Nearest, At(2026, 10, 29, 15, 29));
        foreach (var open in new[] { At(2026, 10, 30, 9, 15), At(2026, 11, 2, 9, 15), At(2026, 11, 3, 9, 15) })
        {
            Capture(db, Nearest, open);
            Capture(db, Next, open.AddSeconds(4));
        }
        Capture(db, Monthly, At(2026, 10, 30, 9, 15).AddSeconds(8));
        // Wednesday 4 Nov: the 3rd has expired and the 10th is the nearest.
        Capture(db, Next, At(2026, 11, 4, 9, 15));
        // An IST day, not a UTC one: 01:30 IST on 28 Oct is still 27 Oct in UTC.
        Capture(db, Nearest, new DateTime(2026, 10, 27, 20, 0, 0, DateTimeKind.Utc));
        // Another underlying's day is not NIFTY's.
        Capture(db, Nearest, At(2026, 10, 26, 9, 15), underlying: "BANKNIFTY");
        await db.SaveChangesAsync();
        var service = new OptionChainService(db);

        var recorded = await service.GetRecordedDaysAsync("nifty");

        Assert.Equal("NIFTY", recorded.Underlying);
        Assert.Equal(
            new[] { new DateOnly(2026, 11, 4), new DateOnly(2026, 11, 3), new DateOnly(2026, 11, 2), new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 29), new DateOnly(2026, 10, 28) },
            recorded.Days);
    }

    [Fact]
    public async Task The_walk_costs_a_read_a_day_and_a_few_an_expiry_however_many_captures_a_day_holds()
    {
        await using var db = Db();
        // Five sessions of the nearest every minute, the next weekly every five.
        var monday = At(2026, 10, 26, 9, 15);
        foreach (var offset in new[] { 0, 1, 2, 3, 4 })
        {
            for (int minute = 0; minute < 60; minute++)
            {
                Capture(db, Nearest, monday.AddDays(offset).AddMinutes(minute));
                if (minute % 5 == 0) Capture(db, Next, monday.AddDays(offset).AddMinutes(minute).AddSeconds(4));
            }
        }
        await db.SaveChangesAsync();
        var service = new OptionChainService(db);
        var days = new HashSet<DateOnly>();

        int reads = await service.AddRecordedDaysAsync("NIFTY", null, days, CancellationToken.None);

        Assert.Equal(5, days.Count);
        // 360 captures: the reads follow the days and expiries, never the rows.
        Assert.True(reads <= days.Count + 3 * 2 + 1, $"{reads} reads for {days.Count} days of two expiries");
    }

    [Fact]
    public async Task A_kept_list_is_answered_from_memory_and_only_its_newest_end_is_read_again()
    {
        await using var db = Db();
        Capture(db, Nearest, At(2026, 10, 29, 9, 15));
        Capture(db, Nearest, At(2026, 10, 30, 9, 15));
        await db.SaveChangesAsync();
        var clock = new Clock(At(2026, 10, 30, 10, 0));
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new OptionChainService(db, cache: cache, time: clock);

        Assert.Equal(new[] { new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 29) }, (await service.GetRecordedDaysAsync("NIFTY")).Days);

        // Monday's first capture, and a day stored late for a week already listed past.
        Capture(db, Nearest, At(2026, 11, 2, 9, 15));
        Capture(db, Nearest, At(2026, 10, 27, 9, 15));
        await db.SaveChangesAsync();

        // Within the window: the kept list, as it was.
        clock.Utc = clock.Utc.Add(OptionChainService.RecordedDaysFor).AddSeconds(-1);
        Assert.Equal(2, (await service.GetRecordedDaysAsync("NIFTY")).Days.Count);

        // Past it: the newest end is read again and Monday is listed. The ended
        // days are not re-read, so the late one waits for the list to be let go.
        clock.Utc = clock.Utc.AddSeconds(1);
        Assert.Equal(
            new[] { new DateOnly(2026, 11, 2), new DateOnly(2026, 10, 30), new DateOnly(2026, 10, 29) },
            (await service.GetRecordedDaysAsync("NIFTY")).Days);
    }

    [Fact]
    public async Task Nothing_recorded_lists_no_day()
    {
        await using var db = Db();
        Capture(db, Nearest, At(2026, 10, 29, 9, 15), underlying: "BANKNIFTY");
        await db.SaveChangesAsync();

        var recorded = await new OptionChainService(db).GetRecordedDaysAsync("NIFTY");

        Assert.Equal("NIFTY", recorded.Underlying);
        Assert.Empty(recorded.Days);
    }

    // ------------------------------------------------------------- helpers

    /// <summary>An IST wall-clock time as UTC.</summary>
    private static DateTime At(int year, int month, int day, int hour, int minute)
        => new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    /// <summary>One capture: a CE and a PE at one strike.</summary>
    private static void Capture(TradingDbContext db, DateOnly expiry, DateTime capturedUtc, string underlying = "NIFTY")
    {
        foreach (var type in new[] { "CE", "PE" })
        {
            db.OptionChainSnapshots.Add(new OptionChainSnapshot
            {
                Underlying = underlying,
                ExpiryDate = expiry,
                StrikePrice = 24000m,
                OptionType = type,
                Symbol = $"NSE:{underlying}{expiry:yyMMdd}24000{type}",
                CapturedUtc = capturedUtc,
                SpotPrice = 24105m,
                LastTradedPrice = 120m,
                OpenInterest = 1_000,
                OpenInterestAtOpen = 900,
                SourceKey = "dhan",
            });
        }
    }

    private sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Utc, TimeSpan.Zero);
    }

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"chain-days-{Guid.NewGuid():N}").Options);
}
