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
/// The latest-quote row refuses a tick older than the one it holds — right for a
/// live feed, where it stops a delayed tick overwriting a newer price — and must
/// not refuse a replay, which runs behind the stamps the live session already
/// wrote.
///
/// On 2026-09-11 the first TrueData recap froze all 49 of its contracts on this
/// rule: the quote held a 17:31 stamp, every replayed trade was stamped 09:xx,
/// entries filled at the frozen price and a +20-point leg target watched a
/// +49.5-point position stay open.
/// </summary>
public class LiveQuoteReplayOrderingTests
{
    private const string Symbol = "NSE:BANKNIFTY26SEP55800CE";
    private static readonly DateTime StoredStamp = new(2026, 9, 11, 12, 1, 2, DateTimeKind.Utc);   // 17:31 IST
    private static readonly DateTime ReplayStamp = new(2026, 9, 11, 3, 55, 1, DateTimeKind.Utc);   // 09:25 IST

    private static (LiveDataService Service, TradingDbContext Db) Build()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"quote-ordering-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new TradingDbContext(options);
        db.LiveQuotesLatest.Add(new LiveQuoteLatest
        {
            Symbol = Symbol,
            SourceKey = "truedata",
            DataType = "symbolUpdate",
            LastTradedPrice = 827.75m,
            ExchangeTimestampUtc = StoredStamp,
            RawPayload = "{}",
            UpdatedUtc = StoredStamp,
        });
        db.SaveChanges();
        return (new LiveDataService(db, new NoArchive(), new NoCatalog()), db);
    }

    private static UpsertLiveTickRequest Tick(bool isReplay) => new()
    {
        Symbol = Symbol,
        DataType = "symbolUpdate",
        SourceKey = "truedata",
        ExchangeTimestampUtc = ReplayStamp,
        LastTradedPrice = 877.20m,
        RawPayload = "{}",
        IsReplay = isReplay,
    };

    private static decimal? Ltp(TradingDbContext db) =>
        db.LiveQuotesLatest.AsNoTracking().Single(x => x.Symbol == Symbol).LastTradedPrice;

    [Fact]
    public async Task A_live_tick_older_than_the_stored_quote_is_still_refused()
    {
        var (service, db) = Build();
        await service.AppendLiveTicksAsync(new[] { Tick(isReplay: false) });
        Assert.Equal(827.75m, Ltp(db));
    }

    [Fact]
    public async Task A_replayed_tick_is_taken_although_it_is_older()
    {
        var (service, db) = Build();
        await service.AppendLiveTicksAsync(new[] { Tick(isReplay: true) });
        Assert.Equal(877.20m, Ltp(db));
        // The row now carries the replay's clock, so the next replayed tick
        // moves it on in the ordinary way.
        Assert.Equal(ReplayStamp, db.LiveQuotesLatest.AsNoTracking().Single(x => x.Symbol == Symbol).ExchangeTimestampUtc);
    }

    [Fact]
    public async Task The_single_tick_path_follows_the_same_rule()
    {
        var (service, db) = Build();
        await service.UpsertLatestQuoteAsync(new UpsertLiveQuoteRequest
        {
            Symbol = Symbol, DataType = "symbolUpdate", SourceKey = "truedata",
            ExchangeTimestampUtc = ReplayStamp, LastTradedPrice = 877.20m, RawPayload = "{}", IsReplay = false,
        });
        await db.SaveChangesAsync();
        Assert.Equal(827.75m, Ltp(db));

        await service.UpsertLatestQuoteAsync(new UpsertLiveQuoteRequest
        {
            Symbol = Symbol, DataType = "symbolUpdate", SourceKey = "truedata",
            ExchangeTimestampUtc = ReplayStamp, LastTradedPrice = 877.20m, RawPayload = "{}", IsReplay = true,
        });
        await db.SaveChangesAsync();
        Assert.Equal(877.20m, Ltp(db));
    }

    private sealed class NoArchive : IMarketTickArchiveQueue
    {
        public ValueTask EnqueueAsync(MarketTickArchiveRequest request, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class NoCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => Array.Empty<ProviderDescriptor>();
        public ProviderDescriptor? Find(string providerKey) => null;
    }
}
