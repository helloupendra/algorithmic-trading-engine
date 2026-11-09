using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.UnitTests;

public class SpecialSessionBackfillTests
{
    private static readonly DateOnly Day = new(2026, 11, 8);
    private const string Symbol = "NSE:NIFTY50-INDEX";

    [Theory]
    [InlineData("1", 70)]
    [InlineData("5", 14)]
    [InlineData("D", 1)]
    public async Task Sunday_is_fetched_and_measured_against_its_cash_hours(string resolution, int count)
    {
        using var db = NewDb();
        var source = new History(db, count);
        var service = new SymbolUniverseService(db, source, new Calendar());
        var request = new BackfillHistoryRequest { Symbol = Symbol, Resolution = resolution, FromDate = Day, ToDate = Day };
        var result = await service.EnsureHistoryCoverageAsync(request);
        Assert.Equal(1, source.Calls);
        Assert.Equal(count, result.CandlesFetched);
        Assert.Equal(1, result.TradingDaysExpected);
        Assert.Equal(1, result.TradingDaysCovered);
        Assert.True(result.FullCoverageAfterBackfill);
        await service.EnsureHistoryCoverageAsync(request);
        Assert.Equal(1, source.Calls); // no repeated request for the ten F&O-only minutes
    }

    [Fact]
    public async Task An_empty_vendor_answer_does_not_hide_a_confirmed_session_gap()
    {
        using var db = NewDb();
        db.SymbolSyncStates.Add(new SymbolSyncState { Symbol = Symbol, Resolution = "1", KnownEmptyDatesCsv = "2026-11-08" });
        db.SaveChanges();
        var source = new History(db, 0);
        var result = await new SymbolUniverseService(db, source, new Calendar()).EnsureHistoryCoverageAsync(
            new BackfillHistoryRequest { Symbol = Symbol, Resolution = "1", FromDate = Day, ToDate = Day });
        Assert.Equal(1, source.Calls);
        Assert.False(result.FullCoverageAfterBackfill);
        Assert.Single(result.RemainingGaps);
        Assert.Equal("", db.SymbolSyncStates.Single().KnownEmptyDatesCsv);
    }

    [Fact]
    public async Task Daily_history_reads_the_requested_IST_date_and_not_the_next_daily_bar()
    {
        using var db = NewDb();
        db.Candles.AddRange(new Candle { Symbol=Symbol, Resolution="D", TimeStampUtc=IstTime.StartOfDayUtc(Day), Close=100 },
            new Candle { Symbol=Symbol, Resolution="D", TimeStampUtc=IstTime.StartOfDayUtc(Day.AddDays(1)), Close=200 });
        await db.SaveChangesAsync();
        var service = new MarketDataService(null!, null!, db, Microsoft.Extensions.Logging.Abstractions.NullLogger<MarketDataService>.Instance);
        var rows = await service.GetStoredHistoryAsync(new GetStoredCandlesRequest { Symbol=Symbol, Resolution="D", FromDate=Day, ToDate=Day });
        Assert.Equal(100, Assert.Single(rows).Close);
    }

    private static TradingDbContext NewDb()
    {
        var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.Instruments.Add(new Instrument { Symbol = Symbol, Exchange = "NSE", Segment = "CM", IsEnabled = true });
        db.SaveChanges();
        return db;
    }

    private sealed class Calendar : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => new()
        {
            IsTradingDay = IstTime.DateOf(utcNow) == Day,
            SessionOpenUtc = IstTime.FromIst(Day.ToDateTime(new TimeOnly(14, 20))),
            SessionCloseUtc = IstTime.FromIst(Day.ToDateTime(new TimeOnly(15, 30))),
            SpecialSessionName = "Muhurat"
        };
        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => false;
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
    }

    private sealed class History(TradingDbContext db, int count) : IMarketDataService
    {
        public int Calls { get; private set; }
        public async Task<IReadOnlyList<CandleResponse>> SyncHistoryAsync(SyncHistoryRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            for (int i = 0; i < count; i++) db.Candles.Add(new Candle
            {
                Symbol = Symbol, Resolution = request.Resolution, Open = 100, High = 101, Low = 99, Close = 100,
                TimeStampUtc = request.Resolution == "D" ? IstTime.StartOfDayUtc(Day)
                    : IstTime.FromIst(Day.ToDateTime(new TimeOnly(14,20))).AddMinutes(i * int.Parse(request.Resolution))
            });
            await db.SaveChangesAsync(cancellationToken);
            return Enumerable.Range(0,count).Select(_ => new CandleResponse()).ToList();
        }
        public Task<IReadOnlyList<CandleResponse>> GetStoredHistoryAsync(GetStoredCandlesRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
