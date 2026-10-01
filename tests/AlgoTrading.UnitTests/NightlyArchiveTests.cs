using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The nightly candle archive runs after every exchange's session of the day, MCX's included: 23:50 IST while New
/// York is on daylight saving (MCX closes at 23:30), and 00:15 the next morning while it is on standard time (MCX
/// closes at 23:55). The closes come from <see cref="MarketSessionService"/>, not from a clock time in the archive.
/// </summary>
public class NightlyArchiveTests
{
    // The US went back to standard time on Sunday 1 Nov 2026: from Monday 2 Nov MCX trades to 23:55 IST.
    private static readonly DateOnly Sun01Nov = new(2026, 11, 1);
    private static readonly DateOnly Mon02Nov = new(2026, 11, 2);
    private static readonly DateOnly Tue03Nov = new(2026, 11, 3);

    // Still on US daylight saving: MCX closes at 23:30.
    private static readonly DateOnly Tue29Sep = new(2026, 9, 29);
    private static readonly DateOnly Wed30Sep = new(2026, 9, 30);

    private const string Crude = "MCX:CRUDEOIL26NOVFUT";
    private const string Nifty = "NSE:NIFTY50-INDEX";

    private static readonly TimeSpan RunAt = ArchiveSchedule.DefaultRunAtIst;

    private static DateTime Ist(DateOnly day, int hour, int minute) => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute)));

    private static DateTime IstLocal(DateOnly day, int hour, int minute) => day.ToDateTime(new TimeOnly(hour, minute));

    [Fact]
    public async Task In_November_MCX_s_last_five_minutes_reach_the_candles()
    {
        var archive = Archive(vixCheck: false, out var db);
        SeedLastArchived(db, Sun01Nov);
        SeedBars(db, Nifty, Ist(Mon02Nov, 9, 15), Ist(Mon02Nov, 15, 30));
        // What the live bars hold at 23:50: crude is still trading.
        SeedBars(db, Crude, Ist(Mon02Nov, 9, 0), Ist(Mon02Nov, 23, 50));

        Assert.Empty(await archive.RunDueDaysAsync(Ist(Mon02Nov, 23, 50), CancellationToken.None));

        // The rest of the session, to the 23:55 close.
        SeedBars(db, Crude, Ist(Mon02Nov, 23, 50), Ist(Mon02Nov, 23, 55));
        Assert.Empty(await archive.RunDueDaysAsync(Ist(Tue03Nov, 0, 14), CancellationToken.None));
        Assert.Equal([Mon02Nov], await archive.RunDueDaysAsync(Ist(Tue03Nov, 0, 15), CancellationToken.None));

        Assert.Equal(895, Candles(db, Crude, "1").Count);
        Assert.Equal(Ist(Mon02Nov, 23, 54), Candles(db, Crude, "1").Max());
        Assert.Equal(Ist(Mon02Nov, 23, 50), Candles(db, Crude, "5").Max());
        Assert.Equal(Ist(Mon02Nov, 23, 45), Candles(db, Crude, "15").Max());
        Assert.Equal(375, Candles(db, Nifty, "1").Count);
        Assert.Equal("2026-11-02", db.SystemSettings.AsNoTracking().Single(s => s.Key == NightlyArchiveService.LastArchivedDayKey).Value);
    }

    [Fact]
    public async Task While_MCX_closes_at_23_30_the_day_is_archived_at_23_50_as_before()
    {
        var archive = Archive(vixCheck: false, out var db);
        SeedLastArchived(db, Tue29Sep);
        SeedBars(db, Crude, Ist(Wed30Sep, 9, 0), Ist(Wed30Sep, 23, 30));

        Assert.Empty(await archive.RunDueDaysAsync(Ist(Wed30Sep, 23, 49), CancellationToken.None));
        Assert.Equal([Wed30Sep], await archive.RunDueDaysAsync(Ist(Wed30Sep, 23, 50), CancellationToken.None));
        Assert.Equal(Ist(Wed30Sep, 23, 29), Candles(db, Crude, "1").Max());
    }

    [Fact]
    public void A_day_is_due_twenty_minutes_after_its_last_exchange_closes_and_never_before_the_run_time()
    {
        var sessions = new MarketSessionService(new Calendar());

        // MCX to 23:55 in the US winter: the next morning.
        Assert.Equal(IstLocal(Tue03Nov, 0, 15), ArchiveSchedule.DueAtIst(Mon02Nov, RunAt, sessions));
        // MCX to 23:30 in the US summer: the run time.
        Assert.Equal(IstLocal(Wed30Sep, 23, 50), ArchiveSchedule.DueAtIst(Wed30Sep, RunAt, sessions));
        // A Sunday: nothing trades, the run time.
        Assert.Equal(IstLocal(Sun01Nov, 23, 50), ArchiveSchedule.DueAtIst(Sun01Nov, RunAt, sessions));

        // MCX closed for the evening (it publishes its holidays per session): the equity close decides, and the run time is later.
        var eveningOff = new MarketSessionService(new Calendar(new MarketHoliday { Exchange = "MCX", Date = Mon02Nov, Name = "Evening off", Closure = MarketClosure.EveningSession }));
        Assert.Equal(IstLocal(Mon02Nov, 23, 50), ArchiveSchedule.DueAtIst(Mon02Nov, RunAt, eveningOff));

        // A run time set earlier than a close cannot cut the session short.
        Assert.Equal(IstLocal(Wed30Sep, 23, 50), ArchiveSchedule.DueAtIst(Wed30Sep, new TimeSpan(20, 0, 0), sessions));
    }

    [Fact]
    public void After_midnight_the_day_before_is_not_due_until_its_time()
    {
        var sessions = new MarketSessionService(new Calendar());
        DateTime Due(DateOnly day) => ArchiveSchedule.DueAtIst(day, RunAt, sessions);

        // 00:05 on 3 Nov: 2 Nov's archive waits for 00:15; 1 Nov's was due at 23:50 on the 1st.
        Assert.Equal([Sun01Nov], ArchiveSchedule.DueDays(null, IstLocal(Tue03Nov, 0, 5), Due));
        Assert.Empty(ArchiveSchedule.DueDays(Sun01Nov, IstLocal(Tue03Nov, 0, 5), Due));
        Assert.Equal([Mon02Nov], ArchiveSchedule.DueDays(Sun01Nov, IstLocal(Tue03Nov, 0, 15), Due));

        // A missed night is caught up in the morning, the cap still holding.
        Assert.Equal([Mon02Nov], ArchiveSchedule.DueDays(Sun01Nov, IstLocal(Tue03Nov, 9, 0), Due));
        Assert.Equal([Mon02Nov, Tue03Nov], ArchiveSchedule.DueDays(Sun01Nov, IstLocal(new DateOnly(2026, 11, 4), 6, 0), Due));
        Assert.Equal([Tue03Nov], ArchiveSchedule.DueDays(Sun01Nov, IstLocal(new DateOnly(2026, 11, 4), 6, 0), Due, maxDays: 1));
    }

    // ---------------------------------------------------------------------- helpers --

    /// <summary>The service over an in-memory database, archiving the live bars only (no broker in a test).</summary>
    private static NightlyArchiveService Archive(bool vixCheck, out TradingDbContext db)
    {
        string name = $"archive-{Guid.NewGuid():N}";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Archive:VixCheckEnabled"] = vixCheck ? "true" : "false" })
            .Build();
        var services = new ServiceCollection();
        services.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IMarketSessionService>(new MarketSessionService(new Calendar()));
        services.AddScoped<IDailyCandleArchiveService, LiveBarsOnly>();
        var provider = services.BuildServiceProvider();

        db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name).Options);
        return new NightlyArchiveService(NullLogger<NightlyArchiveService>.Instance, provider.GetRequiredService<IServiceScopeFactory>(), configuration);
    }

    private static void SeedLastArchived(TradingDbContext db, DateOnly lastArchived)
    {
        db.SystemSettings.Add(new SystemSetting { Key = NightlyArchiveService.LastArchivedDayKey, Value = lastArchived.ToString("yyyy-MM-dd") });
        db.SaveChanges();
    }

    /// <summary>One live 1-minute bar a minute from <paramref name="fromUtc"/> up to, not including, <paramref name="toUtc"/>.</summary>
    private static void SeedBars(TradingDbContext db, string symbol, DateTime fromUtc, DateTime toUtc)
    {
        for (var t = fromUtc; t < toUtc; t = t.AddMinutes(1))
        {
            db.LiveBars.Add(new LiveBar { Symbol = symbol, Resolution = "1m", BarStartUtc = t, Open = 100m, High = 101m, Low = 99m, Close = 100.5m, VolumeDelta = 10, TickCount = 5 });
        }
        db.SaveChanges();
    }

    private static List<DateTime> Candles(TradingDbContext db, string symbol, string resolution) =>
        db.Candles.AsNoTracking().Where(c => c.Symbol == symbol && c.Resolution == resolution).Select(c => c.TimeStampUtc).ToList();

    private sealed class LiveBarsOnly(TradingDbContext db, IConfiguration configuration) : IDailyCandleArchiveService
    {
        private readonly DailyCandleArchiveService _archive = new(db, null!, configuration, NullLogger<DailyCandleArchiveService>.Instance);

        public Task<CandleArchiveResult> ArchiveDayAsync(DateOnly istDay, bool includeBrokerBackfill, CancellationToken cancellationToken = default)
            => _archive.ArchiveDayAsync(istDay, includeBrokerBackfill: false, cancellationToken);
    }

    /// <summary>Weekends and these holidays closed; every year loaded.</summary>
    private sealed class Calendar(params MarketHoliday[] holidays) : IMarketCalendar
    {
        public bool IsLoaded => true;

        public MarketHoliday? HolidayOn(string exchange, DateOnly date) =>
            holidays.FirstOrDefault(h => h.Exchange == exchange && h.Date == date);

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
