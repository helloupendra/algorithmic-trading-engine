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
/// A minute the exchange was not open gets no bar.
///
/// The vendors keep publishing the last quote after the close. On 2026-09-18
/// NIFTYBANK arrived at 57 ticks a minute until 20:00 IST, every one carrying
/// 56,358.70 — the price it had closed at. Filed as bars they became four and a
/// half hours of flat candles that the nightly archive copied into the candle
/// table, where a chart drew swings on them and a backtest traded them. The
/// ticks are still kept; only the bar is refused.
/// </summary>
public class LiveBarSessionTests
{
    private const string Index = "NSE:NIFTYBANK-INDEX";
    private const string Crude = "MCX:CRUDEOIL26SEPFUT";

    // 2026-09-18 was a Friday and a trading day.
    private static readonly DateTime InSession = new(2026, 9, 18, 9, 45, 12, DateTimeKind.Utc);    // 15:15 IST
    private static readonly DateTime AfterClose = new(2026, 9, 18, 10, 35, 4, DateTimeKind.Utc);   // 16:05 IST
    private static readonly DateTime CrudeEvening = new(2026, 9, 18, 15, 5, 0, DateTimeKind.Utc);  // 20:35 IST

    private static (LiveDataService Service, TradingDbContext Db) Build()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"live-bar-session-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new TradingDbContext(options);
        return (new LiveDataService(db, new NoCatalog(), new MarketSessionService(new OpenCalendar())), db);
    }

    private static UpsertLiveTickRequest Tick(string symbol, DateTime stampUtc, decimal price) => new()
    {
        Symbol = symbol,
        DataType = "symbolUpdate",
        SourceKey = "dhan",
        ExchangeTimestampUtc = stampUtc,
        LastTradedPrice = price,
        RawPayload = "{}",
    };

    private static int Bars(TradingDbContext db, string symbol) =>
        db.LiveBars.AsNoTracking().Count(b => b.Symbol == symbol);

    private static int Ticks(TradingDbContext db, string symbol) =>
        db.LiveTicks.AsNoTracking().Count(t => t.Symbol == symbol);

    [Fact]
    public async Task A_tick_inside_the_session_makes_a_bar()
    {
        var (service, db) = Build();

        await service.AppendLiveTickAsync(Tick(Index, InSession, 56_360.20m));

        Assert.Equal(1, Bars(db, Index));
    }

    [Fact]
    public async Task A_tick_after_the_close_makes_no_bar_but_is_still_recorded()
    {
        var (service, db) = Build();

        await service.AppendLiveTickAsync(Tick(Index, AfterClose, 56_358.70m));

        Assert.Equal(0, Bars(db, Index));
        Assert.Equal(1, Ticks(db, Index));
    }

    [Fact]
    public async Task The_batch_path_refuses_the_same_minutes()
    {
        var (service, db) = Build();

        await service.AppendLiveTicksAsync(new[]
        {
            Tick(Index, InSession, 56_360.20m),
            Tick(Index, AfterClose, 56_358.70m),
            Tick(Index, AfterClose.AddMinutes(30), 56_358.70m),
        });

        var bar = Assert.Single(db.LiveBars.AsNoTracking().Where(b => b.Symbol == Index));
        Assert.Equal(new DateTime(2026, 9, 18, 9, 45, 0, DateTimeKind.Utc), bar.BarStartUtc);
        Assert.Equal(3, Ticks(db, Index));
    }

    [Fact]
    public async Task A_commodity_keeps_its_evening_session()
    {
        // MCX trades into the night; 20:35 IST is the middle of its day, not
        // after it, so the rule must not be the equity close in disguise.
        var (service, db) = Build();

        await service.AppendLiveTickAsync(Tick(Crude, CrudeEvening, 5_842.00m));

        Assert.Equal(1, Bars(db, Crude));
    }

    [Fact]
    public async Task An_exchange_the_session_rules_do_not_know_keeps_its_bars()
    {
        // Losing data is worse than keeping a doubtful bar: an exchange nobody
        // has written hours for is left alone rather than silently dropped.
        var (service, db) = Build();

        await service.AppendLiveTickAsync(Tick("NCDEX:SOYBEAN26SEPFUT", AfterClose, 4_800m));

        Assert.Equal(1, Bars(db, "NCDEX:SOYBEAN26SEPFUT"));
    }

    private sealed class NoCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => Array.Empty<ProviderDescriptor>();
        public ProviderDescriptor? Find(string providerKey) => null;
    }

    /// <summary>A calendar with no holidays and no special sessions.</summary>
    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public bool IsLoaded => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
