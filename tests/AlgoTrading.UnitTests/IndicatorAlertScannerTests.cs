using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The indicator scanner over live 1-minute bars: it warms up on earlier
/// sessions, reads closed candles only, records each rule on each candle once,
/// only in session, and asks for Telegram only for a fresh alert outside its
/// cooldown on a line that sends.
/// </summary>
/// <remarks>
/// Small periods (EMA 2/3 settles after 12 candles) keep the bars few; the
/// default periods' arithmetic is pinned in <see cref="IndicatorAlertRulesTests"/>.
/// </remarks>
public class IndicatorAlertScannerTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string NiftyFuture = "NSE:NIFTY26SEPFUT";

    /// <summary>IST wall time on a September 2026 day → UTC. Tuesday the 15th unless said.</summary>
    private static DateTime Ist(int h, int m, int s = 0, int day = 15) => new DateTime(2026, 9, day, h, m, s, DateTimeKind.Utc).AddMinutes(-330);

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"indicators-{Guid.NewGuid():N}").Options);

    private static IndicatorAlertScanner Scanner(TradingDbContext db, IMarketCalendar? calendar = null, IndicatorHistoryCache? cache = null) =>
        new(db, new MarketSessionService(calendar ?? new Calendar()), new PatternWatchPlanner(db), cache ?? new IndicatorHistoryCache(),
            NullLogger<IndicatorAlertScanner>.Instance);

    private static readonly PatternScanSettings Settings = new();

    private static IndicatorAlertConfig Config(string text) => IndicatorAlertConfig.Parse(text);

    private static readonly IndicatorAlertConfig EmaOnNifty15 = Config($"{Nifty} 15 ema-cross(2,3)");

    /// <summary>One flat 1-minute bar per minute of each candle, at that candle's close.</summary>
    private static IEnumerable<LiveBar> Candles(string symbol, DateTime firstStartUtc, int timeframe, IEnumerable<decimal> closes, long volume = 0)
    {
        int k = 0;
        foreach (var close in closes)
        {
            for (int m = 0; m < timeframe; m++)
            {
                yield return Bar(symbol, firstStartUtc.AddMinutes(k * timeframe + m), close, volume);
            }

            k++;
        }
    }

    private static LiveBar Bar(string symbol, DateTime startUtc, decimal price, long volume = 0) => new()
    {
        Symbol = symbol, Resolution = "1m", BarStartUtc = startUtc,
        Open = price, High = price, Low = price, Close = price, VolumeDelta = volume, TickCount = 1, SourceKey = "test",
    };

    /// <summary>Monday's whole session falling ten points a candle: EMA(2) below EMA(3) at the close.</summary>
    private static void SeedFallingMonday(TradingDbContext db, string symbol = Nifty, int day = 14, int timeframe = 15)
    {
        int candles = 375 / timeframe;
        db.LiveBars.AddRange(Candles(symbol, Ist(9, 15, day: day), timeframe, Enumerable.Range(0, candles).Select(k => 25_000m - 10 * k)));
        db.SaveChanges();
    }

    [Fact]
    public async Task The_first_candle_of_the_day_alerts_because_earlier_sessions_warmed_the_indicator()
    {
        using var db = Db();
        SeedFallingMonday(db);
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_500m]));
        db.SaveChanges();

        var outcome = await Scanner(db).ScanAsync(Ist(9, 30, 20), EmaOnNifty15, Settings);

        var recorded = Assert.Single(outcome.Recorded);
        Assert.True(recorded.Notify);
        Assert.True(recorded.Occurrence.Up);
        var row = db.AlertEvents.Single();
        Assert.Equal("indicators", row.Source);
        Assert.Equal("info", row.Severity);
        Assert.Equal(Nifty, row.Symbol);
        Assert.Equal(Ist(9, 30), row.OccurredUtc);
        Assert.Equal("NIFTY 15m EMA(2) crossed above EMA(3)", row.Title);
        Assert.StartsWith("NIFTY 15m EMA(2) crossed above EMA(3) at 09:15 IST — close 25,500; EMA(2) ", row.Message);
        Assert.Equal("indicators:NSE:NIFTY50-INDEX:15m:20260915T0345Z:ema-cross(2,3)", row.DedupeKey);

        var meta = IndicatorEventMetadata.TryRead(row.MetadataJson)!;
        Assert.Equal("ema-cross(2,3)", meta.Rule);
        Assert.Equal("up", meta.Direction);
        Assert.Equal(15, meta.Timeframe);
        Assert.Equal(Ist(9, 15), meta.BarStartUtc);
        Assert.Equal(25_500m, meta.Close);
        Assert.True(meta.Values["emaFast"] > meta.Values["emaSlow"]);
        Assert.Equal([1], meta.ConfigLines);
        Assert.Null(PatternEventMetadata.TryRead(row.MetadataJson)); // not mistaken for a pattern

        var watch = Assert.Single(outcome.Watches);
        Assert.Equal(25, watch.HistoryCandles);
        Assert.Equal(1, watch.TodayCandles);
        Assert.Equal("ready", Assert.Single(watch.Rules).State);
    }

    [Fact]
    public async Task Without_stored_history_a_rule_waits_until_todays_candles_have_settled_it()
    {
        using var db = Db();
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_000m, 25_500m]));
        db.SaveChanges();

        var outcome = await Scanner(db).ScanAsync(Ist(9, 45, 20), EmaOnNifty15, Settings);

        Assert.Empty(outcome.Recorded);
        var rule = Assert.Single(Assert.Single(outcome.Watches).Rules);
        Assert.Equal("warming up", rule.State);
        Assert.Equal("2 of 12 candles", rule.Detail);
    }

    [Fact]
    public async Task A_holiday_is_left_out_of_the_warm_up_and_the_session_before_it_is_used()
    {
        using var db = Db();
        // Friday the 11th falls, as in the other tests. Monday the 14th was a
        // holiday (Ganesh Chaturthi) whose bars are a vendor's replayed close:
        // were they read, the warm-up would end at 30,000 and there would be no cross.
        SeedFallingMonday(db, day: 11);
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15, day: 14), 15, Enumerable.Repeat(30_000m, 25)));
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_500m]));
        db.SaveChanges();

        var holiday = new Calendar(new MarketHoliday { Exchange = "NSE", Date = new DateOnly(2026, 9, 14), Name = "Ganesh Chaturthi" });
        var outcome = await Scanner(db, holiday).ScanAsync(Ist(9, 30, 20), EmaOnNifty15, Settings);

        Assert.Equal(25, Assert.Single(outcome.Watches).HistoryCandles);
        Assert.True(Assert.Single(outcome.Recorded).Occurrence.Up);
    }

    [Fact]
    public async Task A_forming_candle_is_never_read_even_when_it_would_cross_if_it_closed_now()
    {
        using var db = Db();
        SeedFallingMonday(db);
        // Today's first candle jumps to 25,500 for eight minutes, then falls back
        // and closes at 24,700: a cross at 09:23, none at the close.
        db.LiveBars.AddRange(Enumerable.Range(0, 8).Select(m => Bar(Nifty, Ist(9, 15 + m), 25_500m)));
        db.SaveChanges();

        Assert.Empty((await Scanner(db).ScanAsync(Ist(9, 23, 30), EmaOnNifty15, Settings)).Recorded);

        db.LiveBars.AddRange(Enumerable.Range(8, 7).Select(m => Bar(Nifty, Ist(9, 15 + m), 24_700m)));
        db.SaveChanges();

        // Five seconds after the close is inside the settle delay: still forming.
        Assert.Empty((await Scanner(db).ScanAsync(Ist(9, 30, 5), EmaOnNifty15, Settings)).Recorded);
        Assert.Empty((await Scanner(db).ScanAsync(Ist(9, 30, 20), EmaOnNifty15, Settings)).Recorded);
        Assert.Empty(db.AlertEvents);
    }

    [Fact]
    public async Task An_alert_is_recorded_once_whatever_the_number_of_scans_or_restarts()
    {
        using var db = Db();
        SeedFallingMonday(db);
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_500m]));
        db.SaveChanges();

        var cache = new IndicatorHistoryCache();
        Assert.Single((await Scanner(db, cache: cache).ScanAsync(Ist(9, 30, 20), EmaOnNifty15, Settings)).Recorded);
        Assert.Empty((await Scanner(db, cache: cache).ScanAsync(Ist(9, 30, 40), EmaOnNifty15, Settings)).Recorded);
        // A restart: new scanner, empty cache.
        Assert.Empty((await Scanner(db).ScanAsync(Ist(9, 44), EmaOnNifty15, Settings)).Recorded);
        Assert.Single(db.AlertEvents);
    }

    [Fact]
    public async Task Candles_found_late_are_recorded_but_not_sent()
    {
        using var db = Db();
        SeedFallingMonday(db);
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_500m]));
        db.SaveChanges();

        // An API that starts at 11:00 finds the 09:15 cross ninety minutes on.
        var recorded = Assert.Single((await Scanner(db).ScanAsync(Ist(11, 0), EmaOnNifty15, Settings)).Recorded);
        Assert.False(recorded.Notify);
        Assert.Contains("before the scanner saw it", IndicatorEventMetadata.TryRead(db.AlertEvents.Single().MetadataJson)!.NotifySkippedReason);
    }

    [Fact]
    public async Task Nothing_is_scanned_on_a_holiday_or_before_the_open()
    {
        using var db = Db();
        SeedFallingMonday(db);
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_500m]));
        db.SaveChanges();

        var holiday = new Calendar(new MarketHoliday { Exchange = "NSE", Date = new DateOnly(2026, 9, 15), Name = "test holiday" });
        var closed = await Scanner(db, holiday).ScanAsync(Ist(9, 30, 20), EmaOnNifty15, Settings);
        Assert.Empty(closed.Recorded);
        Assert.False(Assert.Single(closed.Watches).ExchangeInSession);

        var early = await Scanner(db).ScanAsync(Ist(9, 10), EmaOnNifty15, Settings);
        Assert.Empty(early.Recorded);
        Assert.False(Assert.Single(early.Watches).ExchangeInSession);

        // And long after the close (past the three-minute grace).
        Assert.Empty((await Scanner(db).ScanAsync(Ist(15, 40), EmaOnNifty15, Settings)).Recorded);
        Assert.Empty(db.AlertEvents);
    }

    [Fact]
    public async Task A_repeat_inside_the_cooldown_is_recorded_for_the_page_and_not_sent()
    {
        using var db = Db();
        SeedFallingMonday(db, timeframe: 5);
        // Five-minute candles: up through the EMAs, down through them, up again.
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 5, [25_500m, 23_000m, 26_000m]));
        db.SaveChanges();

        var config = Config($"cooldown: 30\n{Nifty} 5 ema-cross(2,3)");
        var first = Assert.Single((await Scanner(db).ScanAsync(Ist(9, 20, 20), config, Settings)).Recorded);
        Assert.True(first.Notify);
        var second = Assert.Single((await Scanner(db).ScanAsync(Ist(9, 25, 20), config, Settings)).Recorded);
        Assert.False(second.Notify);
        Assert.False(second.Occurrence.Up);
        var third = Assert.Single((await Scanner(db).ScanAsync(Ist(9, 30, 20), config, Settings)).Recorded);
        Assert.False(third.Notify);

        var meta = IndicatorEventMetadata.TryRead(db.AlertEvents.Single(e => e.Id == third.EventId).MetadataJson)!;
        Assert.Equal("within the 30-minute cooldown after the alert on the candle that closed 09:20 IST", meta.NotifySkippedReason);
        Assert.Equal(Ist(9, 20), meta.CoolingSinceUtc);

        // With the cooldown off, the same day sends all three.
        using var db2 = Db();
        SeedFallingMonday(db2, timeframe: 5);
        db2.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 5, [25_500m, 23_000m, 26_000m]));
        db2.SaveChanges();
        var off = Config($"cooldown: 0\n{Nifty} 5 ema-cross(2,3)");
        await Scanner(db2).ScanAsync(Ist(9, 20, 20), off, Settings);
        Assert.True(Assert.Single((await Scanner(db2).ScanAsync(Ist(9, 25, 20), off, Settings)).Recorded).Notify);
    }

    [Fact]
    public async Task Vwap_is_computed_for_a_future_with_volume_and_refused_for_an_index()
    {
        using var db = Db();
        // 09:15 candle: 100 on heavy volume, last minute 99 on light: VWAP just under 100, close 99 below it.
        db.LiveBars.AddRange(Enumerable.Range(0, 4).Select(m => Bar(NiftyFuture, Ist(9, 15 + m), 100m, 1_000)));
        db.LiveBars.Add(Bar(NiftyFuture, Ist(9, 19), 99m, 10));
        // 09:20 candle at 101: VWAP just over 100, close above it.
        db.LiveBars.AddRange(Enumerable.Range(0, 5).Select(m => Bar(NiftyFuture, Ist(9, 20 + m), 101m, 10)));
        db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 5, [25_000m, 25_100m]));
        db.SaveChanges();

        var config = Config($"{NiftyFuture},{Nifty} 5 vwap-cross");
        Assert.Contains(config.Warnings, w => w.Contains($"vwap-cross on {Nifty}: an index has no traded volume"));

        var outcome = await Scanner(db).ScanAsync(Ist(9, 25, 20), config, Settings);

        var recorded = Assert.Single(outcome.Recorded);
        Assert.Equal(NiftyFuture, recorded.Occurrence.Symbol);
        Assert.True(recorded.Occurrence.Up);
        double vwap = (100.0 * 4_000 + 99 * 10 + 101 * 50) / 4_060;
        Assert.Equal(vwap, recorded.Occurrence.Value("vwap")!.Value, 9);
        Assert.Equal("NIFTY26SEPFUT 5m Close crossed above VWAP at 09:20 IST — close 101; VWAP 100.01", IndicatorAlertText.Message(recorded.Occurrence));

        var index = outcome.Watches.Single(w => w.Symbol == Nifty);
        var skipped = Assert.Single(index.Rules);
        Assert.Equal("skipped", skipped.State);
        Assert.Equal("an index has no traded volume, so it has no VWAP", skipped.Detail);
        Assert.Equal("ready", Assert.Single(outcome.Watches.Single(w => w.Symbol == NiftyFuture).Rules).State);
    }

    [Fact]
    public async Task A_page_only_line_or_telegram_off_records_without_sending()
    {
        foreach (var (text, reason) in new[]
                 {
                     ($"{Nifty} 15 ema-cross(2,3) page-only", "page-only in the indicator config"),
                     ($"telegram: off\n{Nifty} 15 ema-cross(2,3)", "telegram: off in the indicator config"),
                 })
        {
            using var db = Db();
            SeedFallingMonday(db);
            db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_500m]));
            db.SaveChanges();

            var recorded = Assert.Single((await Scanner(db).ScanAsync(Ist(9, 30, 20), Config(text), Settings)).Recorded);
            Assert.False(recorded.Notify);
            Assert.Equal(reason, IndicatorEventMetadata.TryRead(db.AlertEvents.Single().MetadataJson)!.NotifySkippedReason);
        }
    }

    [Fact]
    public async Task Lines_covering_the_same_candle_merge_and_a_future_group_resolves_to_the_nearest_contract()
    {
        using var db = Db();
        db.Instruments.AddRange(
            Future("NSE:NIFTY26SEPFUT", "NIFTY", new DateOnly(2026, 9, 29)),
            Future("NSE:NIFTY26OCTFUT", "NIFTY", new DateOnly(2026, 10, 27)));
        db.SaveChanges();

        var config = Config($"""
            {Nifty},future:NIFTY  5,15  rsi-above ema-cross  page-only
            {Nifty}  15  ema-cross supertrend-flip
            future:NOSUCH  15  rsi-above
            """);
        var (watches, unresolved) = await Scanner(db).PlanAsync(config, new DateOnly(2026, 9, 15), CancellationToken.None);

        Assert.Equal(
            [(NiftyFuture, 5), (NiftyFuture, 15), (Nifty, 5), (Nifty, 15)],
            watches.Select(w => (w.Symbol, w.TimeframeMinutes)).ToArray());
        var nifty15 = watches.Single(w => w.Symbol == Nifty && w.TimeframeMinutes == 15);
        Assert.Equal(["rsi-above(14,70)", "ema-cross(9,21)", "supertrend-flip(10,3)"], nifty15.Rules.Select(r => r.Key));
        // ema-cross is page-only on line 1 but sent by line 2: sent.
        Assert.Equal(["ema-cross(9,21)", "supertrend-flip(10,3)"], nifty15.NotifyRules.Order(StringComparer.Ordinal));
        Assert.Equal([1, 2], nifty15.Lines);
        Assert.Contains(unresolved, u => u.StartsWith("Line 3: \"future:NOSUCH\""));
    }

    // -------------------------------------------------------------- telegram --

    private static IndicatorOccurrence Occurrence(string symbol, int tf, DateTime start, IndicatorRule rule, bool up, decimal close, params IndicatorValue[] values) =>
        new(symbol, tf, start, start.AddMinutes(tf), close, close, close, close, tf, tf, rule, up, values);

    [Fact]
    public void One_message_per_closing_minute_with_a_symbols_rules_on_one_line()
    {
        var rsi = Occurrence("NSE:NIFTYBANK-INDEX", 15, Ist(10, 30), IndicatorRule.RsiAboveDefault, true, 57_214m,
            new("rsiBefore", 68.44), new("rsi", 71.23));
        var ema = Occurrence("NSE:NIFTYBANK-INDEX", 15, Ist(10, 30), IndicatorRule.EmaCrossDefault, true, 57_214m,
            new("emaFast", 57_190.456), new("emaSlow", 57_188.1));
        var st = Occurrence(Nifty, 5, Ist(10, 40), IndicatorRule.SupertrendFlipDefault, false, 25_072.5m,
            new("through", 25_080), new("line", 25_161.3349));
        var later = Occurrence(Nifty, 5, Ist(10, 45), IndicatorRule.RsiBelowDefault, false, 25_010m, new("rsiBefore", 31.2), new("rsi", 28.7));

        var batches = IndicatorTelegramBatches.From(
        [
            new RecordedIndicator(1, st, true),
            new RecordedIndicator(2, ema, true),
            new RecordedIndicator(3, rsi, true),
            new RecordedIndicator(4, later, true),
            new RecordedIndicator(5, later with { Rule = IndicatorRule.EmaCrossDefault }, false),
        ]);

        Assert.Equal(2, batches.Count);
        Assert.Equal(Ist(10, 45), batches[0].ClosedAtUtc);
        // NIFTY before BANKNIFTY (the desk's order), a candle's rules in the catalog's order.
        Assert.Equal([1L, 3L, 2L], batches[0].Alerts.Select(a => a.EventId).ToArray());
        Assert.Equal([4L], batches[1].Alerts.Select(a => a.EventId).ToArray());

        var text = IndicatorAlertText.TelegramMessage(batches[0].ClosedAtUtc, batches[0].Alerts.Select(a => a.Occurrence).ToList());
        Assert.Equal(
            "<b>Indicator alerts · candles closed 10:45 IST</b>\n" +
            "NIFTY 5m at 10:40 IST — close 25,072.50 · Supertrend(10, 3) flipped down (closed below its band at 25,080.00, line now 25,161.33)\n" +
            "BANKNIFTY 15m at 10:30 IST — close 57,214 · RSI(14) crossed above 70 (RSI 68.4 → 71.2) · EMA(9) crossed above EMA(21) (EMA(9) 57,190.46, EMA(21) 57,188.10)",
            text);
    }

    [Fact]
    public void The_words_describe_what_happened_and_say_when_a_candle_was_incomplete()
    {
        var o = Occurrence(Nifty, 15, Ist(10, 30), IndicatorRule.RsiAboveDefault, true, 25_072m, new("rsiBefore", 68.4), new("rsi", 71.2));
        Assert.Equal("NIFTY 15m RSI(14) crossed above 70", IndicatorAlertText.Title(o));
        Assert.Equal("NIFTY 15m RSI(14) crossed above 70 at 10:30 IST — close 25,072; RSI 68.4 → 71.2", IndicatorAlertText.Message(o));
        Assert.EndsWith("(13 of 15 minutes had data)", IndicatorAlertText.Message(o with { MinutesWithData = 13 }));

        // Facts about a candle, never advice about a trade.
        var all = new[] { IndicatorRule.RsiAboveDefault, IndicatorRule.RsiBelowDefault, IndicatorRule.EmaCrossDefault, IndicatorRule.SupertrendFlipDefault, IndicatorRule.VwapCrossDefault }
            .SelectMany(r => new[] { true, false }.Select(up => IndicatorAlertText.Message(o with { Rule = r, Up = up }) + " " + r.Definition + " " + r.Label));
        foreach (var words in all)
        {
            foreach (var claim in new[] { "buy", "sell", "signal", "overbought", "oversold", "bullish", "bearish", "entry", "exit" })
                Assert.DoesNotContain(claim, words, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- config --

    [Fact]
    public void The_config_is_found_from_the_content_root_upwards_and_reread_only_when_it_changes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"indicators-{Guid.NewGuid():N}");
        var api = Path.Combine(root, "src", "AlgoTrading.Api");
        Directory.CreateDirectory(api);
        try
        {
            var source = new IndicatorAlertConfigSource(api, null);
            var missing = source.Read();
            Assert.Null(missing.File);
            Assert.Null(missing.Config);
            Assert.Contains(Path.Combine(root, "config", "indicator-alerts.txt"), missing.Searched);

            var file = Path.Combine(root, "config", "indicator-alerts.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"cooldown: 10\n{Nifty} 15 rsi-above\n");
            var first = source.Read();
            Assert.Equal(file, first.File);
            Assert.Equal(TimeSpan.FromMinutes(10), first.Config!.Cooldown);
            Assert.Same(first, source.Read());

            File.WriteAllText(file, $"cooldown: 45\n{Nifty} 5,15 rsi-above ema-cross\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            var edited = source.Read();
            Assert.Equal(TimeSpan.FromMinutes(45), edited.Config!.Cooldown);
            Assert.Equal([5, 15], Assert.Single(edited.Config.Lines).Timeframes);

            // IndicatorAlerts:ConfigFile names a file outright (relative to the content root).
            var named = new IndicatorAlertConfigSource(api, "../../elsewhere.txt").Read();
            Assert.Null(named.File);
            Assert.Equal([Path.Combine(root, "elsewhere.txt")], named.Searched);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_scanner_follows_the_pattern_alerts_switch_unless_told_otherwise()
    {
        IConfiguration Settings(params (string Key, string Value)[] pairs) =>
            new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value))).Build();

        Assert.True(IndicatorAlertService.IsEnabled(Settings()));
        Assert.False(IndicatorAlertService.IsEnabled(Settings(("PatternAlerts:Enabled", "false"))));
        Assert.True(IndicatorAlertService.IsEnabled(Settings(("PatternAlerts:Enabled", "false"), ("IndicatorAlerts:Enabled", "true"))));
        Assert.False(IndicatorAlertService.IsEnabled(Settings(("IndicatorAlerts:Enabled", "false"))));
    }

    [Fact]
    public async Task The_api_reads_back_the_config_its_warnings_the_warm_up_and_todays_alerts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"indicators-api-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "indicator-alerts.txt"),
            $"cooldown: 20\n{Nifty},future:NIFTY  15  ema-cross(2,3) vwap-cross\n{Nifty} 15 macd\n");
        try
        {
            using var db = Db();
            db.Instruments.Add(Future(NiftyFuture, "NIFTY", IstTime.DateOf(DateTime.UtcNow).AddDays(10)));
            SeedFallingMonday(db);
            db.LiveBars.AddRange(Candles(Nifty, Ist(9, 15), 15, [25_500m]));
            db.SaveChanges();

            var source = new IndicatorAlertConfigSource(root, null);
            var state = new IndicatorScannerState();
            var outcome = await Scanner(db).ScanAsync(Ist(9, 30, 20), source.Read().Config!, Settings);
            state.ScanCompleted(outcome, 12.3);

            // The scan's rows are stamped 15 Sep; one stamped now is "today" for the page.
            var copy = db.AlertEvents.AsNoTracking().Single();
            copy.Id = 0;
            copy.OccurredUtc = DateTime.UtcNow;
            copy.DedupeKey += ":now";
            copy.DeliveredToTelegram = true;
            db.AlertEvents.Add(copy);
            db.SaveChanges();

            var controller = new AlgoTrading.Api.Controllers.PatternAlertsController(db);
            var telegram = new TelegramSender(new NoHttp(), new ConfigurationBuilder().Build(), NullLogger<TelegramSender>.Instance);
            var response = Assert.IsType<AlgoTrading.Contracts.Patterns.IndicatorAlertsResponse>(
                Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(
                    (await controller.GetIndicators(source, state, new PatternWatchPlanner(db), telegram, CancellationToken.None)).Result).Value);

            Assert.Equal(Path.Combine(root, "config", "indicator-alerts.txt"), response.File);
            Assert.Equal(20, response.CooldownMinutes);
            Assert.Equal(
            [
                $"Line 2: vwap-cross on {Nifty}: an index has no traded volume, so it has no VWAP; not watched there.",
                "Line 3: unknown rule 'macd'; the rules are rsi-above, rsi-below, ema-cross, supertrend-flip, vwap-cross; left out.",
                "Line 3: no rule left that could be read; the line is skipped.",
            ], response.Warnings);
            var line = Assert.Single(response.Lines);
            Assert.Equal([Nifty, NiftyFuture], line.ResolvedSymbols);
            Assert.Equal(["ema-cross(2,3)", "vwap-cross"], line.Rules.Select(r => r.Key));
            Assert.Equal(5, response.Catalog.Count);

            var watch = response.Watches.Single(w => w.Symbol == Nifty);
            Assert.Equal(25, watch.HistoryCandles);
            Assert.Equal(["ready", "skipped"], watch.Rules.Select(r => r.State));
            Assert.Equal(1, response.AlertsToday);
            Assert.Equal(1, response.DeliveredToday);
            Assert.False(response.TelegramConfigured);

            var events = Assert.IsType<List<AlgoTrading.Contracts.Patterns.IndicatorAlertDto>>(
                Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await controller.GetIndicatorEvents(null, 15)).Result).Value);
            var alert = Assert.Single(events);
            Assert.Equal("ema-cross(2,3)", alert.Rule);
            Assert.Equal("EMA(2) crossed above EMA(3)", alert.What);
            Assert.Equal("up", alert.Direction);
            Assert.Equal("NIFTY", alert.DisplayName);
            Assert.True(alert.Values.ContainsKey("emaFast"));
            Assert.Empty(Assert.IsType<List<AlgoTrading.Contracts.Patterns.IndicatorAlertDto>>(
                Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await controller.GetIndicatorEvents(null, 5)).Result).Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No request may leave a test.");
    }

    private static Instrument Future(string symbol, string underlying, DateOnly expiry) => new()
    {
        Symbol = symbol, Underlying = underlying, Exchange = "NSE", Segment = "FO",
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
}
