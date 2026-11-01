using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

namespace AlgoTrading.UnitTests;

/// <summary>Each formula of the fund analyst report on series small enough to check by hand.</summary>
public class MutualFundAnalyticsTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    /// <summary>A NAV every calendar day from <paramref name="from"/> to <paramref name="to"/>, from a function of the day number.</summary>
    private static NavSeries Daily(DateOnly from, DateOnly to, Func<int, double> value) =>
        new(Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => new NavPoint(from.AddDays(i), value(i))));

    /// <summary>A NAV every weekday, growing at a steady yearly rate.</summary>
    private static NavSeries Steady(DateOnly from, DateOnly to, double yearly) =>
        new(Enumerable.Range(0, to.DayNumber - from.DayNumber + 1)
            .Select(i => from.AddDays(i))
            .Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            .Select(d => new NavPoint(d, 10 * Math.Pow(1 + yearly, (d.DayNumber - from.DayNumber) / 365.0))));

    [Fact]
    public void The_series_sorts_keeps_the_last_value_for_a_day_and_drops_non_positive_values()
    {
        var s = new NavSeries([new NavPoint(D(2026, 1, 3), 12), new NavPoint(D(2026, 1, 1), 10), new NavPoint(D(2026, 1, 2), 0),
            new NavPoint(D(2026, 1, 1), 11), new NavPoint(D(2026, 1, 4), double.NaN)]);

        Assert.Equal([D(2026, 1, 1), D(2026, 1, 3)], s.Points.Select(p => p.Date));
        Assert.Equal(11, s[0].Value);
    }

    [Fact]
    public void Lookups_find_a_point_only_within_seven_days()
    {
        var s = new NavSeries([new NavPoint(D(2026, 1, 1), 10), new NavPoint(D(2026, 1, 20), 11)]);

        Assert.Equal(D(2026, 1, 20), s.OnOrAfter(D(2026, 1, 13))!.Value.Date);   // 7 days after: usable
        Assert.Null(s.OnOrAfter(D(2026, 1, 12)));                                // 8 days: a gap
        Assert.Equal(D(2026, 1, 1), s.OnOrBefore(D(2026, 1, 8))!.Value.Date);
        Assert.Null(s.OnOrBefore(D(2026, 1, 9)));
        Assert.Null(s.OnOrAfter(D(2026, 1, 21)));
        Assert.True(s.TryGet(D(2026, 1, 20), out double v) && v == 11);
        Assert.False(s.TryGet(D(2026, 1, 19), out _));
        Assert.Equal(1, s.Until(D(2026, 1, 19)).Count);
        Assert.True(s.Until(D(2025, 12, 31)).IsEmpty);
    }

    [Fact]
    public void Cagr_annualises_over_the_actual_days_on_365()
    {
        Assert.Equal(0.1, FundAnalytics.Cagr(100, D(2024, 1, 1), 121, D(2025, 12, 31)), 12);   // 730 days: two years
        Assert.Equal(Math.Pow(1.1, 365.0 / 366) - 1, FundAnalytics.Cagr(100, D(2024, 1, 1), 110, D(2025, 1, 1)), 12);
        Assert.Throws<ArgumentException>(() => FundAnalytics.Cagr(100, D(2024, 1, 1), 110, D(2024, 1, 1)));
    }

    [Fact]
    public void A_trailing_period_needs_history_from_its_anniversary()
    {
        var s = Steady(D(2023, 3, 15), D(2026, 10, 30), 0.12);

        var oneYear = FundAnalytics.Trailing(s, 1);
        var threeYear = FundAnalytics.Trailing(s, 3);
        var fiveYear = FundAnalytics.Trailing(s, 5);

        Assert.Equal(D(2025, 10, 30), oneYear.Start);
        Assert.Equal(0.12, oneYear.Cagr!.Value, 9);
        Assert.Equal(0.12, threeYear.Cagr!.Value, 9);
        Assert.Null(fiveYear.Cagr);
        Assert.Null(fiveYear.Absolute);
        Assert.Equal("not enough history: the NAVs start on 15 Mar 2023, after 30 Oct 2021", fiveYear.Missing);
    }

    [Fact]
    public void A_period_starts_on_the_first_nav_on_or_after_the_anniversary()
    {
        // 30 Oct 2021 was a Saturday: the 5-year period starts on Monday 1 Nov.
        var s = Steady(D(2021, 10, 1), D(2026, 10, 30), 0.1);
        Assert.Equal(D(2021, 11, 1), FundAnalytics.Trailing(s, 5).Start);
    }

    [Fact]
    public void A_gap_at_the_start_of_a_period_is_not_bridged()
    {
        var points = Steady(D(2020, 1, 1), D(2026, 10, 30), 0.1).Points.Where(p => p.Date < D(2023, 10, 20) || p.Date > D(2023, 11, 10));
        var s = new NavSeries(points);

        var threeYear = FundAnalytics.Trailing(s, 3);
        Assert.Null(threeYear.Cagr);
        Assert.Equal("a gap in the history: no NAV within 7 days after 30 Oct 2023", threeYear.Missing);
    }

    [Fact]
    public void A_leap_day_anniversary_falls_on_28_february()
    {
        var s = Steady(D(2022, 1, 1), D(2024, 2, 29), 0.1);
        Assert.Equal(D(2023, 2, 28), FundAnalytics.Trailing(s, 1).Target);
    }

    [Fact]
    public void Calendar_years_run_from_the_last_nav_of_the_year_before_and_the_current_year_is_year_to_date()
    {
        var s = new NavSeries([
            new NavPoint(D(2023, 6, 1), 9), new NavPoint(D(2023, 12, 29), 10), new NavPoint(D(2024, 12, 31), 12),
            new NavPoint(D(2025, 12, 31), 11), new NavPoint(D(2026, 10, 30), 13.2)]);

        var years = FundAnalytics.CalendarYears(s);

        Assert.Equal([2023, 2024, 2025, 2026], years.Select(y => y.Year));
        Assert.Null(years[0].Return);
        Assert.Equal("the NAVs start on 1 Jun 2023, after the year began", years[0].Missing);
        Assert.Equal(0.2, years[1].Return!.Value, 12);
        Assert.Equal((D(2023, 12, 29), D(2024, 12, 31)), (years[1].Start!.Value, years[1].End!.Value));
        Assert.Equal(11.0 / 12 - 1, years[2].Return!.Value, 12);
        Assert.True(years[3].YearToDate);
        Assert.Equal(0.2, years[3].Return!.Value, 12);
        Assert.False(years[2].YearToDate);
    }

    [Fact]
    public void A_year_whose_last_week_has_no_nav_is_not_a_full_year()
    {
        var s = new NavSeries([new NavPoint(D(2023, 12, 29), 10), new NavPoint(D(2024, 11, 15), 12), new NavPoint(D(2025, 3, 3), 12)]);

        var y2024 = FundAnalytics.CalendarYears(s).Single(y => y.Year == 2024);

        Assert.Null(y2024.Return);
        Assert.Equal("no NAV in the last 7 days of 2024", y2024.Missing);
    }

    [Fact]
    public void Rolling_returns_take_the_trailing_return_on_every_day_and_the_median_of_an_even_count()
    {
        // Four years of daily NAVs: 3-year windows end on each day of the fourth year (and the first day it is possible).
        var from = D(2020, 1, 1);
        var s = Daily(from, D(2024, 1, 4), i => 10 + i * 0.01);

        var rolling = FundAnalytics.Rolling(s, 3, out string? missing);

        Assert.Null(missing);
        Assert.NotNull(rolling);
        Assert.Equal(D(2023, 1, 1), rolling!.FirstEnd);
        Assert.Equal(D(2024, 1, 4), rolling.LastEnd);
        int windows = D(2024, 1, 4).DayNumber - D(2023, 1, 1).DayNumber + 1;
        Assert.Equal(windows, rolling.Windows);
        // A linear NAV: the 3-year CAGR falls as the start rises, so the highest is the first window.
        Assert.Equal(D(2023, 1, 1), rolling.HighestEnd);
        Assert.Equal(0, rolling.NegativeShare);
        Assert.Null(rolling.BenchmarkBeatShare);

        var values = Enumerable.Range(0, s.Count).Select(i => (s[i].Date, Fund: FundAnalytics.Trailing(s.Until(s[i].Date), 3).Cagr))
            .Where(x => x.Fund is not null).Select(x => x.Fund!.Value).OrderBy(x => x).ToList();
        Assert.Equal(windows, values.Count);
        double median = values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
        Assert.Equal(median, rolling.Median, 12);

        // One window more: the count changes parity, and the median is the mean of the two middle values when even.
        var longer = FundAnalytics.Rolling(Daily(from, D(2024, 1, 5), i => 10 + i * 0.01), 3, out _)!;
        var all = new[] { rolling, longer }.Single(r => r.Windows % 2 == 0);
        var evenValues = Enumerable.Range(0, all.Windows).Select(k => D(2023, 1, 1).AddDays(k))
            .Select(e => FundAnalytics.Trailing(Daily(from, e, i => 10 + i * 0.01), 3).Cagr!.Value).OrderBy(x => x).ToList();
        Assert.Equal((evenValues[all.Windows / 2 - 1] + evenValues[all.Windows / 2]) / 2, all.Median, 12);
    }

    [Fact]
    public void Rolling_returns_say_why_there_are_none()
    {
        var s = Steady(D(2025, 1, 1), D(2026, 10, 30), 0.1);
        Assert.Null(FundAnalytics.Rolling(s, 3, out string? missing));
        Assert.Equal("not enough history: a 3-year window needs NAVs from 30 Oct 2023; they start on 1 Jan 2025", missing);
    }

    [Fact]
    public void A_sip_at_a_flat_nav_is_worth_what_was_paid_and_its_xirr_is_zero()
    {
        var s = Daily(D(2025, 10, 1), D(2026, 10, 30), _ => 25);

        var example = FundAnalytics.Example(s, 1);

        Assert.Null(example.Missing);
        Assert.Equal(12, example.Sip!.Instalments);
        Assert.Equal(D(2025, 11, 1), example.Sip.FirstDate);
        Assert.Equal(D(2026, 10, 1), example.Sip.LastDate);
        Assert.Equal(120_000, example.Sip.Invested);
        Assert.Equal(120_000, example.Sip.Value, 6);
        Assert.Equal(0, example.Sip.Xirr!.Value, 9);
        Assert.Equal(200_000, example.LumpSum!.Value, 6);
        Assert.Equal(D(2025, 11, 1), example.LumpSum.Start);
    }

    [Fact]
    public void A_sip_buys_on_the_first_nav_day_of_each_month()
    {
        // Weekdays only: 1 Nov 2025 was a Saturday, so November's instalment is on Monday 3 Nov.
        var s = Steady(D(2025, 10, 1), D(2026, 10, 30), 0.1);
        var example = FundAnalytics.Example(s, 1);
        Assert.Equal(D(2025, 11, 3), example.Sip!.FirstDate);
        Assert.Equal(D(2025, 11, 3), example.LumpSum!.Start);
    }

    [Fact]
    public void A_sip_needs_the_history_to_cover_its_first_month()
    {
        var s = Steady(D(2023, 6, 1), D(2026, 10, 30), 0.1);
        var example = FundAnalytics.Example(s, 5);
        Assert.Null(example.Sip);
        Assert.Null(example.LumpSum);
        Assert.Equal("not enough history: a 5-year SIP starts on 1 Nov 2021; the NAVs start on 1 Jun 2023", example.Missing);
    }

    [Fact]
    public void Xirr_finds_the_yearly_rate()
    {
        Assert.Equal(0.1, FundAnalytics.Xirr([(D(2025, 1, 1), -100), (D(2026, 1, 1), 110)])!.Value, 9);
        Assert.Equal(-0.5, FundAnalytics.Xirr([(D(2025, 1, 1), -100), (D(2026, 1, 1), 50)])!.Value, 9);
        // Two payments: 100 now and 100 in a year, worth 231 in two years: (1+r)^2 + (1+r) = 2.31 → r = 10%.
        Assert.Equal(0.1, FundAnalytics.Xirr([(D(2024, 1, 1), -100), (D(2024, 12, 31), -100), (D(2025, 12, 31), 231)])!.Value, 9);
        Assert.Equal(4.0, FundAnalytics.Xirr([(D(2025, 1, 1), -100), (D(2026, 1, 1), 500)])!.Value, 9);   // past the first bracket
        Assert.Null(FundAnalytics.Xirr([(D(2025, 1, 1), -100), (D(2026, 1, 1), -10)]));
        Assert.Null(FundAnalytics.Xirr([(D(2025, 1, 1), -100)]));
    }

    [Fact]
    public void Volatility_is_the_sample_deviation_of_returns_times_the_root_of_returns_a_year()
    {
        // Alternating +1% and -1% days: every return is ±0.01 around a mean near zero.
        var from = D(2025, 10, 30);
        double nav = 100;
        var points = new List<NavPoint> { new(from, nav) };
        for (int i = 1; i <= 365; i++)
        {
            nav *= i % 2 == 1 ? 1.01 : 0.99;
            points.Add(new NavPoint(from.AddDays(i), nav));
        }

        var s = new NavSeries(points);
        var v = FundAnalytics.Volatility(s, null)!;

        var returns = Enumerable.Range(1, 365).Select(i => i % 2 == 1 ? 0.01 : -0.01).ToArray();
        Assert.Equal(1, v.Years);
        Assert.Equal(365, v.Returns);
        Assert.Equal(365 / (365 / 365.25), v.PerYear, 9);
        Assert.Equal(FundAnalytics.SampleStdev(returns) * Math.Sqrt(v.PerYear), v.Annualised, 12);
        Assert.Null(v.Sharpe);
        Assert.Null(v.Sortino);
    }

    [Fact]
    public void Sharpe_and_sortino_use_the_risk_free_rate_per_return()
    {
        var from = D(2025, 10, 30);
        double nav = 100;
        double[] pattern = [0.02, -0.01, 0.005];
        var returns = new List<double>();
        var points = new List<NavPoint> { new(from, nav) };
        for (int i = 1; i <= 365; i++)
        {
            double r = pattern[i % 3];
            returns.Add(r);
            nav *= 1 + r;
            points.Add(new NavPoint(from.AddDays(i), nav));
        }

        var v = FundAnalytics.Volatility(new NavSeries(points), 0.06)!;

        double perYear = 365 / (365 / 365.25);
        double rfPer = 0.06 / perYear;
        double mean = returns.Average() - rfPer;
        double sd = FundAnalytics.SampleStdev(returns);
        double downside = Math.Sqrt(returns.Sum(r => Math.Pow(Math.Min(0, r - rfPer), 2)) / returns.Count);
        Assert.Equal(mean / sd * Math.Sqrt(perYear), v.Sharpe!.Value, 9);
        Assert.Equal(mean / downside * Math.Sqrt(perYear), v.Sortino!.Value, 9);
    }

    [Fact]
    public void Volatility_uses_three_years_when_covered_and_one_year_otherwise()
    {
        Assert.Equal(3, FundAnalytics.Volatility(Steady(D(2023, 1, 1), D(2026, 10, 30), 0.1), null)!.Years);
        Assert.Equal(1, FundAnalytics.Volatility(Steady(D(2024, 1, 1), D(2026, 10, 30), 0.1), null)!.Years);
        Assert.Null(FundAnalytics.Volatility(Steady(D(2026, 1, 1), D(2026, 10, 30), 0.1), null));
    }

    [Fact]
    public void The_drawdown_is_the_deepest_fall_from_a_running_peak_with_its_recovery()
    {
        var s = new NavSeries([
            new NavPoint(D(2026, 1, 1), 100), new NavPoint(D(2026, 1, 2), 120), new NavPoint(D(2026, 1, 5), 90),
            new NavPoint(D(2026, 1, 6), 120), new NavPoint(D(2026, 1, 7), 84), new NavPoint(D(2026, 1, 8), 110),
            new NavPoint(D(2026, 1, 9), 121), new NavPoint(D(2026, 1, 12), 100)]);

        var dd = FundAnalytics.Drawdown(s, 0, null)!;

        Assert.Equal(-0.3, dd.MaxDrawdown, 12);
        Assert.Equal(D(2026, 1, 2), dd.Peak);              // 120 first reached on 2 Jan; 6 Jan only equals it
        Assert.Equal(D(2026, 1, 7), dd.Trough);
        Assert.Equal(D(2026, 1, 9), dd.Recovery);
        Assert.Equal((121.0, D(2026, 1, 9)), (FundAnalytics.Current(s)!.PeakValue, FundAnalytics.Current(s)!.PeakDate));
        Assert.Equal(100 / 121.0 - 1, FundAnalytics.Current(s)!.Value, 12);
    }

    [Fact]
    public void A_fall_not_yet_made_good_has_no_recovery_date()
    {
        var s = new NavSeries([new NavPoint(D(2026, 1, 1), 100), new NavPoint(D(2026, 1, 2), 80), new NavPoint(D(2026, 1, 5), 99.99)]);
        var dd = FundAnalytics.Drawdown(s, 0, null)!;
        Assert.Equal(-0.2, dd.MaxDrawdown, 12);
        Assert.Null(dd.Recovery);
    }

    [Fact]
    public void Drawdown_windows_are_listed_only_when_the_history_covers_them()
    {
        var s = Steady(D(2022, 1, 1), D(2026, 10, 30), 0.1);
        Assert.Equal([3, (int?)null], FundAnalytics.Drawdowns(s).Select(d => d.Years));
    }

    [Fact]
    public void A_fund_that_is_its_benchmark_has_beta_one_no_tracking_error_and_full_capture()
    {
        var from = D(2022, 10, 3);
        var s = Daily(from, D(2026, 10, 30), i => 100 * Math.Exp(0.0003 * i + 0.02 * Math.Sin(i / 7.0)));

        var b = FundAnalytics.AgainstBenchmark(s, s, indexFund: true);

        Assert.Equal(3, b.Beta!.Years);
        Assert.Equal(1, b.Beta.Beta, 12);
        Assert.Equal(0, b.TrackingError!.Value, 15);
        Assert.Equal(100, b.Capture!.Downside!.Value, 9);
        Assert.Equal(100, b.Capture.Upside!.Value, 9);
        Assert.Equal(36, b.Capture.DownMonths + b.Capture.UpMonths);
        Assert.All(b.Periods.Where(p => p.Years <= 3), p => Assert.Equal(0, p.Excess!.Value, 12));
        Assert.Equal(0, b.Rolling!.BenchmarkBeatShare);    // equal is not beating
        Assert.Empty(b.Missing);
    }

    [Fact]
    public void Beta_is_the_covariance_over_the_benchmark_variance()
    {
        // The fund moves twice the benchmark's daily return, every day.
        var from = D(2025, 10, 1);
        var bench = Daily(from, D(2026, 10, 30), i => 100 * Math.Exp(0.01 * Math.Sin(i / 3.0)));
        var fundPoints = new List<NavPoint> { new(bench[0].Date, 50) };
        for (int i = 1; i < bench.Count; i++)
            fundPoints.Add(new NavPoint(bench[i].Date, fundPoints[^1].Value * (1 + 2 * (bench[i].Value / bench[i - 1].Value - 1))));

        var b = FundAnalytics.AgainstBenchmark(new NavSeries(fundPoints), bench, indexFund: false);

        Assert.Equal(1, b.Beta!.Years);
        Assert.Equal(2, b.Beta.Beta, 9);
        Assert.Null(b.TrackingError);    // not an index fund
        Assert.Null(b.Capture);          // fewer than 3 years
        Assert.Contains(b.Missing, m => m.Contains("capture", StringComparison.Ordinal));
    }

    [Fact]
    public void Excess_return_compares_the_same_dates_and_needs_the_benchmark_on_the_as_of_day()
    {
        var fund = Steady(D(2024, 1, 1), D(2026, 10, 30), 0.12);
        var bench = Steady(D(2024, 1, 1), D(2026, 10, 30), 0.10);

        var one = FundAnalytics.AgainstBenchmark(fund, bench, false).Periods.Single(p => p.Years == 1);
        Assert.Equal(0.02, one.Excess!.Value, 9);

        var lagging = FundAnalytics.AgainstBenchmark(fund, bench.Until(D(2026, 10, 29)), false).Periods.Single(p => p.Years == 1);
        Assert.Null(lagging.Excess);
        Assert.Equal(0.12, lagging.FundCagr!.Value, 9);
        Assert.Equal("the benchmark has no value for 30 Oct 2026 yet", lagging.Missing);
    }

    [Fact]
    public void Only_dates_both_series_hold_count_for_beta()
    {
        // The benchmark has no value on 31 March (a fund NAV day that is not a trading day).
        var fund = Steady(D(2025, 1, 1), D(2026, 10, 30), 0.1);
        var fundWithSunday = new NavSeries(fund.Points.Append(new NavPoint(D(2026, 3, 29), fund.OnOrBefore(D(2026, 3, 29))!.Value.Value)));
        var b = FundAnalytics.AgainstBenchmark(fundWithSunday, fund, false);
        Assert.Equal(FundAnalytics.Volatility(fund, null)!.Returns, b.Beta!.Returns);
    }

    [Fact]
    public void Analyse_cuts_both_series_at_the_as_of_day()
    {
        var s = Steady(D(2020, 1, 1), D(2026, 10, 30), 0.1);
        var a = FundAnalytics.Analyse(s, D(2026, 6, 30), s, indexFund: true)!;
        Assert.Equal(D(2026, 6, 30), a.AsOf);
        Assert.Equal(D(2026, 6, 30), a.Benchmark!.LastCommonDate);
        Assert.Null(FundAnalytics.Analyse(s, D(2019, 1, 1)));
    }
}
