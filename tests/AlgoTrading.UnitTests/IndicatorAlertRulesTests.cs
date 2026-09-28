using AlgoTrading.Infrastructure.Patterns;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The indicator alerts' arithmetic, crossings, warm-up, cooldown and config
/// file, without a database.
/// </summary>
/// <remarks>
/// The reference numbers marked "Python" were produced by the strategies' own
/// functions (<c>strategies/indicators.py</c>: <c>ema</c>, <c>rsi</c>,
/// <c>supertrend</c>) on the same bars as <see cref="Series"/>; an alert and a
/// strategy must agree about where RSI is. The generator is written out in both
/// languages rather than shared, so a change to either shows up here.
/// </remarks>
public class IndicatorAlertRulesTests
{
    /// <summary>
    /// A zigzag with a drift that turns: up until <paramref name="upUntil"/>,
    /// down until <paramref name="downUntil"/>, up again after. In Python:
    /// <code>
    /// d = deltas[i % 10] + (0.8 if i &lt; up_until or i >= down_until else -0.8)
    /// close = round(close + d, 2); high = max(open, close) + 0.5 + (i % 3) * 0.25
    /// low = min(open, close) - 0.4 - (i % 4) * 0.2
    /// </code>
    /// </summary>
    private static List<TimeframeBar> Series(int n, int upUntil, int downUntil, int timeframe = 15)
    {
        decimal[] deltas = [1.25m, 0.5m, -0.75m, 2.0m, -1.5m, -0.25m, 0.75m, -2.25m, 1.0m, 0.5m];
        var start = new DateTime(2026, 9, 1, 3, 45, 0, DateTimeKind.Utc);
        var bars = new List<TimeframeBar>();
        decimal close = 100m;
        for (int i = 0; i < n; i++)
        {
            var d = deltas[i % 10] + (i < upUntil || i >= downUntil ? 0.8m : -0.8m);
            var open = close;
            close += d;
            var high = Math.Max(open, close) + 0.5m + (i % 3) * 0.25m;
            var low = Math.Min(open, close) - 0.4m - (i % 4) * 0.2m;
            var s = start.AddMinutes(i * timeframe);
            bars.Add(new TimeframeBar(timeframe, i, s, s.AddMinutes(timeframe), open, high, low, close, timeframe, timeframe, true));
        }

        return bars;
    }

    private static List<double> Closes(IEnumerable<TimeframeBar> bars) => bars.Select(b => (double)b.Close).ToList();

    // ------------------------------------------------------------------ math --

    [Fact]
    public void Ema_is_seeded_with_the_average_and_matches_the_textbook_series()
    {
        // 1..10 with period 3: seed (1+2+3)/3 = 2, then v/2 + ema/2 → 3, 4, … 9.
        var ema = IndicatorMath.Ema(Enumerable.Range(1, 10).Select(i => (double)i).ToList(), 3);
        Assert.Null(ema[0]);
        Assert.Null(ema[1]);
        Assert.Equal([2.0, 3, 4, 5, 6, 7, 8, 9], ema.Skip(2).Select(v => v!.Value).ToArray());
    }

    [Fact]
    public void Ema_matches_the_strategies_python_on_the_same_bars()
    {
        var closes = Closes(Series(90, 30, 60));
        var fast = IndicatorMath.Ema(closes, 9);
        var slow = IndicatorMath.Ema(closes, 21);

        Assert.Null(fast[7]);
        Assert.Null(slow[19]);
        // Python: ema(closes[:i + 1], 9) and ema(closes[:i + 1], 21).
        Assert.Equal(105.36111111111111, fast[8]!.Value, 9);
        Assert.Equal(106.13888888888889, fast[9]!.Value, 9);
        Assert.Equal(116.18564199981513, fast[20]!.Value, 9);
        Assert.Equal(110.85952380952382, slow[20]!.Value, 9);
        Assert.Equal(125.09035869650928, fast[30]!.Value, 9);
        Assert.Equal(119.87612387638754, slow[30]!.Value, 9);
        Assert.Equal(120.35850477542624, fast[45]!.Value, 9);
        Assert.Equal(121.00873249845257, slow[45]!.Value, 9);
        Assert.Equal(131.81705971616083, fast[89]!.Value, 9);
        Assert.Equal(127.34762429819106, slow[89]!.Value, 9);
    }

    [Fact]
    public void Rsi_matches_wilders_worked_example_and_the_strategies_python()
    {
        // The worked example every RSI write-up uses (StockCharts' ChartSchool).
        double[] closes =
        [
            44.34, 44.09, 44.15, 43.61, 44.33, 44.83, 45.10, 45.42, 45.84, 46.08, 45.89, 46.03, 45.61, 46.28, 46.28,
            46.00, 46.03, 46.41, 46.22, 45.64, 46.21, 46.25, 45.71, 46.45, 45.78, 45.35, 44.03, 44.18, 44.22, 44.57,
            43.42, 42.66, 43.13,
        ];
        var rsi = IndicatorMath.Rsi(closes, 14);
        Assert.All(rsi.Take(14), v => Assert.Null(v));

        // The published table rounds its average gain and loss to two decimals
        // at every step, which leaves its RSI up to 0.07 from the exact one.
        double[] published = [70.53, 66.32, 66.55, 69.41, 66.36, 57.97, 62.93, 63.26, 56.06, 62.38, 54.71, 50.42, 39.99, 41.46, 41.87, 45.46, 37.30, 33.08, 37.77];
        // Python: round(rsi(closes[:i + 1], 14), 2) — the exact values.
        double[] python = [70.46, 66.25, 66.48, 69.35, 66.29, 57.92, 62.88, 63.21, 56.01, 62.34, 54.67, 50.39, 40.02, 41.49, 41.9, 45.5, 37.32, 33.09, 37.79];
        for (int k = 0; k < published.Length; k++)
        {
            Assert.InRange(rsi[14 + k]!.Value, published[k] - 0.1, published[k] + 0.1);
            Assert.Equal(python[k], Math.Round(rsi[14 + k]!.Value, 2));
        }

        var series = IndicatorMath.Rsi(Closes(Series(90, 30, 60)), 14);
        Assert.Equal(84.51086956521742, series[14]!.Value, 9);
        Assert.Equal(84.99392466585664, series[15]!.Value, 9);
        Assert.Equal(41.228214086673674, series[45]!.Value, 9);
        Assert.Equal(34.334141257121715, series[60]!.Value, 9);
        Assert.Equal(77.51483139109914, series[89]!.Value, 9);
    }

    [Fact]
    public void Rsi_of_a_flat_series_is_50_and_of_a_rising_one_is_100()
    {
        Assert.Equal(50.0, IndicatorMath.Rsi(Enumerable.Repeat(100.0, 20).ToList(), 14)[19]);
        Assert.Equal(100.0, IndicatorMath.Rsi(Enumerable.Range(0, 20).Select(i => 100.0 + i).ToList(), 14)[19]);
    }

    [Fact]
    public void Supertrend_matches_the_strategies_python_and_flips_where_it_does()
    {
        var st = IndicatorMath.Supertrend(Series(90, 30, 60), 10, 3.0);

        Assert.Null(st[10]);
        // Python: supertrend(bars[:i + 1], 10, 3.0) → direction, round(level, 2).
        (int Index, bool Up, double Line)[] python =
        [
            (11, true, 103.39), (25, true, 116.28), (39, true, 120.02), (41, true, 120.02),
            (42, false, 127.69), (50, false, 121.96), (67, false, 115.27), (68, true, 106.73), (89, true, 126.4),
        ];
        foreach (var (index, up, line) in python)
        {
            Assert.Equal(up, st[index]!.Value.Up);
            Assert.Equal(line, Math.Round(st[index]!.Value.Line, 2));
        }

        var flips = Enumerable.Range(12, 78).Where(i => st[i]!.Value.Up != st[i - 1]!.Value.Up).ToArray();
        Assert.Equal([42, 68], flips);

        // The band each flip's close went through: on that candle only, on the
        // right side of the close, and (the bands only tighten) at or beyond
        // where the line stood a candle earlier.
        var bars = Series(90, 30, 60);
        Assert.All(Enumerable.Range(11, 79).Where(i => i is not 42 and not 68), i => Assert.Null(st[i]!.Value.Through));
        Assert.True((double)bars[42].Close < st[42]!.Value.Through);
        Assert.True(st[42]!.Value.Through >= st[41]!.Value.Line);
        Assert.True((double)bars[68].Close > st[68]!.Value.Through);
        Assert.True(st[68]!.Value.Through <= st[67]!.Value.Line);
    }

    [Fact]
    public void Vwap_is_volume_weighted_anchored_at_the_open_and_absent_without_volume()
    {
        var open = new DateTime(2026, 9, 15, 3, 45, 0, DateTimeKind.Utc);
        var session = new SessionWindow(open, open.AddMinutes(375));
        MinuteBar M(int minute, decimal h, decimal l, decimal c, long v) => new(open.AddMinutes(minute), c, h, l, c, v);
        var minutes = new[]
        {
            M(-5, 999, 999, 999, 1_000_000), // pre-open: not the session's
            M(0, 102, 99, 102, 100),         // typical 101
            M(1, 105, 102, 105, 300),        // typical 104
            M(2, 104, 101, 101, 0),          // nothing traded: no weight
        };
        var candles = new[]
        {
            new TimeframeBar(1, 0, open, open.AddMinutes(1), 99, 102, 99, 102, 1, 1, true),
            new TimeframeBar(1, 1, open.AddMinutes(1), open.AddMinutes(2), 102, 105, 102, 105, 1, 1, true),
            new TimeframeBar(1, 2, open.AddMinutes(2), open.AddMinutes(3), 105, 104, 101, 101, 1, 1, true),
        };

        var vwap = IndicatorMath.SessionVwap(candles, minutes, session);
        Assert.Equal(101.0, vwap[0]!.Value, 9);
        Assert.Equal((101.0 * 100 + 104.0 * 300) / 400, vwap[1]!.Value, 9);
        Assert.Equal(vwap[1], vwap[2]);

        // An index: every minute has volume 0, so there is no VWAP at all.
        var index = IndicatorMath.SessionVwap(candles, minutes.Skip(1).Select(m => m with { Volume = 0 }), session);
        Assert.All(index, v => Assert.Null(v));
    }

    // -------------------------------------------------------------- crossings --

    /// <summary>On 210 bars the default rules have settled well before the turns at 90 and 150.</summary>
    private static readonly List<TimeframeBar> Long = Series(210, 90, 150);

    [Fact]
    public void Each_rule_fires_on_the_candle_it_crosses_and_says_which_way()
    {
        var hits = IndicatorEvaluator.Evaluate(Long, 0, IndicatorRule.Defaults.Where(r => !r.NeedsVolume));

        // Python, on the same bars: supertrend flips at 102 and 158, EMA(9)
        // crosses EMA(21) at 104 and 160, RSI crosses 30 at 115 and 70 at 166 and 169.
        Assert.Equal(
            [
                (102, "supertrend-flip(10,3)", false), (104, "ema-cross(9,21)", false), (115, "rsi-below(14,30)", false),
                (158, "supertrend-flip(10,3)", true), (160, "ema-cross(9,21)", true), (166, "rsi-above(14,70)", true),
                (169, "rsi-above(14,70)", true),
            ],
            hits.Select(h => (h.Index, h.Rule.Key, h.Up)).ToArray());

        var rsi = hits.Single(h => h.Index == 166);
        Assert.Equal(67.5209287035934, rsi.Values.Single(v => v.Name == "rsiBefore").Value, 9);
        Assert.Equal(70.44456455689928, rsi.Values.Single(v => v.Name == "rsi").Value, 9);
        var ema = hits.Single(h => h.Index == 160);
        Assert.Equal(150.20707233303352, ema.Values.Single(v => v.Name == "emaFast").Value, 9);
        Assert.Equal(149.80588149507648, ema.Values.Single(v => v.Name == "emaSlow").Value, 9);
    }

    [Fact]
    public void A_rule_stays_quiet_until_it_has_read_enough_candles_to_settle()
    {
        // On 90 bars the same shape crosses earlier: supertrend at 42 and 68,
        // EMA at 44 and 69, RSI below 30 at 55. Supertrend settles after 50
        // candles, RSI after 70, EMA(9,21) after 84: only what came later speaks.
        var hits = IndicatorEvaluator.Evaluate(Series(90, 30, 60), 0, IndicatorRule.Defaults.Where(r => !r.NeedsVolume));

        Assert.Equal(
            [(68, "supertrend-flip(10,3)"), (73, "rsi-above(14,70)"), (76, "rsi-above(14,70)"), (79, "rsi-above(14,70)")],
            hits.Select(h => (h.Index, h.Rule.Key)).ToArray());
        Assert.Equal(84, IndicatorRule.EmaCrossDefault.SettleCandles);
        Assert.Equal(70, IndicatorRule.RsiAboveDefault.SettleCandles);
        Assert.Equal(50, IndicatorRule.SupertrendFlipDefault.SettleCandles);
    }

    [Fact]
    public void Earlier_sessions_warm_the_indicators_but_never_alert()
    {
        // Candles before today's first (index 150) are history: they feed the
        // values, and the crosses among them are not today's alerts.
        var hits = IndicatorEvaluator.Evaluate(Long, 150, IndicatorRule.Defaults.Where(r => !r.NeedsVolume));
        Assert.Equal([158, 160, 166, 169], hits.Select(h => h.Index).ToArray());
    }

    [Fact]
    public void Nothing_repaints_a_candle_once_closed_says_the_same_whatever_follows()
    {
        var rules = IndicatorRule.Defaults.Where(r => !r.NeedsVolume).ToList();
        var full = IndicatorEvaluator.Evaluate(Long, 0, rules).Select(h => (h.Index, h.Rule.Key, h.Up, Values: string.Join(',', h.Values))).ToList();

        // Every prefix: what the scanner saw when that candle was the last closed one.
        for (int k = 1; k <= Long.Count; k++)
        {
            var seen = IndicatorEvaluator.Evaluate(Long.Take(k).ToList(), 0, rules)
                .Select(h => (h.Index, h.Rule.Key, h.Up, Values: string.Join(',', h.Values))).ToList();
            Assert.Equal(full.Where(h => h.Index < k), seen);
        }
    }

    [Fact]
    public void Touching_is_not_crossing_a_flat_stretch_then_a_rise_is_no_ema_cross()
    {
        // Flat for 100 candles: both EMAs equal the price (and differ only in
        // the last floating-point place). Then it rises: EMA(9) goes above
        // EMA(21) from level, never from below, so there is no cross.
        var start = new DateTime(2026, 9, 1, 3, 45, 0, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 120).Select(i =>
        {
            decimal c = i < 100 ? 24_987.35m : 24_987.35m + (i - 99) * 3;
            return new TimeframeBar(5, i, start.AddMinutes(5 * i), start.AddMinutes(5 * i + 5), c, c + 1, c - 1, c, 5, 5, true);
        }).ToList();

        Assert.Empty(IndicatorEvaluator.Evaluate(bars, 0, [IndicatorRule.EmaCrossDefault]));
    }

    [Fact]
    public void Vwap_cross_counts_only_within_the_session_and_only_with_volume()
    {
        var bars = Series(6, 3, 100, timeframe: 5);
        // Candle 0 is yesterday's (no VWAP); today: below, below, above, above, below.
        double?[] vwap = [null, 200, 200, 90, 90, 1000];
        var hits = IndicatorEvaluator.Evaluate(bars, 1, [IndicatorRule.VwapCrossDefault], vwap);
        Assert.Equal([(3, true), (5, false)], hits.Select(h => (h.Index, h.Up)).ToArray());
        Assert.Equal(90.0, hits[0].Values.Single().Value);

        Assert.Empty(IndicatorEvaluator.Evaluate(bars, 1, [IndicatorRule.VwapCrossDefault], vwap: null));
    }

    // --------------------------------------------------------------- cooldown --

    [Fact]
    public void Cooldown_holds_back_a_repeat_of_the_same_rule_within_thirty_minutes_of_the_last_alert()
    {
        var rules = new[] { IndicatorRule.RsiAboveDefault, IndicatorRule.EmaCrossDefault };

        // Five-minute candles: RSI crosses 70 at 166 and again at 169, fifteen
        // minutes later — cooled. The EMA cross at 160 is another rule: its own window.
        var five = Series(210, 90, 150, timeframe: 5);
        var cooled = IndicatorEvaluator.ApplyCooldown(IndicatorEvaluator.Evaluate(five, 150, rules), TimeSpan.FromMinutes(30));
        Assert.Equal([(160, false), (166, false), (169, true)], cooled.Select(c => (c.Hit.Index, c.CooledDown)).ToArray());
        Assert.Equal(five[166].EndUtc, cooled.Single(c => c.Hit.Index == 169).CoolingSinceUtc);

        // Fifteen-minute candles: the repeat is 45 minutes on, past the window.
        var fifteen = IndicatorEvaluator.ApplyCooldown(IndicatorEvaluator.Evaluate(Long, 150, rules), TimeSpan.FromMinutes(30));
        Assert.All(fifteen, c => Assert.False(c.CooledDown));

        // Off.
        Assert.All(IndicatorEvaluator.ApplyCooldown(IndicatorEvaluator.Evaluate(five, 150, rules), TimeSpan.Zero), c => Assert.False(c.CooledDown));
    }

    [Fact]
    public void Cooldown_runs_from_the_last_alert_so_a_market_that_keeps_crossing_still_alerts_once_a_window()
    {
        var rule = IndicatorRule.EmaCrossDefault;
        var t = new DateTime(2026, 9, 15, 4, 0, 0, DateTimeKind.Utc);
        IndicatorHit Hit(int minutes, bool up)
        {
            var bar = new TimeframeBar(5, minutes / 5, t.AddMinutes(minutes - 5), t.AddMinutes(minutes), 1, 1, 1, 1, 5, 5, true);
            return new IndicatorHit(rule, minutes / 5, bar, up, []);
        }

        // Crosses at +0, +10, +20, +30, +40: alerts at +0 and +30 only.
        var cooled = IndicatorEvaluator.ApplyCooldown([Hit(0, true), Hit(10, false), Hit(20, true), Hit(30, false), Hit(40, true)], TimeSpan.FromMinutes(30));
        Assert.Equal([false, true, true, false, true], cooled.Select(c => c.CooledDown).ToArray());
    }

    // ----------------------------------------------------------------- config --

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine([dir.FullName, .. parts]))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    [Fact]
    public void The_shipped_config_reads_cleanly_and_watches_the_three_indices_on_5_and_15_minutes()
    {
        var config = IndicatorAlertConfig.Parse(File.ReadAllText(RepoFile("config", "indicator-alerts.txt")));

        Assert.Empty(config.Warnings);
        Assert.Equal(TimeSpan.FromMinutes(30), config.Cooldown);
        Assert.True(config.Telegram);
        Assert.Equal(250, config.WarmupCandles);

        var line = Assert.Single(config.Lines);
        Assert.Equal(["NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "BSE:SENSEX-INDEX"], line.Symbols);
        Assert.Equal([5, 15], line.Timeframes);
        Assert.Equal(["rsi-above(14,70)", "rsi-below(14,30)", "ema-cross(9,21)", "supertrend-flip(10,3)"], line.Rules.Select(r => r.Key));
        Assert.False(line.PageOnly);
        // Indices have no volume: VWAP is not on their line.
        Assert.DoesNotContain(line.Rules, r => r.NeedsVolume);
    }

    [Fact]
    public void Rules_take_their_numbers_or_the_defaults_in_any_case_and_spacing()
    {
        Assert.Equal("rsi-above(14,70)", IndicatorRule.TryParse("rsi-above", out _)!.Key);
        Assert.Equal("rsi-below(7,25.5)", IndicatorRule.TryParse("RSI-BELOW(7, 25.5)", out _)!.Key);
        Assert.Equal("ema-cross(5,21)", IndicatorRule.TryParse("ema-cross(5)", out _)!.Key);
        Assert.Equal("supertrend-flip(7,2.5)", IndicatorRule.TryParse("supertrend-flip(7,2.5)", out _)!.Key);
        Assert.Equal("vwap-cross", IndicatorRule.TryParse("vwap-cross", out _)!.Key);
        Assert.Equal("RSI(14) crosses above 70", IndicatorRule.RsiAboveDefault.Label);

        Assert.Null(IndicatorRule.TryParse("macd-cross", out var unknown));
        Assert.Contains("unknown rule 'macd-cross'", unknown);
        Assert.Null(IndicatorRule.TryParse("ema-cross(21,9)", out var backwards));
        Assert.Contains("fast EMA period must be below the slow one", backwards);
        Assert.Null(IndicatorRule.TryParse("rsi-above(14,170)", out var level));
        Assert.Contains("between 0 and 100", level);
        Assert.Null(IndicatorRule.TryParse("rsi-above(14.5)", out var fraction));
        Assert.Contains("whole number", fraction);
        Assert.Null(IndicatorRule.TryParse("vwap-cross(20)", out var vwapArgs));
        Assert.Contains("takes no numbers", vwapArgs);
    }

    [Fact]
    public void A_bad_line_is_reported_with_its_number_and_the_rest_of_the_file_still_counts()
    {
        const string text = """
            # comment only
            cooldown: soon
            cooldown: 10
            telegram: maybe
            colour: blue
            NSE:NIFTY50-INDEX  5,7m,15  rsi-above(14,70) macd-cross   # a comment after the line is fine
            NIFTY,NSE:SBIN-EQ  15  ema-cross(21,9) ema-cross(5,13)  page-only
            NSE:NIFTYBANK-INDEX,future:NIFTY  5  vwap-cross supertrend-flip
            NFO:NIFTY 5 rsi-above
            BSE:SENSEX-INDEX 15
            """;

        var config = IndicatorAlertConfig.Parse(text);

        Assert.Equal(
            [
                "Line 2: cooldown 'soon' is not a number of minutes from 0 to 1440; 30 is used.",
                "Line 3: a second 'cooldown:' line; the first one is used.",
                "Line 4: telegram 'maybe' is neither on nor off; on is used.",
                "Line 5: unknown setting 'colour:'; the settings are cooldown, telegram and warmup.",
                "Line 6: timeframe '7m' is not one of 3, 5, 15, 30, 60 minutes; left out.",
                "Line 6: unknown rule 'macd-cross'; the rules are rsi-above, rsi-below, ema-cross, supertrend-flip, vwap-cross; left out.",
                "Line 7: 'NIFTY' is neither EXCHANGE:NAME on NSE, BSE or MCX nor future:UNDERLYING; left out.",
                "Line 7: 'ema-cross(21,9)': the fast EMA period must be below the slow one; left out.",
                "Line 8: vwap-cross on NSE:NIFTYBANK-INDEX: an index has no traded volume, so it has no VWAP; not watched there.",
                "Line 9: 'NFO:NIFTY' is neither EXCHANGE:NAME on NSE, BSE or MCX nor future:UNDERLYING; left out.",
                "Line 9: no symbol left that could be read; the line is skipped.",
                "Line 10: 'BSE:SENSEX-INDEX 15' needs symbols, timeframes and at least one rule; skipped.",
            ],
            config.Warnings);

        // The first cooldown line was unreadable, and is still the one that counts.
        Assert.Equal(TimeSpan.FromMinutes(30), config.Cooldown);
        Assert.True(config.Telegram);

        Assert.Equal([6, 7, 8], config.Lines.Select(l => l.Number));
        Assert.Equal([5, 15], config.Lines[0].Timeframes);
        Assert.Equal(["rsi-above(14,70)"], config.Lines[0].Rules.Select(r => r.Key));
        Assert.Equal(["NSE:SBIN-EQ"], config.Lines[1].Symbols);
        Assert.True(config.Lines[1].PageOnly);
        Assert.Equal(["future:NIFTY"], config.Lines[2].Groups);
        Assert.Equal(["supertrend-flip(10,3)", "vwap-cross"], config.Lines[2].Rules.Select(r => r.Key));
    }

    [Fact]
    public void An_empty_file_or_a_short_warmup_is_said_rather_than_silently_run()
    {
        var empty = IndicatorAlertConfig.Parse("# nothing yet\n");
        Assert.Empty(empty.Lines);
        Assert.Equal(["No watch line: nothing is watched. A line is SYMBOLS TIMEFRAMES RULE [RULE ...]."], empty.Warnings);

        var shortWarmup = IndicatorAlertConfig.Parse("warmup: 40\nNSE:NIFTY50-INDEX 15 ema-cross\n");
        Assert.Equal(40, shortWarmup.WarmupCandles);
        Assert.Contains(shortWarmup.Warnings, w => w.StartsWith("warmup 40 is below the 84 candles ema-cross(9,21) settles over"));

        Assert.Empty(IndicatorAlertConfig.Parse(null).Lines);
    }
}
