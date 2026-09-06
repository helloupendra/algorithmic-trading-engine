using System;
using System.Collections.Generic;
using System.Linq;
using AlgoTrading.Domain.MarketData;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Coverage decides whether a backtest is allowed to believe its own data.
/// </summary>
/// <remarks>
/// The rule it replaced was "at least one candle exists in the range", under
/// which a symbol holding a single day out of twenty reported full coverage and
/// was never completed. These pin the day-level rule that replaced it.
/// </remarks>
public class HistoryCoverageTests
{
    private static DateOnly D(string iso) => DateOnly.Parse(iso);

    private static Dictionary<DateOnly, int> Bars(params (string Date, int Count)[] days)
        => days.ToDictionary(d => D(d.Date), d => d.Count);

    // --- expected bars ----------------------------------------------------

    [Theory]
    [InlineData("1", 375)]
    [InlineData("5", 75)]
    [InlineData("15", 25)]
    [InlineData("30", 13)]   // 12.5 rounds up: the last bar is short but real.
    [InlineData("60", 7)]    // 6.25 rounds up, likewise.
    [InlineData("D", 1)]
    public void A_session_divides_into_the_expected_number_of_bars(string resolution, int expected)
        => Assert.Equal(expected, HistoryCoverage.ExpectedBarsPerDay(resolution));

    [Theory]
    [InlineData("")]
    [InlineData("weekly")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("W")]
    public void An_unusable_resolution_yields_no_expectation_rather_than_a_guess(string resolution)
        => Assert.Null(HistoryCoverage.ExpectedBarsPerDay(resolution));

    // --- one day ----------------------------------------------------------

    [Fact]
    public void A_day_with_no_bars_is_missing()
        => Assert.Equal(DayCoverage.Missing, HistoryCoverage.ClassifyDay(0, 75));

    [Fact]
    public void A_full_session_is_complete()
        => Assert.Equal(DayCoverage.Complete, HistoryCoverage.ClassifyDay(75, 75));

    [Fact]
    public void A_third_of_a_session_is_partial_not_complete()
    {
        // The real case that started this: the index held 23 of 75 five-minute
        // bars for one day and the old check called the symbol fully covered.
        Assert.Equal(DayCoverage.Partial, HistoryCoverage.ClassifyDay(23, 75));
    }

    [Fact]
    public void A_slightly_short_session_is_accepted()
    {
        // Shortened sessions and mid-session halts are real; demanding the
        // exact count would refetch those days on every run forever.
        Assert.Equal(DayCoverage.Complete, HistoryCoverage.ClassifyDay(70, 75));
    }

    [Fact]
    public void Without_an_expectation_any_bar_at_all_counts_as_complete()
        => Assert.Equal(DayCoverage.Complete, HistoryCoverage.ClassifyDay(1, null));

    // --- ranges -----------------------------------------------------------

    [Fact]
    public void Weekends_are_not_expected_and_never_reported_missing()
    {
        // 2026-09-05 and 06 are a Saturday and a Sunday.
        var days = HistoryCoverage.ExpectedTradingDays(D("2026-09-04"), D("2026-09-07")).ToList();
        Assert.Equal(new[] { D("2026-09-04"), D("2026-09-07") }, days);
    }

    [Fact]
    public void A_fully_covered_range_reports_no_gaps()
    {
        var bars = Bars(("2026-09-01", 75), ("2026-09-02", 75), ("2026-09-03", 75));
        Assert.True(HistoryCoverage.IsFullyCovered(D("2026-09-01"), D("2026-09-03"), "5", bars));
    }

    [Fact]
    public void A_missing_day_in_the_middle_is_found()
    {
        var bars = Bars(("2026-09-01", 75), ("2026-09-03", 75));
        var gaps = HistoryCoverage.FindGaps(D("2026-09-01"), D("2026-09-03"), "5", bars);

        var gap = Assert.Single(gaps);
        Assert.Equal(D("2026-09-02"), gap.From);
        Assert.Equal(D("2026-09-02"), gap.To);
        Assert.Equal(DayCoverage.Missing, gap.Kind);
    }

    [Fact]
    public void A_partial_day_is_refetched_rather_than_left_half_written()
    {
        var bars = Bars(("2026-09-01", 75), ("2026-09-02", 23));
        var gap = Assert.Single(HistoryCoverage.FindGaps(D("2026-09-01"), D("2026-09-02"), "5", bars));
        Assert.Equal(DayCoverage.Partial, gap.Kind);
        Assert.Equal(D("2026-09-02"), gap.From);
    }

    [Fact]
    public void The_exact_case_that_was_reported_as_full_coverage()
    {
        // Index 5m: complete through 2026-09-02, 23 bars on the 3rd, nothing on
        // the 4th — and the old check answered "fully covered, 0 fetched".
        var bars = Bars(
            ("2026-09-01", 75), ("2026-09-02", 75), ("2026-09-03", 23));

        Assert.False(HistoryCoverage.IsFullyCovered(D("2026-09-01"), D("2026-09-04"), "5", bars));

        var gap = Assert.Single(HistoryCoverage.FindGaps(D("2026-09-01"), D("2026-09-04"), "5", bars));
        Assert.Equal(D("2026-09-03"), gap.From);
        Assert.Equal(D("2026-09-04"), gap.To);
    }

    [Fact]
    public void Consecutive_bad_days_merge_into_one_fetch()
    {
        var bars = Bars(("2026-09-01", 75));
        var gap = Assert.Single(HistoryCoverage.FindGaps(D("2026-09-01"), D("2026-09-04"), "5", bars));
        Assert.Equal(D("2026-09-02"), gap.From);
        Assert.Equal(D("2026-09-04"), gap.To);
    }

    [Fact]
    public void A_weekend_between_bad_days_does_not_split_the_fetch()
    {
        // Friday the 4th and Monday the 7th, with nothing to fetch between.
        var gaps = HistoryCoverage.FindGaps(D("2026-09-04"), D("2026-09-07"), "5",
            new Dictionary<DateOnly, int>());
        var gap = Assert.Single(gaps);
        Assert.Equal(D("2026-09-04"), gap.From);
        Assert.Equal(D("2026-09-07"), gap.To);
    }

    [Fact]
    public void Separate_holes_stay_separate_fetches()
    {
        var bars = Bars(
            ("2026-09-01", 75), ("2026-09-03", 75), ("2026-09-04", 0), ("2026-09-07", 75));
        var gaps = HistoryCoverage.FindGaps(D("2026-09-01"), D("2026-09-07"), "5", bars);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(D("2026-09-02"), gaps[0].From);
        Assert.Equal(D("2026-09-04"), gaps[1].From);
    }

    // --- holidays ---------------------------------------------------------

    [Fact]
    public void A_day_the_broker_has_confirmed_empty_is_not_asked_for_again()
    {
        // A market holiday is indistinguishable from a missing day until it has
        // been requested once. Without remembering the answer, every holiday is
        // re-requested on every run, forever.
        var bars = Bars(("2026-09-01", 75), ("2026-09-03", 75));
        var holidays = new HashSet<DateOnly> { D("2026-09-02") };

        Assert.True(HistoryCoverage.IsFullyCovered(D("2026-09-01"), D("2026-09-03"), "5", bars, holidays));
    }

    [Fact]
    public void A_known_holiday_does_not_glue_two_separate_gaps_together()
    {
        var bars = Bars(("2026-09-02", 75));
        var holidays = new HashSet<DateOnly> { D("2026-09-02") };
        var gaps = HistoryCoverage.FindGaps(D("2026-09-01"), D("2026-09-03"), "5", bars, holidays);

        Assert.Equal(2, gaps.Count);
    }

    // --- sparse instruments (options) -------------------------------------

    [Theory]
    [InlineData("NSE:BANKNIFTY26SEP57000CE", CoveragePolicy.Sparse)]
    [InlineData("NSE:BANKNIFTY26SEP57000PE", CoveragePolicy.Sparse)]
    [InlineData("BSE:SENSEX26SEP81000pe", CoveragePolicy.Sparse)]
    [InlineData("NSE:NIFTYBANK-INDEX", CoveragePolicy.Continuous)]
    [InlineData("NSE:RELIANCE-EQ", CoveragePolicy.Continuous)]
    [InlineData("", CoveragePolicy.Continuous)]
    public void An_option_is_recognised_as_printing_only_when_it_trades(string symbol, CoveragePolicy expected)
        => Assert.Equal(expected, HistoryCoverage.PolicyFor(symbol));

    [Fact]
    public void An_illiquid_strike_that_traded_at_all_counts_as_covered()
    {
        // A deep out-of-the-money strike genuinely goes hours without a trade.
        // Judged as a continuous series it is permanently incomplete, and the
        // backfill re-requests it on every run for data that does not exist.
        Assert.Equal(DayCoverage.Partial, HistoryCoverage.ClassifyDay(5, 75, CoveragePolicy.Continuous));
        Assert.Equal(DayCoverage.Complete, HistoryCoverage.ClassifyDay(5, 75, CoveragePolicy.Sparse));
    }

    [Fact]
    public void A_day_an_option_did_not_trade_at_all_is_still_a_gap()
    {
        // Sparse does not mean unchecked: a whole session absent is worth one
        // request, after which an empty answer is remembered.
        Assert.Equal(DayCoverage.Missing, HistoryCoverage.ClassifyDay(0, 75, CoveragePolicy.Sparse));
    }

    [Fact]
    public void A_thinly_traded_option_reports_no_gaps_across_a_whole_range()
    {
        var bars = Bars(("2026-09-01", 3), ("2026-09-02", 11), ("2026-09-03", 1));
        Assert.False(HistoryCoverage.IsFullyCovered(
            D("2026-09-01"), D("2026-09-03"), "5", bars, null, CoveragePolicy.Continuous));
        Assert.True(HistoryCoverage.IsFullyCovered(
            D("2026-09-01"), D("2026-09-03"), "5", bars, null, CoveragePolicy.Sparse));
    }

    [Fact]
    public void The_index_is_still_held_to_a_full_session()
    {
        // The policy must not have loosened the case it was built for.
        var bars = Bars(("2026-09-03", 23));
        Assert.False(HistoryCoverage.IsFullyCovered(
            D("2026-09-03"), D("2026-09-03"), "5", bars, null, CoveragePolicy.Continuous));
    }

    // --- edges ------------------------------------------------------------

    [Fact]
    public void An_inverted_range_asks_for_nothing()
        => Assert.Empty(HistoryCoverage.FindGaps(D("2026-09-04"), D("2026-09-01"), "5",
            new Dictionary<DateOnly, int>()));

    [Fact]
    public void A_range_of_only_weekend_days_asks_for_nothing()
        => Assert.Empty(HistoryCoverage.FindGaps(D("2026-09-05"), D("2026-09-06"), "5",
            new Dictionary<DateOnly, int>()));

    [Fact]
    public void An_empty_database_reports_the_whole_range_as_one_gap()
    {
        var gap = Assert.Single(HistoryCoverage.FindGaps(D("2026-09-01"), D("2026-09-04"), "5",
            new Dictionary<DateOnly, int>()));
        Assert.Equal(D("2026-09-01"), gap.From);
        Assert.Equal(D("2026-09-04"), gap.To);
        Assert.Equal(DayCoverage.Missing, gap.Kind);
    }
}

/// <summary>
/// "No candles in that window" is an answer, not a failure.
/// </summary>
/// <remarks>
/// A chain backfill sweeps every strike, and most strikes are quiet. Raising
/// the broker's empty reply aborted the sweep at its first untraded contract —
/// exactly the gap the sweep existed to fill.
/// </remarks>
public class FyersNoDataTests
{
    [Fact]
    public void The_reply_the_broker_actually_sends_is_recognised()
    {
        // Observed verbatim: {"candles":[],"message":"","s":"no_data"} — note
        // there is no "code" field, which an earlier version required.
        Assert.True(AlgoTrading.Infrastructure.Providers.Fyers.FyersMarketDataProvider.IsNoData("no_data", 0));
    }

    [Theory]
    [InlineData("no_data", 0)]
    [InlineData("no_data", 200)]
    [InlineData("NO_DATA", 0)]
    [InlineData("nodata", 0)]
    [InlineData("", 200)]
    [InlineData("", 0)]
    public void Empty_windows_are_reported_as_empty(string status, int code)
        => Assert.True(AlgoTrading.Infrastructure.Providers.Fyers.FyersMarketDataProvider.IsNoData(status, code));

    [Theory]
    [InlineData("error", 401)]
    [InlineData("error", 0)]
    [InlineData("ok", 200)]
    public void A_real_failure_is_not_mistaken_for_an_empty_window(string status, int code)
        => Assert.False(AlgoTrading.Infrastructure.Providers.Fyers.FyersMarketDataProvider.IsNoData(status, code));
}
