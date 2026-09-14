using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The market must be known to be closed on an exchange holiday.
///
/// On 2026-09-14 (Ganesh Chaturthi) the session service knew only weekends: the
/// morning job restarted the API and waited for a broker sign-in until 14:30,
/// and the feeds took a closed market for a silent one. These pin the calendar:
/// holidays per exchange, MCX's half-day closures, special sessions, and a
/// missing year reported instead of assumed open.
/// </summary>
public class MarketHolidayTests
{
    private static readonly DateOnly GaneshChaturthi = new(2026, 9, 14); // Monday

    /// <summary>IST wall time → the UTC instant the service is asked about.</summary>
    private static DateTime Ist(int y, int mo, int d, int h, int mi = 0)
        => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static MarketHoliday Holiday(string exchange, DateOnly date, string name, MarketClosure closure = MarketClosure.FullDay)
        => new() { Exchange = exchange, Date = date, Name = name, Closure = closure, Source = "test" };

    private sealed class FakeCalendar(IEnumerable<MarketHoliday> holidays, IEnumerable<MarketSpecialSession>? sessions = null, bool loaded = true) : IMarketCalendar
    {
        private readonly List<MarketHoliday> _holidays = holidays.ToList();
        private readonly List<MarketSpecialSession> _sessions = (sessions ?? []).ToList();
        public bool IsLoaded => loaded;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => _holidays.FirstOrDefault(h => h.Exchange == exchange && h.Date == date);
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => _sessions.FirstOrDefault(s => s.Exchange == exchange && s.Date == date);
        public bool HasYear(string exchange, int year) => _holidays.Any(h => h.Exchange == exchange && h.Date.Year == year);
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static MarketSessionService Service(params MarketHoliday[] holidays) => new(new FakeCalendar(holidays));

    [Fact]
    public void An_exchange_holiday_is_not_a_trading_day_and_says_why()
    {
        var info = Service(Holiday("NSE", GaneshChaturthi, "Ganesh Chaturthi"))
            .GetSessionInfo(Ist(2026, 9, 14, 10), "NSE", "CM");

        Assert.False(info.IsTradingDay);
        Assert.False(info.IsMarketOpen);
        Assert.True(info.IsHoliday);
        Assert.Equal("Ganesh Chaturthi", info.HolidayName);
        Assert.Equal(Ist(2026, 9, 15, 9, 15), info.NextMarketOpenUtc);
        Assert.Null(info.CalendarWarning);
    }

    [Fact]
    public void The_next_open_skips_a_weekend_and_a_holiday_together()
    {
        // Friday after the close; Saturday, Sunday and the Monday holiday follow.
        var info = Service(Holiday("NSE", GaneshChaturthi, "Ganesh Chaturthi"))
            .GetSessionInfo(Ist(2026, 9, 11, 16), "NSE", "FO");

        Assert.Equal(Ist(2026, 9, 15, 9, 15), info.NextMarketOpenUtc);
    }

    [Fact]
    public void Each_exchange_is_answered_from_its_own_list()
    {
        var service = Service(Holiday("NSE", GaneshChaturthi, "Ganesh Chaturthi"), Holiday("BSE", new DateOnly(2026, 1, 26), "Republic Day"));

        Assert.False(service.IsMarketOpen(Ist(2026, 9, 14, 10), "NSE", "CM"));
        Assert.True(service.IsMarketOpen(Ist(2026, 9, 14, 10), "BSE", "CM"));
    }

    [Fact]
    public void MCX_closed_in_the_morning_trades_the_evening()
    {
        var service = Service(Holiday("MCX", GaneshChaturthi, "Ganesh Chaturthi", MarketClosure.MorningSession));

        var morning = service.GetSessionInfo(Ist(2026, 9, 14, 10), "MCX", "COM");
        Assert.True(morning.IsTradingDay);
        Assert.False(morning.IsMarketOpen);
        Assert.Equal(Ist(2026, 9, 14, 17), morning.NextMarketOpenUtc);

        Assert.True(service.IsMarketOpen(Ist(2026, 9, 14, 18), "MCX", "COM"));
    }

    [Fact]
    public void MCX_closed_in_the_evening_stops_at_five()
    {
        var service = Service(Holiday("MCX", GaneshChaturthi, "Ganesh Chaturthi", MarketClosure.EveningSession));

        Assert.True(service.IsMarketOpen(Ist(2026, 9, 14, 16), "MCX", "COM"));
        var evening = service.GetSessionInfo(Ist(2026, 9, 14, 18), "MCX", "COM");
        Assert.False(evening.IsMarketOpen);
        Assert.Equal(Ist(2026, 9, 15, 9), evening.NextMarketOpenUtc);
    }

    [Fact]
    public void A_special_session_opens_a_Sunday()
    {
        var sunday = new DateOnly(2026, 11, 8);
        var calendar = new FakeCalendar(
            [Holiday("NSE", new DateOnly(2026, 1, 26), "Republic Day")],
            [new MarketSpecialSession { Exchange = "NSE", Date = sunday, Name = "Muhurat trading", OpenIst = new TimeOnly(18, 0), CloseIst = new TimeOnly(19, 0) }]);
        var service = new MarketSessionService(calendar);

        var before = service.GetSessionInfo(Ist(2026, 11, 8, 17), "NSE", "CM");
        Assert.True(before.IsTradingDay);
        Assert.False(before.IsMarketOpen);
        Assert.Equal("Muhurat trading", before.SpecialSessionName);
        Assert.Equal(Ist(2026, 11, 8, 18), before.NextMarketOpenUtc);

        Assert.True(service.IsMarketOpen(Ist(2026, 11, 8, 18, 30), "NSE", "CM"));
    }

    [Fact]
    public void A_year_with_no_calendar_is_reported_not_assumed_open()
    {
        var service = Service(Holiday("NSE", GaneshChaturthi, "Ganesh Chaturthi"));

        // Republic Day 2027 is a Tuesday. Without the 2027 list the service can
        // only apply the weekday rule, and it must say so.
        var info = service.GetSessionInfo(Ist(2027, 1, 26, 10), "NSE", "CM");
        Assert.True(info.IsMarketOpen);
        Assert.Contains("2027", info.CalendarWarning);
    }

    [Fact]
    public void December_warns_that_next_years_list_is_missing()
    {
        var info = Service(Holiday("NSE", GaneshChaturthi, "Ganesh Chaturthi"))
            .GetSessionInfo(Ist(2026, 12, 15, 10), "NSE", "CM");

        Assert.Contains("2027", info.CalendarWarning);
    }

    [Fact]
    public void A_calendar_that_did_not_load_is_reported()
    {
        var info = new MarketSessionService(new FakeCalendar([], loaded: false))
            .GetSessionInfo(Ist(2026, 9, 14, 10), "NSE", "CM");

        Assert.Contains("could not be loaded", info.CalendarWarning);
    }

    [Fact]
    public async Task The_calendar_reads_every_row_and_normalises_the_exchange()
    {
        var (calendar, db) = BuildCalendar();
        db.MarketHolidays.Add(Holiday("nse ", GaneshChaturthi, "Ganesh Chaturthi"));
        db.SaveChanges();

        Assert.False(calendar.IsLoaded);
        await calendar.RefreshAsync();

        Assert.True(calendar.IsLoaded);
        Assert.Equal("Ganesh Chaturthi", calendar.HolidayOn("NSE", GaneshChaturthi)?.Name);
        Assert.True(calendar.HasYear("nse", 2026));
        Assert.False(calendar.HasYear("NSE", 2027));

        db.MarketHolidays.RemoveRange(db.MarketHolidays);
        db.SaveChanges();
        await calendar.RefreshAsync();
        Assert.Null(calendar.HolidayOn("NSE", GaneshChaturthi));
    }

    [Fact]
    public async Task The_seed_fills_a_year_once_and_never_undoes_an_admin_change()
    {
        var root = Directory.CreateTempSubdirectory("calendar-seed-");
        Directory.CreateDirectory(Path.Combine(root.FullName, "SeedData"));
        await File.WriteAllTextAsync(Path.Combine(root.FullName, "SeedData", "market_calendar.json"), """
            {
              "holidays": [
                { "exchange": "NSE", "date": "2026-09-14", "name": "Ganesh Chaturthi", "closure": "FullDay", "source": "test" },
                { "exchange": "NSE", "date": "2026-10-02", "name": "Gandhi Jayanti", "closure": "FullDay", "source": "test" },
                { "exchange": "MCX", "date": "2026-09-14", "name": "Ganesh Chaturthi", "closure": "MorningSession", "source": "test" }
              ],
              "specialSessions": [
                { "exchange": "NSE", "date": "2026-11-08", "name": "Muhurat trading", "openIst": "18:00", "closeIst": "19:00", "source": "test" }
              ]
            }
            """);

        var (_, db) = BuildCalendar();
        var seeder = new ReferenceDataSeeder(db, new Env(root.FullName), NullLogger<ReferenceDataSeeder>.Instance);

        await seeder.SeedMarketCalendarAsync(CancellationToken.None);
        Assert.Equal(3, db.MarketHolidays.Count());
        Assert.Equal(MarketClosure.MorningSession, db.MarketHolidays.Single(x => x.Exchange == "MCX").Closure);
        Assert.Equal(1, db.MarketSpecialSessions.Count());

        // The exchange cancels Gandhi Jayanti; an admin deletes it. A restart must not bring it back.
        db.MarketHolidays.Remove(db.MarketHolidays.Single(x => x.Name == "Gandhi Jayanti"));
        db.SaveChanges();

        await seeder.SeedMarketCalendarAsync(CancellationToken.None);
        Assert.Equal(2, db.MarketHolidays.Count());
        Assert.Equal(1, db.MarketSpecialSessions.Count());
    }

    private static (MarketCalendar Calendar, TradingDbContext Db) BuildCalendar()
    {
        var name = $"calendar-{Guid.NewGuid():N}";
        var services = new ServiceCollection()
            .AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(name))
            .BuildServiceProvider();
        var calendar = new MarketCalendar(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<MarketCalendar>.Instance);
        var db = services.CreateScope().ServiceProvider.GetRequiredService<TradingDbContext>();
        return (calendar, db);
    }

    private sealed class Env(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "AlgoTrading.UnitTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
