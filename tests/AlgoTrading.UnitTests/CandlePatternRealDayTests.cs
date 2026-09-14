using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Services;
using Xunit;
using Xunit.Abstractions;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The detector run over real live 1-minute bars exported from the desk's
/// database: NSE/BSE on Tuesday 2026-09-08 (four indices, SENSEX, HDFCBANK,
/// SBIN — FYERS feed) and the MCX evening of Thursday 2026-09-10 (six futures).
/// </summary>
/// <remarks>
/// Prints every 15-minute pattern the scanner would have recorded (run with
/// <c>--logger "console;verbosity=detailed"</c> to see the table) and pins a
/// few that were checked by hand against the candle's OHLC.
/// </remarks>
public class CandlePatternRealDayTests
{
    private readonly ITestOutputHelper _output;

    public CandlePatternRealDayTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed record Hit(string Symbol, TimeframeBar Bar, PatternHit Pattern);

    private static List<(string Symbol, MinuteBar Bar)> LoadFixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "live_bars_real_days.csv")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "live_bars_real_days.csv");
        return File.ReadLines(path).Skip(1)
            .Select(line => line.Split(','))
            .Select(p => (p[0], new MinuteBar(
                DateTime.Parse(p[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
                decimal.Parse(p[2], CultureInfo.InvariantCulture),
                decimal.Parse(p[3], CultureInfo.InvariantCulture),
                decimal.Parse(p[4], CultureInfo.InvariantCulture),
                decimal.Parse(p[5], CultureInfo.InvariantCulture))))
            .ToList();
    }

    private static List<Hit> Detect(int timeframe)
    {
        var sessions = new MarketSessionService(new NoHolidays());
        var hits = new List<Hit>();

        foreach (var group in LoadFixture().GroupBy(x => (x.Symbol, Day: IstTime.DateOf(x.Bar.StartUtc))))
        {
            var exchange = CandlePatternRules.ExchangeOf(group.Key.Symbol);
            var noon = IstTime.FromIst(group.Key.Day.ToDateTime(new TimeOnly(12, 0)));
            var info = sessions.GetSessionInfo(noon, exchange, exchange == "MCX" ? "COM" : "CM");
            var window = new SessionWindow(info.SessionOpenUtc, info.SessionCloseUtc);

            var candles = SessionBarAggregator.Aggregate(group.Select(x => x.Bar), window, timeframe, window.CloseUtc)
                .Where(b => b.IsClosed).ToList();
            for (int i = 0; i < candles.Count; i++)
            {
                hits.AddRange(CandlePatternDetector.Detect(candles, i).Select(h => new Hit(group.Key.Symbol, candles[i], h)));
            }
        }

        return hits;
    }

    [Fact]
    public void Fifteen_minute_patterns_on_real_bars()
    {
        var hits = Detect(15);

        foreach (var h in hits.OrderBy(h => h.Symbol, StringComparer.Ordinal).ThenBy(h => h.Bar.StartUtc))
        {
            var ist = IstTime.ToIst(h.Bar.StartUtc);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{h.Symbol,-24} {ist:dd MMM HH:mm}  {CandlePatternCatalog.Key(h.Pattern.Pattern),-18} {CandlePatternCatalog.DirectionKey(h.Pattern.Direction),-8} O {h.Bar.Open} H {h.Bar.High} L {h.Bar.Low} C {h.Bar.Close}  ({h.Bar.MinutesWithData}/{h.Bar.MinutesExpected} min)"));
        }

        _output.WriteLine("");
        foreach (var g in hits.GroupBy(h => h.Pattern.Pattern).OrderByDescending(g => g.Count()))
        {
            _output.WriteLine($"{CandlePatternCatalog.Key(g.Key),-18} {g.Count()}");
        }

        _output.WriteLine($"total {hits.Count} on {hits.Select(h => h.Symbol).Distinct().Count()} symbols");

        // Checked by hand against the candles (independently re-aggregated in SQL):
        //
        // NIFTY 10:45 O 23669.8 H 23698.6 L 23669.3 C 23670.35 — body 0.55 ≤ 10% of 29.3,
        // lower wick 0.5 ≤ 2.93: gravestone doji. Upper wick 28.25 ≥ 2 × 0.55 and lower
        // 0.5 ≤ body: shooting star. Previous five closes average 23682.48 > 23670.35:
        // after a fall, so also an inverted hammer. Inside the 10:30 candle (H 23701.6, L 23667.75).
        Assert.Equal(
            [CandlePattern.GravestoneDoji, CandlePattern.InvertedHammer, CandlePattern.ShootingStar, CandlePattern.InsideBar],
            PatternsAt(hits, "NSE:NIFTY50-INDEX", 9, 8, 10, 45));

        // SBIN 10:30 O 1008.5 H 1008.7 L 1007 C 1008 — lower wick 1.0 is exactly 2 × body 0.5
        // (is_hammer's threshold is inclusive), upper 0.2 ≤ body. Previous five closes average
        // 1005.64 < 1008: after a rise, so also a hanging man.
        Assert.Equal([CandlePattern.Hammer, CandlePattern.HangingMan], PatternsAt(hits, "NSE:SBIN-EQ", 9, 8, 10, 30));

        // CRUDEOIL 22:00 after 21:30 O 9644 C 9729 (body 85, the whole range; midpoint 9686.5)
        // and 21:45 O 9727 C 9702 (body 25 ≤ 0.3 × 85, bottom 9702 above the midpoint):
        // O 9706 C 9680 is bearish, body 26 > 25, close below 9686.5 — evening star. Its upper
        // wick 61 ≥ 2 × 26 and lower 7 ≤ 26 make it a shooting star too.
        Assert.Equal([CandlePattern.ShootingStar, CandlePattern.EveningStar], PatternsAt(hits, "MCX:CRUDEOIL26SEPFUT", 9, 10, 22, 0));

        // SENSEX opened 75876.41 = its high and closed 75727.07 near the 75722.17 low: body 96.8% of range.
        Assert.Equal([CandlePattern.Marubozu], PatternsAt(hits, "BSE:SENSEX-INDEX", 9, 8, 9, 15));
    }

    [Fact]
    public void What_the_default_rules_would_have_recorded_and_sent()
    {
        // The seeded rules, with their groups resolved to the symbols these days streamed.
        var members = new Dictionary<string, string[]>
        {
            [PatternSymbolGroups.Indices] = ["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "NSE:FINNIFTY-INDEX", "BSE:SENSEX-INDEX"],
            [PatternSymbolGroups.RecordingStocks] = ["NSE:HDFCBANK-EQ", "NSE:SBIN-EQ"],
            ["future:CRUDEOIL"] = ["MCX:CRUDEOIL26SEPFUT"],
        };
        var rules = CandlePatternRules.Defaults(DateTime.UtcNow).Select(CandlePatternRules.Parse).ToList();

        var alerts = new List<Hit>();
        foreach (var tf in new[] { 5, 15 })
        {
            foreach (var hit in Detect(tf))
            {
                bool watched = rules.Any(r => r.Timeframes.Contains(tf) && r.Patterns.Contains(hit.Pattern.Pattern)
                                              && r.Groups.Any(g => members[g].Contains(hit.Symbol)));
                if (watched) alerts.Add(hit);
            }
        }

        // One message per closing minute, at most four in five minutes.
        var limiter = new SlidingWindowLimiter(4, TimeSpan.FromMinutes(5));
        int sent = 0, suppressed = 0;
        foreach (var minute in alerts.Select(a => a.Bar.EndUtc).Distinct().Order())
        {
            if (limiter.TryAcquire(minute)) sent++; else suppressed++;
        }

        foreach (var g in alerts.GroupBy(a => (a.Symbol, a.Bar.TimeframeMinutes)).OrderBy(g => g.Key.Symbol, StringComparer.Ordinal).ThenBy(g => g.Key.TimeframeMinutes))
        {
            _output.WriteLine($"{g.Key.Symbol,-24} {g.Key.TimeframeMinutes,2}m  {g.Count()} alerts");
        }

        _output.WriteLine($"alerts {alerts.Count}, Telegram messages {sent}, over the rate limit {suppressed}");
        Assert.NotEmpty(alerts);
        Assert.Equal(0, suppressed);
    }

    private static CandlePattern[] PatternsAt(List<Hit> hits, string symbol, int month, int day, int hour, int minute)
    {
        var startUtc = IstTime.FromIst(new DateTime(2026, month, day, hour, minute, 0));
        return hits.Where(h => h.Symbol == symbol && h.Bar.StartUtc == startUtc).Select(h => h.Pattern.Pattern).ToArray();
    }

    private sealed class NoHolidays : IMarketCalendar
    {
        public bool IsLoaded => true;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
