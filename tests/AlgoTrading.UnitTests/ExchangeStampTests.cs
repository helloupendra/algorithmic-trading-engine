using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A stamp from the future must not freeze a quote. A Dhan feed restarted during
/// the session once stamped a contract's 10:00 snapshot 15:30; the quote then
/// refused that contract's real ticks as older for the rest of the day. These run
/// on the real clock because the guard is about the real clock.
/// </summary>
public class ExchangeStampTests
{
    private const string Option = "NSE:NIFTY2611025000CE";

    private static (LiveDataService Service, TradingDbContext Db) Build()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"exchange-stamps-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new TradingDbContext(options);
        return (new LiveDataService(db, new NoCatalog(), new MarketSessionService(new OpenCalendar())), db);
    }

    private static UpsertLiveTickRequest Tick(DateTime? stampUtc, decimal price, bool replay = false) => new()
    {
        Symbol = Option,
        DataType = "symbolUpdate",
        SourceKey = "dhan",
        ExchangeTimestampUtc = stampUtc,
        LastTradedPrice = price,
        RawPayload = "{}",
        IsReplay = replay,
    };

    /// <summary>Both ways a tick reaches the quote: one at a time, and the feed's batches.</summary>
    public static TheoryData<bool> Paths => new() { true, false };

    private static Task Send(LiveDataService service, bool batched, UpsertLiveTickRequest tick) =>
        batched ? service.AppendLiveTicksAsync([tick]) : service.AppendLiveTickAsync(tick);

    private static void StoreQuote(TradingDbContext db, DateTime? stampUtc, decimal price)
    {
        db.LiveQuotesLatest.Add(new LiveQuoteLatest
        {
            Symbol = Option, SourceKey = "dhan", DataType = "symbolUpdate", RawPayload = "{}",
            LastTradedPrice = price, ExchangeTimestampUtc = stampUtc, UpdatedUtc = DateTime.UtcNow.AddHours(-1),
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static LiveQuoteLatest Quote(TradingDbContext db)
    {
        db.ChangeTracker.Clear();
        return db.LiveQuotesLatest.AsNoTracking().Single(q => q.Symbol == Option);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task A_stored_stamp_from_the_future_does_not_block_a_good_tick(bool batched)
    {
        var (service, db) = Build();
        var now = DateTime.UtcNow;
        StoreQuote(db, now.AddHours(2.5), 12.5m);

        await Send(service, batched, Tick(now.AddSeconds(-1), 13m));

        var quote = Quote(db);
        Assert.Equal(13m, quote.LastTradedPrice);
        Assert.True(quote.ExchangeTimestampUtc <= DateTime.UtcNow, "the real stamp replaces the bad one");
        Assert.True(quote.UpdatedUtc > now.AddMinutes(-1), "the quote is no longer ageing");
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task An_older_tick_still_gives_way_to_a_stored_stamp_that_is_not_in_the_future(bool batched)
    {
        var (service, db) = Build();
        var now = DateTime.UtcNow;
        StoreQuote(db, now.AddSeconds(-5), 100m);

        await Send(service, batched, Tick(now.AddSeconds(-30), 90m));

        Assert.Equal(100m, Quote(db).LastTradedPrice);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task A_stamp_from_the_future_is_treated_as_no_stamp(bool batched)
    {
        var (service, db) = Build();
        var now = DateTime.UtcNow;

        await Send(service, batched, Tick(now.AddHours(2.5), 12.5m));

        Assert.Null(Quote(db).ExchangeTimestampUtc);
        Assert.Null(db.LiveTicks.AsNoTracking().Single().ExchangeTimestampUtc);
        // No bar past the clock, whatever the session rule made of the arrival minute.
        Assert.DoesNotContain(db.LiveBars.AsNoTracking().ToList(), b => b.BarStartUtc > DateTime.UtcNow);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task An_unstamped_tick_does_not_keep_a_bad_stamp_alive(bool batched)
    {
        var (service, db) = Build();
        StoreQuote(db, DateTime.UtcNow.AddHours(2.5), 12.5m);

        await Send(service, batched, Tick(null, 13m));

        var quote = Quote(db);
        Assert.Equal(13m, quote.LastTradedPrice);
        Assert.Null(quote.ExchangeTimestampUtc);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task A_few_seconds_of_clock_difference_is_no_fault(bool batched)
    {
        var (service, db) = Build();
        var ahead = DateTime.UtcNow.AddSeconds(30);

        await Send(service, batched, Tick(ahead, 12.5m));

        Assert.Equal(ahead, Quote(db).ExchangeTimestampUtc);
    }

    [Fact]
    public void A_replay_keeps_its_own_clock()
    {
        var now = new DateTime(2026, 11, 2, 7, 30, 0, DateTimeKind.Utc);
        var ahead = now.AddHours(3);

        Assert.Equal(ahead, ExchangeStamps.Usable(ahead, isReplay: true, now));
        Assert.Null(ExchangeStamps.Usable(ahead, isReplay: false, now));
        Assert.False(ExchangeStamps.IsOlderThanStored(now, now.AddMinutes(-1).AddHours(1), isReplay: true, now));
    }

    private sealed class NoCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => Array.Empty<ProviderDescriptor>();
        public ProviderDescriptor? Find(string providerKey) => null;
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public bool IsLoaded => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
