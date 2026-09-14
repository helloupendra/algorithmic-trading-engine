using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The scanner records each pattern on each closed candle exactly once, only
/// while the exchange is in session, and asks for Telegram only for candles
/// that closed moments ago on a rule that wants it.
/// </summary>
public class CandlePatternScannerTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";

    /// <summary>IST wall time on Tuesday 2026-09-15 → UTC.</summary>
    private static DateTime Ist(int h, int m, int s = 0) => new DateTime(2026, 9, 15, h, m, s, DateTimeKind.Utc).AddMinutes(-330);

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"patterns-{Guid.NewGuid():N}").Options);

    private static CandlePatternScanner Scanner(TradingDbContext db, IMarketCalendar? calendar = null) =>
        new(db, new MarketSessionService(calendar ?? new Calendar()), new PatternWatchPlanner(db), NullLogger<CandlePatternScanner>.Instance);

    private static readonly PatternScanSettings Settings = new();

    private static CandlePatternRule Rule(string symbols = Nifty, string groups = "", string timeframes = "15", string patterns = "doji", bool notify = true) => new()
    {
        Name = "test rule",
        SymbolsCsv = symbols,
        GroupsCsv = groups,
        TimeframesCsv = timeframes,
        PatternsCsv = patterns,
        IsEnabled = true,
        Notify = notify,
    };

    /// <summary>Fifteen 1-minute bars whose 15-minute candle is exactly (o, h, l, c).</summary>
    private static IEnumerable<LiveBar> Candle(string symbol, DateTime startUtc, decimal o, decimal h, decimal l, decimal c)
    {
        for (int i = 0; i < 15; i++)
        {
            var (bo, bh, bl, bc) = i switch
            {
                0 => (o, h, o, o),
                1 => (o, o, l, o),
                14 => (o, Math.Max(o, c), Math.Min(o, c), c),
                _ => (o, o, o, o),
            };
            yield return new LiveBar
            {
                Symbol = symbol, Resolution = "1m", BarStartUtc = startUtc.AddMinutes(i),
                Open = bo, High = bh, Low = bl, Close = bc, TickCount = 1, SourceKey = "test",
            };
        }
    }

    /// <summary>A plain rising candle at 09:15 and a doji at 09:30 on NIFTY.</summary>
    private static void SeedDojiAt0930(TradingDbContext db, string symbol = Nifty)
    {
        db.LiveBars.AddRange(Candle(symbol, Ist(9, 15), 25000m, 25080m, 24990m, 25070m));
        db.LiveBars.AddRange(Candle(symbol, Ist(9, 30), 25070m, 25120m, 25020m, 25072m)); // body 2 of range 100
        db.SaveChanges();
    }

    [Fact]
    public async Task A_closed_candle_pattern_is_recorded_once_with_its_candle_in_the_metadata()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule());
        SeedDojiAt0930(db);

        var first = await Scanner(db).ScanAsync(Ist(9, 45, 20), Settings);

        var recorded = Assert.Single(first.Recorded);
        Assert.True(recorded.Notify);
        var row = db.AlertEvents.Single();
        Assert.Equal("patterns", row.Source);
        Assert.Equal("info", row.Severity);
        Assert.Equal(Nifty, row.Symbol);
        Assert.Equal("NIFTY 15m doji — neutral", row.Title);
        Assert.Equal("NIFTY 15m doji at 09:30 IST — open 25,070, close 25,072, range 100", row.Message);
        Assert.Equal(Ist(9, 45), row.OccurredUtc);
        Assert.Equal("patterns:NSE:NIFTY50-INDEX:15m:20260915T0400Z:doji", row.DedupeKey);

        var meta = PatternEventMetadata.TryRead(row.MetadataJson)!;
        Assert.Equal(15, meta.Timeframe);
        Assert.Equal(Ist(9, 30), meta.BarStartUtc);
        Assert.Equal(15, meta.MinutesInBar);
        Assert.Equal(25120m, meta.High);
        Assert.Equal("neutral", meta.Direction);

        // A second scan, and a scanner built fresh as after an API restart, add nothing.
        Assert.Empty((await Scanner(db).ScanAsync(Ist(9, 45, 40), Settings)).Recorded);
        Assert.Empty((await Scanner(db).ScanAsync(Ist(10, 5), Settings)).Recorded);
        Assert.Single(db.AlertEvents);
    }

    [Fact]
    public async Task A_candle_inside_the_settle_delay_is_still_forming_and_not_read()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule());
        SeedDojiAt0930(db);

        // 09:45:05 minus the 10 s settle is 09:44:55: the 09:30 candle has not closed.
        var outcome = await Scanner(db).ScanAsync(Ist(9, 45, 5), Settings);
        Assert.Empty(outcome.Recorded);
        Assert.Empty(db.AlertEvents);
    }

    [Fact]
    public async Task Candles_found_late_are_recorded_but_not_sent_to_telegram()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule());
        SeedDojiAt0930(db);

        // An API that starts at 11:00 finds the 09:30 doji an hour after it closed.
        var recorded = Assert.Single((await Scanner(db).ScanAsync(Ist(11, 0), Settings)).Recorded);
        Assert.False(recorded.Notify);
        var meta = PatternEventMetadata.TryRead(db.AlertEvents.Single().MetadataJson)!;
        Assert.False(meta.Notify);
        Assert.Contains("before the scanner saw it", meta.NotifySkippedReason);
    }

    [Fact]
    public async Task A_rule_without_telegram_records_without_notifying()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule(notify: false));
        SeedDojiAt0930(db);

        var recorded = Assert.Single((await Scanner(db).ScanAsync(Ist(9, 45, 20), Settings)).Recorded);
        Assert.False(recorded.Notify);
    }

    [Fact]
    public async Task Patterns_the_rule_does_not_watch_are_ignored()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule(patterns: "hammer"));
        SeedDojiAt0930(db);

        Assert.Empty((await Scanner(db).ScanAsync(Ist(9, 45, 20), Settings)).Recorded);
    }

    [Fact]
    public async Task Nothing_is_scanned_on_an_exchange_holiday()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule());
        SeedDojiAt0930(db);

        var holiday = new Calendar(new MarketHoliday { Exchange = "NSE", Date = new DateOnly(2026, 9, 15), Name = "test holiday" });
        var outcome = await Scanner(db, holiday).ScanAsync(Ist(9, 45, 20), Settings);

        Assert.Empty(outcome.Recorded);
        var state = Assert.Single(outcome.Symbols);
        Assert.False(state.ExchangeInSession);
        Assert.Null(state.Problem);
    }

    [Fact]
    public async Task The_candle_that_closes_with_the_session_is_still_read_just_after_the_close()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule());
        db.LiveBars.AddRange(Candle(Nifty, Ist(15, 15), 25070m, 25120m, 25020m, 25072m));
        db.SaveChanges();

        var recorded = Assert.Single((await Scanner(db).ScanAsync(Ist(15, 30, 20), Settings)).Recorded);
        Assert.True(recorded.Notify);
        Assert.Empty((await Scanner(db).ScanAsync(Ist(15, 40), Settings)).Recorded);
    }

    [Fact]
    public async Task A_watched_symbol_with_no_bars_is_reported_as_not_streamed()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule(symbols: $"{Nifty},NSE:SBIN-EQ"));
        SeedDojiAt0930(db);

        var outcome = await Scanner(db).ScanAsync(Ist(9, 45, 20), Settings);
        var sbin = outcome.Symbols.Single(s => s.Symbol == "NSE:SBIN-EQ");
        Assert.Equal("no live bars — not streamed by any feed", sbin.Problem);
        // NIFTY's last minute (09:44) ended twenty seconds ago: receiving data.
        Assert.Null(outcome.Symbols.Single(s => s.Symbol == Nifty).Problem);
    }

    [Fact]
    public async Task A_symbol_whose_bars_stopped_is_reported_with_the_last_minute_seen()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule());
        SeedDojiAt0930(db);

        var outcome = await Scanner(db).ScanAsync(Ist(10, 0), Settings);
        Assert.Equal("no live bars since 09:44 IST", outcome.Symbols.Single().Problem);
    }

    [Fact]
    public async Task Forming_shows_the_current_candle_and_what_it_would_be_if_it_closed_now()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule(patterns: "doji,marubozu"));
        // 09:30 candle so far: eight minutes, open 25070, close 25071, range 100 → a doji if it closed now.
        db.LiveBars.AddRange(Candle(Nifty, Ist(9, 30), 25070m, 25120m, 25020m, 25071m).Take(8)
            .Select((b, i) => i == 7 ? new LiveBar { Symbol = Nifty, Resolution = "1m", BarStartUtc = b.BarStartUtc, Open = 25070m, High = 25071m, Low = 25070m, Close = 25071m } : b));
        db.SaveChanges();

        var forming = Assert.Single(await Scanner(db).GetFormingAsync(Ist(9, 37, 30)));
        Assert.True(forming.ExchangeInSession);
        Assert.Equal(Ist(9, 30), forming.BarStartUtc);
        Assert.Equal(7, forming.MinutesElapsed);
        Assert.Equal(15, forming.MinutesExpected);
        Assert.Equal(8, forming.Bar!.MinutesWithData);
        Assert.Equal(25071m, forming.Bar.Close);
        Assert.Equal([CandlePattern.Doji], forming.WouldBe.Select(h => h.Pattern).ToArray());

        // Forming is for the page only.
        Assert.Empty(db.AlertEvents);
    }

    [Fact]
    public async Task Groups_resolve_to_nearest_futures_the_recording_list_and_the_indices()
    {
        using var db = Db();
        db.Instruments.AddRange(
            Future("MCX:CRUDEOIL26SEPFUT", "CRUDEOIL", "MCX", new DateOnly(2026, 9, 21)),
            Future("MCX:CRUDEOIL26OCTFUT", "CRUDEOIL", "MCX", new DateOnly(2026, 10, 19)),
            Future("MCX:CRUDEOILM26SEPFUT", "CRUDEOILM", "MCX", new DateOnly(2026, 9, 21)),
            Future("NSE:NIFTY26AUGFUT", "NIFTY", "NSE", new DateOnly(2026, 8, 27)), // expired
            Future("NSE:NIFTY26SEPFUT", "NIFTY", "NSE", new DateOnly(2026, 9, 29)));
        db.LiveWatchlistItems.AddRange(
            new LiveWatchlistItem { Symbol = "NSE:SBIN-EQ", IsActive = true },
            new LiveWatchlistItem { Symbol = "NSE:INFY-EQ", IsActive = false },
            new LiveWatchlistItem { Symbol = "NSE:NIFTY2691523450CE", IsActive = true });
        db.SaveChanges();

        var rules = new[]
        {
            CandlePatternRules.Parse(Rule(symbols: "", groups: "future:crudeoil,index-futures,recording-stocks,indices,future:NOSUCH")),
        };
        var plan = await new PatternWatchPlanner(db).PlanAsync(rules, new DateOnly(2026, 9, 15));

        Assert.Equal(["MCX:CRUDEOIL26SEPFUT"], plan.SymbolsByGroup["future:CRUDEOIL"]);
        Assert.Equal(["NSE:NIFTY26SEPFUT"], plan.SymbolsByGroup["index-futures"]);
        Assert.Equal(["NSE:SBIN-EQ"], plan.SymbolsByGroup["recording-stocks"]);
        Assert.Contains("NSE:NIFTYBANK-INDEX", plan.SymbolsByGroup["indices"]);
        Assert.Contains("BSE:SENSEX-INDEX", plan.SymbolsByGroup["indices"]);
        Assert.Contains(plan.Unresolved, u => u.Contains("future:NOSUCH"));
    }

    [Fact]
    public async Task Rules_covering_the_same_candle_merge_and_notify_if_any_of_them_does()
    {
        using var db = Db();
        db.CandlePatternRules.Add(Rule(notify: false));
        db.CandlePatternRules.Add(Rule(patterns: "doji,hammer", notify: true));
        SeedDojiAt0930(db);

        var recorded = Assert.Single((await Scanner(db).ScanAsync(Ist(9, 45, 20), Settings)).Recorded);
        Assert.True(recorded.Notify);
    }

    [Fact]
    public async Task Default_rules_are_seeded_once_and_a_deletion_survives_a_restart()
    {
        using var db = Db();
        var seeder = new ReferenceDataSeeder(db, new Env(), NullLogger<ReferenceDataSeeder>.Instance);

        await seeder.SeedCandlePatternRulesAsync(CancellationToken.None);
        Assert.Equal(4, db.CandlePatternRules.Count());
        Assert.Contains(db.CandlePatternRules, r => r.GroupsCsv == "future:CRUDEOIL" && r.TimeframesCsv == "15");
        // Five-minute index candles alert on the page only; Telegram gets the 15-minute ones.
        Assert.Contains(db.CandlePatternRules, r => r.GroupsCsv == "indices" && r.TimeframesCsv == "5" && !r.Notify);
        Assert.Contains(db.CandlePatternRules, r => r.GroupsCsv == "indices" && r.TimeframesCsv == "15" && r.Notify);

        db.CandlePatternRules.RemoveRange(db.CandlePatternRules);
        db.SaveChanges();
        await seeder.SeedCandlePatternRulesAsync(CancellationToken.None);
        Assert.Empty(db.CandlePatternRules);
    }

    [Fact]
    public void Validation_explains_every_problem_in_words()
    {
        var errors = CandlePatternRules.Validate("", ["SBIN", "NSE:SBIN-EQ"], ["stocks"], [7], ["tweezer"]);
        Assert.Contains(errors, e => e.StartsWith("Name is required"));
        Assert.Contains(errors, e => e.Contains("EXCHANGE:NAME") && e.Contains("SBIN"));
        Assert.Contains(errors, e => e.StartsWith("Unknown symbol group: stocks"));
        Assert.Contains(errors, e => e.Contains("not 7"));
        Assert.Contains(errors, e => e.StartsWith("Unknown pattern: tweezer"));

        Assert.Empty(CandlePatternRules.Validate("Crude", [], ["future:CRUDEOIL"], [15], ["doji", "morning_star"]));
    }

    [Fact]
    public void Telegram_batches_group_by_closing_minute_and_skip_what_no_rule_sends()
    {
        PatternOccurrence O(string symbol, int tf, DateTime end, CandlePattern p) =>
            new(symbol, tf, end.AddMinutes(-tf), end, 1, 2, 0.5m, 1.01m, tf, tf, p, PatternDirection.Neutral);

        var recorded = new[]
        {
            new RecordedPattern(1, O("NSE:SBIN-EQ", 15, Ist(10, 45), CandlePattern.Doji), true),
            new RecordedPattern(2, O(Nifty, 5, Ist(10, 45), CandlePattern.Doji), true),
            new RecordedPattern(3, O(Nifty, 5, Ist(10, 50), CandlePattern.Doji), true),
            new RecordedPattern(4, O(Nifty, 15, Ist(10, 45), CandlePattern.Hammer), false),
        };

        var batches = PatternTelegramBatches.From(recorded);
        Assert.Equal(2, batches.Count);
        Assert.Equal(Ist(10, 45), batches[0].ClosedAtUtc);
        Assert.Equal([2L, 1L], batches[0].Patterns.Select(p => p.EventId).ToArray()); // index before stock
        Assert.Equal([3L], batches[1].Patterns.Select(p => p.EventId).ToArray());
    }

    [Fact]
    public void Telegram_text_is_one_escaped_message_capped_at_twenty_five_lines()
    {
        var one = new PatternOccurrence("NSE:NIFTYBANK-INDEX", 15, Ist(10, 30), Ist(10, 45), 57210m, 57260m, 57164m, 57214m, 15, 15, CandlePattern.Doji, PatternDirection.Neutral);
        Assert.Equal("BANKNIFTY 15m doji at 10:30 IST — open 57,210, close 57,214, range 96", PatternAlertText.Message(one));

        var many = Enumerable.Repeat(one with { Symbol = "NSE:M&M-EQ", Open = 3210.5m }, 30).ToList();
        var text = PatternAlertText.TelegramMessage(Ist(10, 45), many);
        var lines = text.Split('\n');
        Assert.Equal("<b>Candle patterns · candles closed 10:45 IST</b>", lines[0]);
        Assert.Equal(1 + 25 + 1, lines.Length);
        Assert.Contains("M&amp;M 15m doji", lines[1]);
        Assert.Contains("open 3,210.50", lines[1]);
        Assert.Equal("+5 more on the Pattern alerts page.", lines[^1]);
    }

    [Fact]
    public void Incomplete_candles_say_how_many_minutes_they_had()
    {
        var o = new PatternOccurrence("NSE:SBIN-EQ", 15, Ist(10, 30), Ist(10, 45), 812.3m, 815m, 810m, 812.35m, 13, 15, CandlePattern.Doji, PatternDirection.Neutral);
        Assert.EndsWith("(13 of 15 minutes had data)", PatternAlertText.Message(o));
        Assert.Equal("12,34,567", PatternAlertText.Price(1234567m));
        Assert.Equal("812.30", PatternAlertText.Price(812.3m));
    }

    [Fact]
    public void The_rate_limit_allows_a_burst_then_refuses_until_the_window_moves_on()
    {
        var limiter = new SlidingWindowLimiter(4, TimeSpan.FromMinutes(5));
        var t = Ist(10, 0);
        Assert.True(limiter.TryAcquire(t));
        Assert.True(limiter.TryAcquire(t.AddSeconds(1)));
        Assert.True(limiter.TryAcquire(t.AddSeconds(2)));
        Assert.True(limiter.TryAcquire(t.AddSeconds(3)));
        Assert.False(limiter.TryAcquire(t.AddMinutes(4)));
        Assert.True(limiter.TryAcquire(t.AddMinutes(5)));
    }

    private static Instrument Future(string symbol, string underlying, string exchange, DateOnly expiry) => new()
    {
        Symbol = symbol, Underlying = underlying, Exchange = exchange, Segment = exchange == "MCX" ? "COM" : "FO",
        InstrumentType = "FUT", ExpiryDate = expiry, IsEnabled = true,
    };

    private sealed class Calendar(params MarketHoliday[] holidays) : IMarketCalendar
    {
        public bool IsLoaded => true;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => holidays.FirstOrDefault(h => h.Exchange == exchange && h.Date == date);
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "AlgoTrading.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
