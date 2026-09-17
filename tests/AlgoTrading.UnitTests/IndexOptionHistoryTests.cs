using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.OptionHistory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Expired index options for backtests: symbols in the master's grammar, the expiry
/// calendar file, which bars belong to which contract, the roll-up to the run's
/// resolution, and the history-aware contract lookups. March 2021 is used because
/// NIFTY's weekly expiry moved to Wednesday 10 Mar for the Mahashivratri holiday.
/// </summary>
public class IndexOptionHistoryTests
{
    private static readonly DateOnly[] March2021 =
    {
        new(2021, 3, 4), new(2021, 3, 10), new(2021, 3, 18), new(2021, 3, 25), new(2021, 4, 1)
    };

    private static DateTime Ist(int year, int month, int day, int hour, int minute) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    [Fact]
    public void Symbols_follow_the_masters_weekly_and_monthly_grammar()
    {
        Assert.Equal("NSE:NIFTY2691519050CE", IndexOptionSymbols.Format("NIFTY", new DateOnly(2026, 9, 15), 19050m, "CE", monthly: false));
        Assert.Equal("NSE:NIFTY26SEP19050PE", IndexOptionSymbols.Format("nifty", new DateOnly(2026, 9, 29), 19050m, "pe", monthly: true));
        Assert.Equal("NSE:NIFTY21O0717500CE", IndexOptionSymbols.Format("NIFTY", new DateOnly(2021, 10, 7), 17500m, "CE", monthly: false));
        Assert.Equal("BSE:SENSEX2380465800CE", IndexOptionSymbols.Format("SENSEX", new DateOnly(2023, 8, 4), 65800m, "CE", monthly: false));

        var weekly = UnderlyingCatalog.ParseOptionSymbol("NSE:NIFTY21O0717500CE")!;
        Assert.Equal(("NIFTY", 17500m, "CE", new DateOnly(2021, 10, 7), true),
            (weekly.Underlying, weekly.Strike, weekly.OptionType, weekly.Expiry!.Value, weekly.IsWeekly));
    }

    [Fact]
    public void The_calendar_file_is_read_by_underlying_in_date_order()
    {
        var calendar = IndexOptionExpiryCalendar.Parse("""
            {"generatedUtc": "2026-09-17T18:00:00+00:00",
             "underlyings": {"NIFTY": ["2021-03-10", "2021-03-04"], "SENSEX": ["2023-08-04"]}}
            """);
        Assert.Equal(new[] { new DateOnly(2021, 3, 4), new DateOnly(2021, 3, 10) }, calendar["nifty"]);
        Assert.Single(calendar["SENSEX"]);
    }

    [Fact]
    public void The_shipped_calendar_holds_the_exchanges_moved_expiries()
    {
        // Walk up from the test binary to the repository, where the API's seed file lives.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "AlgoTrading.Api", "SeedData", IndexOptionExpiryCalendar.FileName)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var calendar = IndexOptionExpiryCalendar.Parse(File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "AlgoTrading.Api", "SeedData", IndexOptionExpiryCalendar.FileName)));

        Assert.Contains(new DateOnly(2021, 3, 10), calendar["NIFTY"]);        // Wednesday: 11 Mar 2021 was Mahashivratri
        Assert.DoesNotContain(new DateOnly(2021, 3, 11), calendar["NIFTY"]);
        Assert.Contains(new DateOnly(2024, 10, 1), calendar["BANKNIFTY"]);    // Tuesday: 2 Oct was a holiday
        Assert.Contains(new DateOnly(2023, 5, 19), calendar["SENSEX"]);       // first weekly SENSEX expiry after the relaunch
        // BANKNIFTY weeklies stopped after 13 Nov 2024; the next expiry is the month's last.
        var next = calendar["BANKNIFTY"].First(x => x > new DateOnly(2024, 11, 13));
        Assert.Equal(new DateOnly(2024, 11, 27), next);
    }

    [Fact]
    public void A_contracts_bars_are_the_days_after_the_previous_expiry_through_its_own()
    {
        var (from, to) = IndexOptionHistory.Window(new DateOnly(2021, 3, 4), new DateOnly(2021, 3, 10));
        Assert.Equal(new DateTime(2021, 3, 4, 18, 30, 0, DateTimeKind.Utc), from);   // 5 Mar 00:00 IST
        Assert.Equal(new DateTime(2021, 3, 10, 18, 30, 0, DateTimeKind.Utc), to);    // 11 Mar 00:00 IST
    }

    [Fact]
    public void Minutes_roll_up_from_the_open_and_a_day_is_stamped_midnight_utc()
    {
        var bars = new List<IndexOptionHistory.MinuteBar>
        {
            new(Ist(2021, 3, 8, 9, 15), 100, 104, 99, 103, 10),
            new(Ist(2021, 3, 8, 9, 15), 555, 555, 555, 555, 999),   // a repeated minute keeps its first row
            new(Ist(2021, 3, 8, 9, 19), 103, 108, 101, 107, 5),
            new(Ist(2021, 3, 8, 9, 20), 107, 107, 90, 91, 7),
            new(Ist(2021, 3, 8, 15, 29), 91, 95, 88, 94, 1),
        };

        var five = IndexOptionHistory.RollUp("S", bars, "5");
        Assert.Equal(new[] { Ist(2021, 3, 8, 9, 15), Ist(2021, 3, 8, 9, 20), Ist(2021, 3, 8, 15, 25) }, five.Select(x => x.TimestampUtc));
        Assert.Equal((100m, 108m, 99m, 107m, 15m), (five[0].Open, five[0].High, five[0].Low, five[0].Close, five[0].Volume));

        var day = Assert.Single(IndexOptionHistory.RollUp("S", bars, "D"));
        Assert.Equal(new DateTime(2021, 3, 8, 0, 0, 0, DateTimeKind.Utc), day.TimestampUtc);
        Assert.Equal((100m, 108m, 88m, 94m, 23m), (day.Open, day.High, day.Low, day.Close, day.Volume));
        Assert.Equal(4, IndexOptionHistory.RollUp("S", bars, "1").Count);
    }

    [Fact]
    public async Task An_expired_contract_is_found_and_priced_only_from_its_own_weeks_bars()
    {
        await using var db = NewDb();
        // 15100 CE: on 4 Mar (the previous contract's expiry day) and on 8 Mar (this one's week).
        db.OptionHistoryBars.AddRange(
            Bar(Ist(2021, 3, 4, 15, 29), 15100m, "CE", close: 2m),
            Bar(Ist(2021, 3, 8, 9, 15), 15100m, "CE", close: 120m),
            Bar(Ist(2021, 3, 8, 9, 16), 15100m, "CE", close: 125m),
            Bar(Ist(2021, 3, 18, 9, 15), 15100m, "PE", close: 80m));
        await db.SaveChangesAsync();
        var history = new IndexOptionHistory(db, Calendar());

        var weekly = await history.FindContractAsync("NIFTY", new DateOnly(2021, 3, 10), 15100m, "CE", default);
        Assert.Equal("NSE:NIFTY2131015100CE", weekly!.Symbol);
        Assert.Null(await history.FindContractAsync("NIFTY", new DateOnly(2021, 3, 10), 15200m, "CE", default));
        Assert.Null(await history.FindContractAsync("NIFTY", new DateOnly(2021, 3, 11), 15100m, "CE", default));   // not an expiry

        var candles = await history.CandlesAsync(weekly.Symbol, "1", new DateOnly(2021, 3, 1), new DateOnly(2021, 3, 31), default);
        Assert.Equal(new[] { 120m, 125m }, candles.Select(x => x.Close));   // the 4 Mar bar belongs to the 4 Mar contract

        // 18 Mar is followed by 25 Mar in the same month, so it is weekly; 25 Mar is the monthly.
        Assert.Equal("NSE:NIFTY2131815100PE", (await history.FindContractAsync("NIFTY", new DateOnly(2021, 3, 18), 15100m, "PE", default))!.Symbol);
        Assert.Empty(await history.CandlesAsync("NSE:NIFTY21MAR15100PE", "5", null, null, default));   // monthly = 25 Mar: nothing stored
    }

    [Fact]
    public async Task Backtest_lookups_add_expired_expiries_and_contracts_only_when_asked()
    {
        await using var db = NewDb();
        db.Instruments.Add(new Instrument
        {
            Symbol = "NSE:NIFTY26SEP23500CE", Underlying = "NIFTY", InstrumentType = "CE", OptionType = "CE",
            ExpiryDate = new DateOnly(2026, 9, 29), StrikePrice = 23500m, IsEnabled = true
        });
        db.OptionHistoryBars.Add(Bar(Ist(2021, 3, 8, 9, 15), 15100m, "CE", close: 120m));
        await db.SaveChangesAsync();
        var service = new DerivativesInstrumentService(db, new IndexOptionHistory(db, Calendar()));

        Assert.Equal(new[] { new DateOnly(2026, 9, 29) }, (await service.GetExpiriesAsync("NIFTY")).Select(x => x.ExpiryDate));
        var all = (await service.GetExpiriesAsync("NIFTY", includeHistory: true)).Select(x => x.ExpiryDate).ToList();
        Assert.Equal(March2021.Length + 1, all.Count);
        Assert.Equal(new DateOnly(2021, 3, 4), all[0]);

        Assert.Null(await service.GetExactContractAsync("NIFTY", new DateOnly(2021, 3, 10), 15100m, "CE"));
        Assert.Equal("NSE:NIFTY2131015100CE",
            (await service.GetExactContractAsync("NIFTY", new DateOnly(2021, 3, 10), 15100m, "CE", includeHistory: true))!.Symbol);
        Assert.Equal("NSE:NIFTY26SEP23500CE",
            (await service.GetExactContractAsync("NIFTY", new DateOnly(2026, 9, 29), 23500m, "CE", includeHistory: true))!.Symbol);
    }

    [Fact]
    public void Stored_candles_win_over_history_bars_at_the_same_start()
    {
        CandleResponse C(int minute, decimal close) => new() { TimestampUtc = Ist(2026, 9, 8, 9, minute), Close = close };
        var merged = MarketDataService.MergeCandles(new[] { C(16, 1m) }, new[] { C(15, 10m), C(16, 20m), C(17, 30m) });
        Assert.Equal(new[] { 10m, 1m, 30m }, merged.Select(x => x.Close));
    }

    private static IndexOptionExpiryCalendar Calendar() =>
        IndexOptionExpiryCalendar.From(new Dictionary<string, IEnumerable<DateOnly>> { ["NIFTY"] = March2021 });

    private static OptionHistoryBar Bar(DateTime startUtc, decimal strike, string side, decimal close) => new()
    {
        BarStartUtc = startUtc, Underlying = "NIFTY", ExpiryFlag = "WEEK", ExpiryCode = 1, StrikeOffset = 0,
        Strike = strike, OptionType = side, Resolution = "1m", Open = close, High = close, Low = close, Close = close,
        Volume = 1, SourceKey = "dhan"
    };

    private static TradingDbContext NewDb() => new(new DbContextOptionsBuilder<TradingDbContext>()
        .UseInMemoryDatabase($"option-history-{Guid.NewGuid():N}").Options);
}
