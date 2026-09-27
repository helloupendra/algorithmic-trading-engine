using System.Text.Json;
using AlgoTrading.Api.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The scoreboard's arithmetic and its verdicts: the pass mark that was fixed
/// before any live forecast existed, so nobody can move it after seeing them.
/// </summary>
public class ForecastScoreboardTests
{
    private const string Key = "range.har-vix";
    private const string Version = "2026-09-27.1";
    private static readonly DateOnly Start = new(2026, 9, 28);

    [Fact]
    public void Skill_is_one_minus_the_ratio_of_mean_losses()
    {
        var rows = Board(Scored("NIFTY", 0, 0.2, 0.25), Scored("NIFTY", 1, 0.3, 0.35));

        var all = Row(rows, "ALL");
        Assert.Equal(2, all.LiveCount);
        Assert.Equal(0.25, all.MeanLoss!.Value, 12);
        Assert.Equal(0.30, all.MeanBaselineLoss!.Value, 12);
        Assert.Equal(1 - 0.25 / 0.30, all.Skill!.Value, 12);
        Assert.Equal(Start, all.FirstSession);
        Assert.Equal(Start.AddDays(1), all.LastSession);
    }

    [Fact]
    public void Unscored_forecasts_put_an_underlying_on_the_board_but_are_not_counted()
    {
        var rows = Board(Scored("NIFTY", 0, 0.2, 0.3), Issued("SENSEX", 1));

        Assert.Equal(new[] { "ALL", "NIFTY", "SENSEX" }, rows.Select(r => r.Underlying));
        Assert.Equal(1, Row(rows, "ALL").LiveCount);

        var sensex = Row(rows, "SENSEX");
        Assert.Equal(0, sensex.LiveCount);
        Assert.Null(sensex.MeanLoss);
        Assert.Null(sensex.Skill);
        Assert.Null(sensex.DiffCiLow);
        Assert.Null(sensex.FirstSession);
        Assert.Equal(ForecastScoreboard.Collecting, sensex.Status);
    }

    [Fact]
    public void A_registered_model_without_forecasts_has_its_ALL_row_and_its_backtest()
    {
        var backtest = JsonSerializer.SerializeToElement(new { holdout = new { skill = 0.073 } });

        var rows = ForecastScoreboard.Build([new ScoreboardModel(Key, Version, "range", "Log-range model.", backtest)], []);

        var row = Assert.Single(rows);
        Assert.Equal("ALL", row.Underlying);
        Assert.Equal(0, row.LiveCount);
        Assert.Equal(ForecastScoreboard.Collecting, row.Status);
        Assert.Equal("Log-range model.", row.Description);
        Assert.Equal(0.073, row.Backtest!.Value.GetProperty("holdout").GetProperty("skill").GetDouble());
        Assert.Empty(row.Calibration);
    }

    [Fact]
    public void The_bootstrap_interval_is_the_same_every_time_whatever_order_the_rows_come_in()
    {
        // The page reloads; an interval that moved on every load would make
        // the verdict flicker between testing and proven.
        var random = new Random(7);
        var forecasts = Enumerable.Range(0, 45)
            .Select(i => Scored("NIFTY", i, 0.2 + random.NextDouble() * 0.1, 0.22 + random.NextDouble() * 0.1))
            .ToList();

        var first = ForecastScoreboard.BootstrapInterval(forecasts)!.Value;
        var again = ForecastScoreboard.BootstrapInterval(forecasts)!.Value;
        var shuffled = ForecastScoreboard.BootstrapInterval(forecasts.OrderByDescending(f => f.SessionDate).ToList())!.Value;
        var fromBoard = Row(Board(forecasts.ToArray()), "NIFTY");

        Assert.Equal(first, again);
        Assert.Equal(first, shuffled);
        Assert.Equal(first.Low, fromBoard.DiffCiLow);
        Assert.Equal(first.High, fromBoard.DiffCiHigh);

        double mean = forecasts.Average(f => f.BaselineLoss!.Value - f.Loss!.Value);
        Assert.True(first.Low < mean && mean < first.High, $"{first.Low} < {mean} < {first.High}");
    }

    [Fact]
    public void The_bootstrap_interval_is_pinned_so_a_change_to_the_generator_is_noticed()
    {
        // Differences 0.01, 0.02 … 0.10 over ten sessions. If this moves, every
        // interval on the page moved with it: that needs a reason, not a
        // re-baselined number.
        var forecasts = Enumerable.Range(0, 10).Select(i => Scored("NIFTY", i, 0.2, 0.2 + (i + 1) / 100.0)).ToList();

        var (low, high) = ForecastScoreboard.BootstrapInterval(forecasts)!.Value;

        Assert.Equal(PinnedLow, low, 12);
        Assert.Equal(PinnedHigh, high, 12);
        Assert.True(low > 0.03 && high < 0.08);
    }

    [Fact]
    public void A_constant_difference_has_an_interval_of_exactly_that_difference()
    {
        var forecasts = Enumerable.Range(0, 30).Select(i => Scored("NIFTY", i, 0.2, 0.25)).ToList();

        var (low, high) = ForecastScoreboard.BootstrapInterval(forecasts)!.Value;

        Assert.Equal(0.05, low, 12);
        Assert.Equal(0.05, high, 12);
    }

    [Fact]
    public void One_session_is_not_an_interval()
    {
        // Three underlyings on one day are still one day.
        var oneDay = new[] { Scored("NIFTY", 0, 0.2, 0.3), Scored("BANKNIFTY", 0, 0.1, 0.3), Scored("SENSEX", 0, 0.2, 0.25) };

        Assert.Null(ForecastScoreboard.BootstrapInterval(oneDay));
        Assert.Null(ForecastScoreboard.BootstrapInterval([]));
        Assert.Null(Row(Board(oneDay), "ALL").DiffCiLow);
    }

    [Fact]
    public void The_ALL_row_resamples_sessions_so_three_correlated_indices_are_not_three_times_the_evidence()
    {
        // The same difference on all three indices every day: one piece of
        // evidence a day. Resampled as independent forecasts the ALL interval
        // would be about √3 narrower than one index's, and a model could be
        // called Proven on a third of the evidence the rule asks for.
        var random = new Random(11);
        var forecasts = new List<ScoreboardForecast>();
        for (int day = 0; day < 40; day++)
        {
            double loss = 0.2 + random.NextDouble() * 0.1;
            double baseline = 0.2 + random.NextDouble() * 0.1;
            forecasts.AddRange(new[] { "NIFTY", "BANKNIFTY", "SENSEX" }.Select(u => Scored(u, day, loss, baseline)));
        }

        var rows = Board(forecasts.ToArray());
        var all = Row(rows, "ALL");
        var nifty = Row(rows, "NIFTY");

        Assert.Equal(120, all.LiveCount);
        Assert.Equal(nifty.DiffCiLow!.Value, all.DiffCiLow!.Value, 12);
        Assert.Equal(nifty.DiffCiHigh!.Value, all.DiffCiHigh!.Value, 12);
    }

    [Theory]
    [InlineData(0, null, null, "collecting")]
    [InlineData(19, 0.1, 0.2, "collecting")]
    [InlineData(20, 0.1, 0.2, "testing")]
    [InlineData(59, 0.01, 0.1, "testing")]
    [InlineData(60, 0.01, 0.1, "proven")]
    [InlineData(60, 0.0, 0.1, "testing")]
    [InlineData(200, -0.01, 0.1, "testing")]
    [InlineData(119, -0.2, -0.01, "testing")]
    [InlineData(120, -0.2, -0.01, "retired")]
    [InlineData(120, -0.2, 0.0, "testing")]
    public void The_verdict_follows_the_fixed_rule(int n, double? low, double? high, string expected)
    {
        var (status, reason) = ForecastScoreboard.Judge(n, low, high);

        Assert.Equal(expected, status);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void The_reason_says_where_the_model_stands_in_words()
    {
        Assert.Equal(
            "42 of 60 scored forecasts; the confidence interval still includes no improvement",
            ForecastScoreboard.Judge(42, -0.004, 0.061).Reason);
        Assert.StartsWith("7 of 20", ForecastScoreboard.Judge(7, null, null).Reason);
        Assert.Contains("ahead of the baseline so far", ForecastScoreboard.Judge(45, 0.01, 0.05).Reason);
        Assert.Contains("behind the baseline so far", ForecastScoreboard.Judge(80, -0.05, -0.01).Reason);
    }

    [Fact]
    public void Sixty_sessions_clearly_better_than_the_baseline_are_proven_on_that_row()
    {
        var forecasts = Enumerable.Range(0, 60).Select(i => Scored("NIFTY", i, 0.2, 0.26 + (i % 5) / 100.0)).ToArray();

        var nifty = Row(Board(forecasts), "NIFTY");

        Assert.Equal(ForecastScoreboard.Proven, nifty.Status);
        Assert.True(nifty.DiffCiLow > 0);
    }

    [Fact]
    public void Calibration_groups_stated_probabilities_into_tenths()
    {
        var bins = ForecastScoreboard.Calibrate(new[]
        {
            new CalibrationPair(0.0, 0),
            new CalibrationPair(0.05, 1),
            new CalibrationPair(0.3, 1),   // written as 0.3, stored as 0.29999999999999999: the 0.3 tenth
            new CalibrationPair(0.31, 0),
            new CalibrationPair(0.39, 1),
            new CalibrationPair(1.0, 1),   // the top tenth includes certainty
            new CalibrationPair(1.2, 1),   // not a probability: left out
            new CalibrationPair(double.NaN, 0),
        });

        Assert.Equal(new[] { 0.0, 0.3, 0.9 }, bins.Select(b => b.From));
        Assert.Equal(new[] { 0.1, 0.4, 1.0 }, bins.Select(b => b.To));
        Assert.Equal(new[] { 2, 3, 1 }, bins.Select(b => b.N));

        var third = bins[1];
        Assert.Equal((0.3 + 0.31 + 0.39) / 3, third.MeanP, 12);
        Assert.Equal(2.0 / 3, third.HitRate, 12);
        Assert.Equal(0.5, bins[0].HitRate, 12);
    }

    [Fact]
    public void Coverage80_is_the_share_inside_the_band_and_absent_for_targets_without_one()
    {
        var range = Board(
            Scored("NIFTY", 0, 0.1, 0.2, covered: true),
            Scored("NIFTY", 1, 0.1, 0.2, covered: true),
            Scored("NIFTY", 2, 0.1, 0.2, covered: false),
            Scored("NIFTY", 3, 0.1, 0.2, covered: true));
        var trend = ForecastScoreboard.Build([], [Scored("NIFTY", 0, 0.2, 0.25, target: "trend", key: "trend.logit")]);

        Assert.Equal(0.75, Row(range, "ALL").Coverage80);
        Assert.Null(Row(trend, "ALL").Coverage80);
    }

    [Fact]
    public void Rows_come_by_target_then_model_newest_version_first_then_ALL_NIFTY_BANKNIFTY_SENSEX()
    {
        var rows = ForecastScoreboard.Build(
            [
                new ScoreboardModel("direction.logit", "v1", "direction", "d", null),
                new ScoreboardModel("trend.logit", "v1", "trend", "t", null),
                new ScoreboardModel(Key, "2026-09-27.1", "range", "r", null),
                new ScoreboardModel(Key, "2026-10-15.1", "range", "r2", null),
            ],
            [
                Scored("SENSEX", 0, 0.1, 0.2),
                Scored("NIFTY", 0, 0.1, 0.2),
                Scored("BANKNIFTY", 0, 0.1, 0.2),
            ]);

        Assert.Equal(
            new[]
            {
                "range.har-vix 2026-10-15.1 ALL",
                "range.har-vix 2026-09-27.1 ALL",
                "range.har-vix 2026-09-27.1 NIFTY",
                "range.har-vix 2026-09-27.1 BANKNIFTY",
                "range.har-vix 2026-09-27.1 SENSEX",
                "trend.logit v1 ALL",
                "direction.logit v1 ALL",
            },
            rows.Select(r => $"{r.ModelKey} {r.ModelVersion} {r.Underlying}"));
    }

    [Fact]
    public void Stored_scores_are_read_for_coverage_and_calibration_and_bad_ones_are_skipped()
    {
        var (covered, pairs) = ForecastScoreboard.ReadScores(
            """{ "loss": 0.004, "metrics": { "covered80": true }, "calibration": [ { "p": 0.31, "y": 0 }, { "p": 0.45, "y": true }, { "p": "x", "y": 1 }, { "p": 0.2, "y": 2 } ] }""");

        Assert.True(covered);
        Assert.Equal(new[] { new CalibrationPair(0.31, 0), new CalibrationPair(0.45, 1) }, pairs);

        Assert.Equal((null, 0), Summary(ForecastScoreboard.ReadScores("{not json")));
        Assert.Equal((null, 0), Summary(ForecastScoreboard.ReadScores(null)));
        Assert.Equal((null, 1), Summary(ForecastScoreboard.ReadScores("""{ "calibration": [ { "p": 0.38, "y": 1 } ] }""")));
    }

    // ---------- helpers ----------

    // The same numbers come out of a separate Python implementation of
    // SplitMix64 (seed 20260927), the multiply-high draw and numpy's linear
    // percentile, so a check of the page's intervals from Python can agree.
    private const double PinnedLow = 0.037;
    private const double PinnedHigh = 0.073;

    private static (bool?, int) Summary((bool? Covered, IReadOnlyList<CalibrationPair> Pairs) scores) => (scores.Covered, scores.Pairs.Count);

    private static IReadOnlyList<ScoreboardRow> Board(params ScoreboardForecast[] forecasts)
        => ForecastScoreboard.Build([new ScoreboardModel(Key, Version, "range", "d", null)], forecasts);

    private static ScoreboardRow Row(IReadOnlyList<ScoreboardRow> rows, string underlying)
        => rows.Single(r => r.Underlying == underlying);

    private static ScoreboardForecast Scored(
        string underlying,
        int day,
        double loss,
        double baselineLoss,
        bool? covered = null,
        string target = "range",
        string key = Key)
        => new(key, Version, target, underlying, Start.AddDays(day), loss, baselineLoss, covered, [new CalibrationPair(0.35, day % 2)]);

    private static ScoreboardForecast Issued(string underlying, int day)
        => new(Key, Version, "range", underlying, Start.AddDays(day), null, null, null, []);
}
