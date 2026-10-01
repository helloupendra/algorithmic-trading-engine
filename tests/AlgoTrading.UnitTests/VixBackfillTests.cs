using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The nightly India VIX check: when it runs, which days it looks at, what a
/// complete day is, that it fills only the missing bars and nothing twice, and
/// that a gap no vendor can fill reaches the System channel once.
/// </summary>
/// <remarks>
/// The weeks of 21 Sep and 28 Sep 2026, with Friday 2 Oct (Gandhi Jayanti) an
/// exchange holiday. Incident #204 (1 Oct 2026) was India VIX missing on 22,
/// 23 and 24 Sep; 23 Sep had no bar at all and 24 Sep stopped late in the
/// morning, which is what these days are seeded to look like.
/// </remarks>
public class VixBackfillTests
{
    private static readonly DateOnly Mon21 = new(2026, 9, 21);
    private static readonly DateOnly Tue22 = new(2026, 9, 22);
    private static readonly DateOnly Wed23 = new(2026, 9, 23);
    private static readonly DateOnly Thu24 = new(2026, 9, 24);
    private static readonly DateOnly Fri25 = new(2026, 9, 25);
    private static readonly DateOnly Wed30 = new(2026, 9, 30);
    private static readonly DateOnly Thu01 = new(2026, 10, 1);
    private static readonly DateOnly GandhiJayanti = new(2026, 10, 2);
    private static readonly DateOnly Sat03 = new(2026, 10, 3);
    private static readonly DateOnly Mon05 = new(2026, 10, 5);

    private static readonly DateOnly[] Week = [Mon21, Tue22, Wed23, Thu24, Fri25];

    private static bool TradingDay(DateOnly day)
        => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && day != GandhiJayanti;

    private static DateTime Ist(DateOnly day, int hour, int minute)
        => DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute)).AddMinutes(-330), DateTimeKind.Utc);

    private static IReadOnlyList<DateTime> Session(DateOnly day, int minutes)
        => VixBackfillPlan.ExpectedBarStarts(Ist(day, 9, 15), Ist(day, 15, 30), minutes);

    // ------------------------------------------------------------ when and which days --

    [Fact]
    public void The_check_runs_through_the_latest_trading_day_the_archive_just_did()
    {
        Assert.Equal(Thu01, VixBackfillPlan.CheckThrough([Thu01], TradingDay));

        // 23:50 on a holiday or a Saturday: nothing to check.
        Assert.Null(VixBackfillPlan.CheckThrough([GandhiJayanti], TradingDay));
        Assert.Null(VixBackfillPlan.CheckThrough([Sat03], TradingDay));
        Assert.Null(VixBackfillPlan.CheckThrough([], TradingDay));

        // The API was down from Thursday night to Saturday night: the catch-up
        // archives three days, and the check runs once, through Thursday.
        Assert.Equal(Thu01, VixBackfillPlan.CheckThrough([Thu01, GandhiJayanti, Sat03], TradingDay));
    }

    [Fact]
    public void The_window_counts_trading_days_and_skips_weekends_and_holidays()
    {
        Assert.Equal(new[] { Wed30, Thu01, Mon05 }, VixBackfillPlan.TradingDaysThrough(Mon05, 3, TradingDay));
        Assert.Equal(Week, VixBackfillPlan.TradingDaysThrough(Fri25, 5, TradingDay));
    }

    [Fact]
    public void A_calendar_with_no_trading_day_returns_nothing_rather_than_looping()
    {
        Assert.Empty(VixBackfillPlan.TradingDaysThrough(Mon05, 20, _ => false));
    }

    [Fact]
    public void A_session_expects_375_one_minute_75_five_minute_and_25_fifteen_minute_bars()
    {
        Assert.Equal(375, Session(Thu24, 1).Count);
        Assert.Equal(75, Session(Thu24, 5).Count);
        Assert.Equal(25, Session(Thu24, 15).Count);
        Assert.Equal(Ist(Thu24, 9, 15), Session(Thu24, 5)[0]);
        Assert.Equal(Ist(Thu24, 15, 25), Session(Thu24, 5)[^1]);
        Assert.Equal(Ist(Thu24, 15, 15), Session(Thu24, 15)[^1]);
    }

    // ----------------------------------------------------------------- a day's state --

    [Fact]
    public void A_day_with_every_bar_is_complete()
    {
        var bars = Session(Thu24, 5);
        Assert.Equal(VixDayState.Complete, VixBackfillPlan.Classify(Thu24, 5, bars, bars.ToHashSet()).State);
    }

    [Fact]
    public void A_day_short_of_a_few_bars_in_the_middle_still_counts()
    {
        var bars = Session(Thu24, 5);
        var stored = bars.Where((_, i) => i is < 30 or >= 36).ToHashSet();   // 69 of 75, 11:45 to 12:10 missing

        var day = VixBackfillPlan.Classify(Thu24, 5, bars, stored);

        Assert.Equal(VixDayState.Holes, day.State);
        Assert.Equal(6, day.Missing);
    }

    [Theory]
    [InlineData(6, 0, "69 of 75, but the first bar is 09:45: the session's open is not there")]
    [InlineData(0, 4, "71 of 75, but the last bar is 15:05: the close is not there")]
    [InlineData(0, 48, "27 of 75: the 24 Sep shape, stopped late in the morning")]
    [InlineData(75, 0, "nothing: the 23 Sep shape")]
    public void A_day_the_forecasts_would_drop_is_a_gap(int fromStart, int fromEnd, string why)
    {
        var bars = Session(Thu24, 5);
        var stored = bars.Skip(fromStart).Take(Math.Max(0, bars.Count - fromStart - fromEnd)).ToHashSet();

        Assert.True(VixBackfillPlan.Classify(Thu24, 5, bars, stored).State == VixDayState.Gap, why);
    }

    [Fact]
    public void Bars_outside_the_session_do_not_count()
    {
        var bars = Session(Thu24, 5);
        var stored = bars.ToHashSet();
        stored.Remove(bars[40]);
        stored.Add(Ist(Thu24, 15, 30));   // an after-close flat bar is not the missing 12:35

        Assert.Equal(74, VixBackfillPlan.Classify(Thu24, 5, bars, stored).Present);
    }

    // ------------------------------------------------------------------------ requests --

    [Fact]
    public void Missing_days_merge_into_runs_of_consecutive_trading_days()
    {
        Assert.Equal(new[] { (Tue22, Thu24) }, VixBackfillPlan.FetchRuns(Week, new HashSet<DateOnly> { Tue22, Wed23, Thu24 }));
        Assert.Equal(new[] { (Tue22, Tue22), (Thu24, Thu24) }, VixBackfillPlan.FetchRuns(Week, new HashSet<DateOnly> { Tue22, Thu24 }));

        // Friday and the next Monday are neighbours: the weekend is not a reason for a second request.
        var days = VixBackfillPlan.TradingDaysThrough(Mon05, 3, TradingDay);
        Assert.Equal(new[] { (Thu01, Mon05) }, VixBackfillPlan.FetchRuns(days, new HashSet<DateOnly> { Thu01, Mon05 }));
        Assert.Empty(VixBackfillPlan.FetchRuns(days, new HashSet<DateOnly>()));
    }

    [Fact]
    public void A_gap_already_reported_is_not_new()
    {
        var reported = VixBackfillPlan.ParseDays("2026-09-22,not-a-date, 2026-09-23");
        Assert.Equal(new[] { Tue22, Wed23 }, reported.OrderBy(d => d));
        Assert.Equal(new[] { Thu24 }, VixBackfillPlan.NewGaps([Wed23, Thu24, Thu24], reported));
        Assert.Equal("2026-09-22,2026-09-23", VixBackfillPlan.FormatDays([Wed23, Tue22, Wed23]));
        Assert.Empty(VixBackfillPlan.ParseDays(null));
    }

    // ------------------------------------------------------------------ the service --

    [Fact]
    public async Task Only_the_missing_bars_are_written_and_a_second_run_writes_nothing()
    {
        using var db = Db();
        SeedIncidentWeek(db);
        var fyers = Vendor.Full("fyers");
        var service = Service(db, fyers);

        var result = await service.RunAsync(Fri25, 5);

        Assert.Equal(Week, result.Days);
        Assert.Empty(result.GapDays);
        Assert.Empty(result.Holes);
        // 23 Sep whole (375 + 75 + 25) and the rest of 24 Sep (240 + 48 + 16).
        Assert.Equal(475 + 304, result.BarsFilled);

        // One request per resolution: 23 and 24 Sep are one run.
        Assert.Equal(3, fyers.Calls.Count);
        Assert.All(fyers.Calls, call => Assert.Equal((Ist(Wed23, 0, 0), Ist(Thu24, 23, 59).AddSeconds(59)), (call.From, call.To)));

        foreach (int minutes in new[] { 1, 5, 15 })
        {
            foreach (var day in Week)
                Assert.Equal(Session(day, minutes).Count, Stored(db, day, minutes).Count);
        }

        // The live bars of the 24th are untouched; only what was missing came from the vendor.
        var thursday = Stored(db, Thu24, 5);
        Assert.Equal(27, thursday.Count(c => c.SourceKey == "live" && c.Close == 11m));
        Assert.Equal(48, thursday.Count(c => c.SourceKey == "fyers" && c.Close == 99m));
        // The vendor's pre-open and after-close bars were not taken.
        Assert.DoesNotContain(db.Candles, c => c.TimeStampUtc == Ist(Wed23, 9, 10) || c.TimeStampUtc == Ist(Wed23, 15, 30));

        // Idempotent: nothing missing, nothing asked, nothing written.
        var again = await Service(db, fyers).RunAsync(Fri25, 5);
        Assert.Equal(3, fyers.Calls.Count);
        Assert.Equal(0, again.BarsFilled);
        Assert.Empty(again.Fetches);
    }

    [Fact]
    public async Task A_vendor_that_fails_hands_over_to_the_next()
    {
        using var db = Db();
        SeedIncidentWeek(db);
        var fyers = Vendor.Failing("fyers", new InvalidOperationException("the FYERS token has expired"));
        var dhan = Vendor.Full("dhan");

        var result = await Service(db, fyers, dhan).RunAsync(Fri25, 5);

        Assert.Empty(result.GapDays);
        Assert.Equal(75, Stored(db, Wed23, 5).Count(c => c.SourceKey == "dhan"));
        Assert.Contains(result.Fetches, line => line.StartsWith("fyers 5m 2026-09-23..2026-09-24: failed (the FYERS token has expired)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_connector_that_reads_the_platforms_own_table_is_never_asked()
    {
        using var db = Db();
        SeedIncidentWeek(db);
        var replay = Vendor.Full("replay", platformStore: true);
        var fyers = Vendor.Full("fyers");

        await Service(db, replay, fyers).RunAsync(Fri25, 5);

        Assert.Empty(replay.Calls);
        Assert.Equal(3, fyers.Calls.Count);
    }

    [Fact]
    public async Task A_holiday_expects_nothing_and_a_complete_window_asks_nobody()
    {
        using var db = Db();
        foreach (var day in new[] { Wed30, Thu01, Mon05 }) Seed(db, day, _ => true);
        var fyers = Vendor.Full("fyers");

        var result = await Service(db, fyers).RunAsync(Mon05, 3);

        Assert.Equal(new[] { Wed30, Thu01, Mon05 }, result.Days);
        Assert.DoesNotContain(GandhiJayanti, result.Days);
        Assert.Empty(fyers.Calls);
        Assert.All(result.After, day => Assert.Equal(VixDayState.Complete, day.State));
    }

    [Fact]
    public async Task A_gap_no_vendor_can_fill_is_sent_once_and_cleared_when_filled()
    {
        using var db = Db();
        SeedIncidentWeek(db);
        var settings = new ProcessSettingsStore(db);
        var notifier = new Notifier();
        var log = new ListLogger();

        // Night one: the vendor answers, but with nothing for those days.
        var empty = Vendor.Empty("fyers");
        var night1 = await Service(db, empty).RunAsync(Fri25, 5);
        await VixBackfillReport.ReportAsync(night1, settings, notifier, log, CancellationToken.None);

        Assert.Equal(new[] { Wed23, Thu24 }, night1.GapDays);
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal((NotificationSeverity.Error, VixBackfillReport.GapTitle), (sent.Severity, sent.Title));
        Assert.Contains("2026-09-23 (1m 0/375, 5m 0/75, 15m 0/25)", sent.Message);
        Assert.Contains("2026-09-24 (1m 135/375, 5m 27/75, 15m 9/25)", sent.Message);
        Assert.Contains("fyers 5m 2026-09-23..2026-09-24: 0 bars returned, 0 of 123 missing filled", sent.Message);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Text.StartsWith("India VIX has a gap", StringComparison.Ordinal));
        Assert.Equal("2026-09-23,2026-09-24", await settings.GetAsync(SystemSettingKeys.VixGapsReported));

        // Night two: still nothing. Asked again, logged as a warning, not sent again.
        var night2 = await Service(db, empty).RunAsync(Fri25, 5);
        await VixBackfillReport.ReportAsync(night2, settings, notifier, log, CancellationToken.None);

        Assert.Single(notifier.Sent);
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("still has a gap on 2026-09-23, 2026-09-24", StringComparison.Ordinal));

        // Night three: a vendor has the bars. Filled, nothing sent, the list emptied.
        var night3 = await Service(db, Vendor.Full("dhan")).RunAsync(Fri25, 5);
        await VixBackfillReport.ReportAsync(night3, settings, notifier, log, CancellationToken.None);

        Assert.Empty(night3.GapDays);
        Assert.Single(notifier.Sent);
        Assert.Equal(string.Empty, await settings.GetAsync(SystemSettingKeys.VixGapsReported));
        Assert.Contains(log.Lines, l => l.Text == "India VIX gap now filled: 2026-09-23, 2026-09-24.");
    }

    [Fact]
    public async Task A_day_short_of_a_few_bars_is_logged_not_sent()
    {
        using var db = Db();
        foreach (var day in Week)
            Seed(db, day, t => day != Thu24 || t < Ist(Thu24, 12, 0) || t >= Ist(Thu24, 12, 10));
        var notifier = new Notifier();
        var log = new ListLogger();

        var result = await Service(db, Vendor.Empty("fyers")).RunAsync(Fri25, 5);
        await VixBackfillReport.ReportAsync(result, new ProcessSettingsStore(db), notifier, log, CancellationToken.None);

        Assert.Empty(result.GapDays);
        Assert.Equal(new[] { "2026-09-24 1m 365/375", "2026-09-24 5m 73/75", "2026-09-24 15m 24/25" }, result.Holes.Select(h => h.ToString()));
        Assert.Empty(notifier.Sent);
        Assert.DoesNotContain(log.Lines, l => l.Level >= LogLevel.Warning);
        Assert.Null(await new ProcessSettingsStore(db).GetAsync(SystemSettingKeys.VixGapsReported));
    }

    [Fact]
    public async Task A_vendor_s_error_page_cannot_stop_the_gap_message_reaching_Telegram()
    {
        using var db = Db();
        // Sixty sessions with no VIX at all, and every vendor failing: FYERS behind a proxy that answered 502 with its
        // own page (the provider puts the body in its error), Dhan with an error that reads like a tag.
        string page = "<html><head><title>502 Bad Gateway</title></head><body>" + new string('x', 3000) + "</body></html>";
        var fyers = Vendor.Failing("fyers", new InvalidOperationException($"FYERS history API failed for NSE:INDIAVIX-INDEX. HTTP 502: {page}"));
        var dhan = Vendor.Failing("dhan", new InvalidOperationException("DH-905: <securityId> & <exchangeSegment> are required"));
        var notifier = new Notifier();
        var log = new ListLogger();

        var result = await Service(db, fyers, dhan).RunAsync(Mon05, VixBackfillPlan.MaxLookbackTradingDays);
        await VixBackfillReport.ReportAsync(result, new ProcessSettingsStore(db), notifier, log, CancellationToken.None);

        Assert.Equal(60, result.GapDays.Count);
        var sent = Assert.Single(notifier.Sent);
        // Telegram reads it as HTML: a bare "<" or "&" makes it refuse the whole message, and so does one over 4,096
        // characters (with the subscriber's "ALERT" line in front of it).
        Assert.DoesNotContain("<", sent.Message);
        Assert.DoesNotMatch("&(?!amp;|lt;|gt;)", sent.Message);
        Assert.InRange(sent.Message.Length, 1, 3800);
        Assert.Contains("HTTP 502: &lt;html&gt;&lt;head&gt;&lt;title&gt;502 Bad Gateway", sent.Message);
        Assert.Contains("DH-905: &lt;securityId&gt; &amp; &lt;exchangeSegment&gt; are required", sent.Message);
        // Every gap day is still named, and the log keeps the vendors' words as they came.
        Assert.All(result.GapDays, day => Assert.Contains(day.ToString("yyyy-MM-dd"), sent.Message));
        Assert.Contains(log.Lines, l => l.Level == LogLevel.Error && l.Text.Contains("DH-905: <securityId> & <exchangeSegment>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_failed_check_s_message_is_safe_for_Telegram()
    {
        var error = new InvalidOperationException("'<' is an invalid start of a value. Path: $ | LineNumber: 0 & more");

        string message = VixBackfillReport.FailedMessage(Thu01, error);

        Assert.StartsWith("The nightly India VIX check through 2026-10-01 stopped: '&lt;' is an invalid start of a value. Path: $ | LineNumber: 0 &amp; more.", message);
        Assert.DoesNotContain("<", message);
    }

    // ----------------------------------------------------------------------- helpers --

    private static TradingDbContext Db()
        => new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"vix-{Guid.NewGuid():N}").Options);

    private static VixBackfillService Service(TradingDbContext db, params IMarketDataProvider[] chain)
        => new(db, new Router(chain), new HistoricalCandleStore(db), new MarketSessionService(new Calendar()),
               NullLogger<VixBackfillService>.Instance);

    /// <summary>Every session bar at 1, 5 and 15 minutes for the day, where <paramref name="keep"/> says so; source "live", close 11.</summary>
    private static void Seed(TradingDbContext db, DateOnly day, Func<DateTime, bool> keep)
    {
        foreach (int minutes in new[] { 1, 5, 15 })
        {
            foreach (var start in Session(day, minutes).Where(keep))
            {
                db.Candles.Add(new Candle
                {
                    Symbol = VixBackfillPlan.Symbol, Resolution = minutes.ToString(), TimeStampUtc = start,
                    Open = 11m, High = 11.2m, Low = 10.9m, Close = 11m, Volume = 0, SourceKey = "live",
                });
            }
        }
        db.SaveChanges();
    }

    /// <summary>The incident's week: 23 Sep empty, 24 Sep stopping at 11:30, the other days whole.</summary>
    private static void SeedIncidentWeek(TradingDbContext db)
    {
        foreach (var day in Week)
        {
            if (day == Wed23) continue;
            Seed(db, day, t => day != Thu24 || t < Ist(Thu24, 11, 30));
        }
    }

    private static List<Candle> Stored(TradingDbContext db, DateOnly day, int minutes)
    {
        var from = Ist(day, 0, 0);
        var to = Ist(day, 23, 59);
        return db.Candles.AsNoTracking()
            .Where(c => c.Symbol == VixBackfillPlan.Symbol && c.Resolution == minutes.ToString() && c.TimeStampUtc >= from && c.TimeStampUtc <= to)
            .ToList();
    }

    private sealed class Calendar : IMarketCalendar
    {
        public bool IsLoaded => true;

        public MarketHoliday? HolidayOn(string exchange, DateOnly date)
            => date == GandhiJayanti ? new MarketHoliday { Exchange = exchange, Date = date, Name = "Gandhi Jayanti" } : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Router(IReadOnlyList<IMarketDataProvider> chain) : IProviderRouter
    {
        public Task<IReadOnlyList<IMarketDataProvider>> ResolveDataChainAsync(ProviderCapability capability, string? segment = null, CancellationToken cancellationToken = default)
            => Task.FromResult(chain);

        public Task<IMarketDataProvider> ResolveDataAsync(ProviderCapability capability, string? segment = null, CancellationToken cancellationToken = default)
            => Task.FromResult(chain[0]);

        public Task<IBrokerProvider> ResolveBrokerAsync(long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>A history vendor that answers every request one way, and remembers what it was asked.</summary>
    private sealed class Vendor : IMarketDataProvider
    {
        private readonly Func<string, DateTime, DateTime, IReadOnlyList<ProviderHistoryBar>> _answer;

        private Vendor(string key, bool platformStore, Func<string, DateTime, DateTime, IReadOnlyList<ProviderHistoryBar>> answer)
        {
            _answer = answer;
            Descriptor = new ProviderDescriptor(key, key, ProviderKind.Data, ProviderAuthKind.None,
                new ProviderCapabilities { History = true, ServesFromPlatformStore = platformStore });
        }

        public ProviderDescriptor Descriptor { get; }

        public List<(string Resolution, DateTime From, DateTime To)> Calls { get; } = new();

        /// <summary>Every session bar of every trading day asked for, close 99, plus a pre-open and an after-close bar.</summary>
        public static Vendor Full(string key, bool platformStore = false) => new(key, platformStore, (resolution, from, to) =>
        {
            var bars = new List<ProviderHistoryBar>();
            for (var day = DateOnly.FromDateTime(from.AddMinutes(330)); day <= DateOnly.FromDateTime(to.AddMinutes(330)); day = day.AddDays(1))
            {
                if (!TradingDay(day)) continue;
                var starts = Session(day, int.Parse(resolution)).Concat([Ist(day, 9, 10), Ist(day, 15, 30)]);
                bars.AddRange(starts.Select(t => new ProviderHistoryBar { TimestampUtc = t, Open = 99m, High = 99.5m, Low = 98.5m, Close = 99m }));
            }
            return bars;
        });

        public static Vendor Empty(string key) => new(key, false, (_, _, _) => []);

        public static Vendor Failing(string key, Exception error) => new(key, false, (_, _, _) => throw error);

        public Task<IReadOnlyList<ProviderHistoryBar>> GetHistoryAsync(string canonicalSymbol, string resolution, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
        {
            Assert.Equal(VixBackfillPlan.Symbol, canonicalSymbol);
            Calls.Add((resolution, fromUtc, toUtc));
            return Task.FromResult(_answer(resolution, fromUtc, toUtc));
        }
    }

    private sealed class Notifier : ISystemNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string Message)> Sent { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((severity, title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add((logLevel, formatter(state, exception)));
    }
}
