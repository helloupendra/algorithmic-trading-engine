using System.Globalization;

namespace AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

/// <summary>
/// The fund analyst report's arithmetic, from a NAV series (and a benchmark's total-return index when there is one):
/// trailing and calendar-year returns, rolling returns, a ₹10,000 monthly SIP and a ₹2,00,000 lump sum, volatility,
/// drawdowns, and the comparisons with a benchmark. Pure functions: no database, no clock.
/// </summary>
/// <remarks>
/// <para>
/// The definitions are written out in docs/modules/mutual_funds_module.md ("How each figure is computed"), and an
/// independent Python implementation of the same text checks this one (tests/AlgoTrading.UnitTests/Fixtures/MutualFunds/Analytics/reference.py).
/// The rules that matter most:
/// </para>
/// <list type="bullet">
/// <item>A period needs history that really covers it: an N-year figure needs the series to start on or before the
/// day N years before the as-of day. Otherwise there is no number, only the reason.</item>
/// <item>A period starts on the first NAV on or after its anniversary (a SIP instalment on the first NAV on or after
/// the 1st of the month), and only within 7 days of it; a longer gap is a hole in the history, not a start.</item>
/// <item>Annualised as (end / start) ^ (365 / days) - 1, over the actual days between the two NAVs.</item>
/// </list>
/// </remarks>
public static class FundAnalytics
{
    /// <summary>The trailing periods, SIP and lump-sum periods of the report, in years.</summary>
    public static readonly IReadOnlyList<int> PeriodYears = [1, 3, 5, 10];

    /// <summary>The drawdown windows besides the whole series, in years.</summary>
    public static readonly IReadOnlyList<int> DrawdownYears = [3, 5, 10];

    public const int RollingYears = 3;
    public const double SipInstalment = 10_000;
    public const double LumpSumAmount = 200_000;

    /// <summary>Months of returns in the capture ratios, and the fewest down (or up) months a ratio needs.</summary>
    public const int CaptureMonths = 36;

    public const int CaptureMinimumMonths = 3;

    /// <summary>
    /// Analyses <paramref name="fund"/> as of <paramref name="asOf"/> (its last NAV on or before that day; the last
    /// NAV when null). <paramref name="benchmark"/> adds the comparisons; <paramref name="indexFund"/> adds the
    /// tracking error; <paramref name="riskFreeRate"/> (a yearly fraction) adds Sharpe and Sortino. Null when the
    /// series has no NAV on or before the day.
    /// </summary>
    public static FundAnalysis? Analyse(NavSeries fund, DateOnly? asOf = null, NavSeries? benchmark = null, bool indexFund = false, double? riskFreeRate = null)
    {
        var series = asOf is { } day ? fund.Until(day) : fund;
        if (series.IsEmpty) return null;

        var rolling = Rolling(series, RollingYears, out string? rollingMissing);
        return new FundAnalysis(
            series.Last,
            series.First,
            series[series.Count - 1].Value,
            series.Count,
            PeriodYears.Select(n => Trailing(series, n)).ToList(),
            CalendarYears(series),
            rolling,
            rollingMissing,
            PeriodYears.Select(n => Example(series, n)).ToList(),
            Volatility(series, riskFreeRate),
            Drawdowns(series),
            Current(series),
            benchmark is null ? null : AgainstBenchmark(series, benchmark.Until(series.Last), indexFund));
    }

    /// <summary>(end / start) ^ (365 / days) - 1.</summary>
    public static double Cagr(double startValue, DateOnly start, double endValue, DateOnly end)
    {
        int days = FundDates.Days(start, end);
        if (days <= 0) throw new ArgumentException("A period must end after it starts.");
        return Math.Pow(endValue / startValue, 365.0 / days) - 1;
    }

    /// <summary>The trailing return over <paramref name="years"/> years to the series' last day.</summary>
    public static PeriodReturn Trailing(NavSeries series, int years)
    {
        var end = series[series.Count - 1];
        var target = FundDates.YearsBefore(end.Date, years);
        if (TrailingAt(series, series.Count - 1, years) is { } found)
        {
            var start = series[found.StartIndex];
            return new PeriodReturn(years, target, end.Date, start.Date, found.Cagr, end.Value / start.Value - 1, null);
        }

        return new PeriodReturn(years, target, end.Date, null, null, null, CoverageMissing(series, target));
    }

    /// <summary>Why a period that must start on <paramref name="target"/> has no figure.</summary>
    private static string CoverageMissing(NavSeries series, DateOnly target) =>
        series.First > target
            ? $"not enough history: the NAVs start on {Day(series.First)}, after {Day(target)}"
            : $"a gap in the history: no NAV within {NavSeries.ToleranceDays} days after {Day(target)}";

    /// <summary>The trailing return as it stood on the point at <paramref name="endIndex"/>: its start point and CAGR.</summary>
    private static (int StartIndex, double Cagr)? TrailingAt(NavSeries series, int endIndex, int years)
    {
        var end = series[endIndex];
        var target = FundDates.YearsBefore(end.Date, years);
        if (series.First > target) return null;
        int start = series.IndexOnOrAfter(target);
        if (start < 0 || start >= endIndex || series[start].Date.DayNumber - target.DayNumber > NavSeries.ToleranceDays) return null;
        return (start, Cagr(series[start].Value, series[start].Date, end.Value, end.Date));
    }

    /// <summary>Each calendar year from the series' first to its last, with the return or why there is none.</summary>
    public static IReadOnlyList<CalendarYearReturn> CalendarYears(NavSeries series)
    {
        var result = new List<CalendarYearReturn>();
        var asOf = series.Last;
        for (int year = series.First.Year; year <= asOf.Year; year++)
        {
            var priorEnd = new DateOnly(year - 1, 12, 31);
            bool ytd = year == asOf.Year;
            if (series.OnOrBefore(priorEnd) is not { } start)
            {
                string why = series.First > priorEnd
                    ? $"the NAVs start on {Day(series.First)}, after the year began"
                    : $"no NAV in the last {NavSeries.ToleranceDays} days of {year - 1}";
                result.Add(new CalendarYearReturn(year, null, null, null, ytd, why));
                continue;
            }

            NavPoint? end = ytd ? series[series.Count - 1] : series.OnOrBefore(new DateOnly(year, 12, 31));
            if (end is not { } last)
            {
                result.Add(new CalendarYearReturn(year, start.Date, null, null, false, $"no NAV in the last {NavSeries.ToleranceDays} days of {year}"));
                continue;
            }

            result.Add(new CalendarYearReturn(year, start.Date, last.Date, last.Value / start.Value - 1, ytd, null));
        }

        return result;
    }

    /// <summary>
    /// The trailing <paramref name="years"/>-year return on every day it exists: lowest, highest, median, how
    /// often below zero. Null (with the reason) when not one window exists.
    /// </summary>
    public static RollingReturns? Rolling(NavSeries series, int years, out string? missing, NavSeries? benchmark = null)
    {
        var values = new List<(DateOnly End, double Cagr)>();
        int compared = 0, beat = 0;
        for (int i = 0; i < series.Count; i++)
        {
            if (TrailingAt(series, i, years) is not { } found) continue;
            var end = series[i];
            values.Add((end.Date, found.Cagr));

            if (benchmark is null || !benchmark.TryGet(end.Date, out double benchEnd)) continue;
            var start = series[found.StartIndex];
            if (benchmark.OnOrBefore(start.Date) is not { } benchStart) continue;
            compared++;
            if (found.Cagr > Cagr(benchStart.Value, start.Date, benchEnd, end.Date)) beat++;
        }

        if (values.Count == 0)
        {
            var target = FundDates.YearsBefore(series.Last, years);
            missing = series.First > target
                ? $"not enough history: a {years}-year window needs NAVs from {Day(target)}; they start on {Day(series.First)}"
                : $"no {years}-year window without a gap in the history";
            return null;
        }

        missing = null;
        var lowest = values[0];
        var highest = values[0];
        foreach (var v in values)
        {
            if (v.Cagr < lowest.Cagr) lowest = v;
            if (v.Cagr > highest.Cagr) highest = v;
        }

        var sorted = values.Select(v => v.Cagr).OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        double median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;

        return new RollingReturns(years, values.Count, values[0].End, values[^1].End, lowest.Cagr, lowest.End, highest.Cagr, highest.End,
            median, (double)values.Count(v => v.Cagr < 0) / values.Count,
            benchmark is null ? null : compared,
            benchmark is null || compared == 0 ? null : (double)beat / compared);
    }

    /// <summary>
    /// A ₹10,000 SIP on the first NAV day of each month for <paramref name="years"/> years, ending in the as-of
    /// month, and a ₹2,00,000 lump sum on the SIP's first day, both valued at the last NAV.
    /// </summary>
    public static InvestmentExample Example(NavSeries series, int years, double instalment = SipInstalment, double lumpSum = LumpSumAmount)
    {
        var end = series[series.Count - 1];
        int months = years * 12;
        var firstMonth = FundDates.MonthStart(end.Date, months - 1);
        if (series.First > firstMonth)
        {
            return new InvestmentExample(years, null, null,
                $"not enough history: a {years}-year SIP starts on {Day(firstMonth)}; the NAVs start on {Day(series.First)}");
        }

        var dates = new List<NavPoint>(months);
        for (int k = months - 1; k >= 0; k--)
        {
            var monthStart = FundDates.MonthStart(end.Date, k);
            if (series.OnOrAfter(monthStart) is not { } point)
                return new InvestmentExample(years, null, null, $"a gap in the history: no NAV within {NavSeries.ToleranceDays} days after {Day(monthStart)}");
            dates.Add(point);
        }

        double units = dates.Sum(p => instalment / p.Value);
        double invested = instalment * months;
        double value = units * end.Value;
        var flows = dates.Select(p => (p.Date, -instalment)).Append((end.Date, value)).ToList();
        var sip = new SipResult(years, instalment, months, dates[0].Date, dates[^1].Date, end.Date, invested, units, value, value - invested, Xirr(flows));

        var start = dates[0];
        double lumpValue = lumpSum / start.Value * end.Value;
        LumpSumResult? lump = start.Date < end.Date
            ? new LumpSumResult(years, lumpSum, start.Date, start.Value, end.Date, end.Value, lumpValue, lumpValue - lumpSum, Cagr(lumpSum, start.Date, lumpValue, end.Date))
            : null;

        return new InvestmentExample(years, sip, lump, null);
    }

    /// <summary>
    /// The yearly rate at which the cash flows' present value is zero, with time in days / 365 from the first flow;
    /// null when there is no sign change. Bisection: the flows here are payments then one value, so there is one root.
    /// </summary>
    public static double? Xirr(IReadOnlyList<(DateOnly Date, double Amount)> flows)
    {
        if (flows.Count < 2 || !flows.Any(f => f.Amount < 0) || !flows.Any(f => f.Amount > 0)) return null;
        var first = flows.Min(f => f.Date);
        var times = flows.Select(f => FundDates.Days(first, f.Date) / 365.0).ToArray();

        double Npv(double rate)
        {
            double sum = 0;
            for (int i = 0; i < flows.Count; i++) sum += flows[i].Amount / Math.Pow(1 + rate, times[i]);
            return sum;
        }

        double lo = -0.9999, hi = 1.0;
        double npvLo = Npv(lo), npvHi = Npv(hi);
        for (int widen = 0; Math.Sign(npvLo) == Math.Sign(npvHi) && widen < 20; widen++)
        {
            hi = hi * 2 + 1;
            npvHi = Npv(hi);
        }

        if (Math.Sign(npvLo) == Math.Sign(npvHi)) return null;
        for (int i = 0; i < 300 && hi - lo > 1e-14; i++)
        {
            double mid = (lo + hi) / 2;
            double npvMid = Npv(mid);
            if (npvMid == 0) return mid;
            if (Math.Sign(npvMid) == Math.Sign(npvLo))
            {
                lo = mid;
                npvLo = npvMid;
            }
            else
            {
                hi = mid;
            }
        }

        return (lo + hi) / 2;
    }

    /// <summary>
    /// Annualised volatility of daily returns over the last 3 years (1 year when the history is shorter), with
    /// Sharpe and Sortino when <paramref name="riskFreeRate"/> is given.
    /// </summary>
    public static VolatilityResult? Volatility(NavSeries series, double? riskFreeRate)
    {
        var end = series.Last;
        foreach (int years in new[] { 3, 1 })
        {
            var target = FundDates.YearsBefore(end, years);
            if (series.First > target) continue;
            if (series.OnOrAfter(target) is not { } start) return null;

            int from = series.IndexOnOrAfter(start.Date);
            var returns = DailyReturns(series, from, series.Count - 1);
            if (returns.Length < 2) return null;

            double perYear = returns.Length / (FundDates.Days(start.Date, end) / 365.25);
            double sd = SampleStdev(returns);
            double annual = sd * Math.Sqrt(perYear);
            double? sharpe = null, sortino = null;
            if (riskFreeRate is { } rf && sd > 0)
            {
                double rfPer = rf / perYear;
                double meanExcess = returns.Average() - rfPer;
                sharpe = meanExcess / sd * Math.Sqrt(perYear);
                double downside = Math.Sqrt(returns.Sum(r => Math.Pow(Math.Min(0, r - rfPer), 2)) / returns.Length);
                sortino = downside > 0 ? meanExcess / downside * Math.Sqrt(perYear) : null;
            }

            return new VolatilityResult(years, start.Date, end, returns.Length, perYear, annual, sharpe, sortino);
        }

        return null;
    }

    /// <summary>The deepest fall over the last 3, 5 and 10 years (when covered) and over the whole series.</summary>
    public static IReadOnlyList<DrawdownResult> Drawdowns(NavSeries series)
    {
        var result = new List<DrawdownResult>();
        foreach (int years in DrawdownYears)
        {
            var target = FundDates.YearsBefore(series.Last, years);
            if (series.First > target || series.OnOrAfter(target) is not { } start) continue;
            if (Drawdown(series, series.IndexOnOrAfter(start.Date), years) is { } dd) result.Add(dd);
        }

        if (Drawdown(series, 0, null) is { } all) result.Add(all);
        return result;
    }

    /// <summary>The deepest fall from a running peak from the point at <paramref name="from"/> to the end.</summary>
    public static DrawdownResult? Drawdown(NavSeries series, int from, int? years)
    {
        if (from < 0 || from >= series.Count - 1) return null;
        var peak = series[from];
        var worstPeak = peak;
        var trough = peak;
        double worst = 0;
        for (int i = from; i < series.Count; i++)
        {
            var p = series[i];
            if (p.Value > peak.Value) peak = p;
            double dd = p.Value / peak.Value - 1;
            if (dd < worst)
            {
                worst = dd;
                worstPeak = peak;
                trough = p;
            }
        }

        DateOnly? recovery = null;
        if (worst < 0)
        {
            for (int i = series.IndexOnOrAfter(trough.Date) + 1; i < series.Count; i++)
            {
                if (series[i].Value >= worstPeak.Value)
                {
                    recovery = series[i].Date;
                    break;
                }
            }
        }

        return new DrawdownResult(years, series[from].Date, series.Last, worst, worstPeak.Date, worstPeak.Value, trough.Date, trough.Value, recovery);
    }

    /// <summary>The last value against the highest value in the series (the first day it was reached).</summary>
    public static CurrentDrawdown? Current(NavSeries series)
    {
        if (series.IsEmpty) return null;
        var high = series[0];
        foreach (var p in series.Points)
        {
            if (p.Value > high.Value) high = p;
        }

        return new CurrentDrawdown(series[series.Count - 1].Value / high.Value - 1, high.Date, high.Value);
    }

    /// <summary>
    /// The fund against a benchmark total-return index: each period's return beside the index's over the same
    /// dates, the rolling windows it beat, beta, the capture ratios, and for an index fund the tracking error.
    /// </summary>
    public static BenchmarkAnalysis AgainstBenchmark(NavSeries fund, NavSeries benchmark, bool indexFund)
    {
        var missing = new List<string>();
        var end = fund[fund.Count - 1];

        var periods = new List<BenchmarkPeriod>();
        foreach (int years in PeriodYears)
        {
            var own = Trailing(fund, years);
            if (own is not { Start: { } start, Cagr: { } cagr })
            {
                periods.Add(new BenchmarkPeriod(years, null, end.Date, null, null, null, own.Missing));
                continue;
            }

            if (!benchmark.TryGet(end.Date, out double benchEnd))
            {
                periods.Add(new BenchmarkPeriod(years, start, end.Date, cagr, null, null, $"the benchmark has no value for {Day(end.Date)} yet"));
                continue;
            }

            if (benchmark.OnOrBefore(start) is not { } benchStart)
            {
                periods.Add(new BenchmarkPeriod(years, start, end.Date, cagr, null, null, $"the benchmark has no value within {NavSeries.ToleranceDays} days before {Day(start)}"));
                continue;
            }

            double benchCagr = Cagr(benchStart.Value, start, benchEnd, end.Date);
            periods.Add(new BenchmarkPeriod(years, start, end.Date, cagr, benchCagr, cagr - benchCagr, null));
        }

        var rolling = Rolling(fund, RollingYears, out _, benchmark);
        var (f, b) = fund.Common(benchmark);
        if (f.Count < 3)
        {
            missing.Add("the fund and the benchmark have too few dates in common");
            return new BenchmarkAnalysis(f.IsEmpty ? null : f.Last, periods, rolling, null, null, null, missing);
        }

        var lastCommon = f.Last;
        bool Covers(int years) => fund.First <= FundDates.YearsBefore(lastCommon, years) && benchmark.First <= FundDates.YearsBefore(lastCommon, years);

        BetaResult? beta = null;
        int betaYears = Covers(3) ? 3 : Covers(1) ? 1 : 0;
        if (betaYears > 0 && f.OnOrAfter(FundDates.YearsBefore(lastCommon, betaYears)) is { } betaStart)
        {
            int from = f.IndexOnOrAfter(betaStart.Date);
            var fr = DailyReturns(f, from, f.Count - 1);
            var br = DailyReturns(b, from, b.Count - 1);
            double var = SampleVariance(br);
            if (fr.Length >= 2 && var > 0) beta = new BetaResult(betaYears, betaStart.Date, lastCommon, fr.Length, SampleCovariance(fr, br) / var);
        }

        if (beta is null) missing.Add("beta needs at least a year of NAVs and benchmark values on the same dates");

        TrackingErrorResult? trackingError = null;
        if (indexFund)
        {
            if (Covers(1) && f.OnOrAfter(FundDates.YearsBefore(lastCommon, 1)) is { } teStart)
            {
                int from = f.IndexOnOrAfter(teStart.Date);
                var fr = DailyReturns(f, from, f.Count - 1);
                var br = DailyReturns(b, from, b.Count - 1);
                if (fr.Length >= 2)
                {
                    var diff = fr.Zip(br, (x, y) => x - y).ToArray();
                    double perYear = diff.Length / (FundDates.Days(teStart.Date, lastCommon) / 365.25);
                    trackingError = new TrackingErrorResult(teStart.Date, lastCommon, diff.Length, SampleStdev(diff) * Math.Sqrt(perYear));
                }
            }

            if (trackingError is null) missing.Add("the tracking error needs a year of NAVs and index values on the same dates");
        }

        var capture = Capture(fund, benchmark, f, b);
        if (capture is null) missing.Add("the capture ratios need 3 years of NAVs and benchmark values on the same dates");

        return new BenchmarkAnalysis(lastCommon, periods, rolling, beta, trackingError, capture, missing);
    }

    /// <summary>
    /// Upside and downside capture over the 36 monthly returns ending on the last common date (each month from the
    /// first common date on or after the same day a month earlier).
    /// </summary>
    private static CaptureResult? Capture(NavSeries fund, NavSeries benchmark, NavSeries f, NavSeries b)
    {
        var lastCommon = f.Last;
        var target = FundDates.YearsBefore(lastCommon, CaptureMonths / 12);
        if (fund.First > target || benchmark.First > target) return null;

        var points = new List<int>(CaptureMonths + 1);
        for (int k = 0; k <= CaptureMonths; k++)
        {
            var anchor = FundDates.MonthsBefore(lastCommon, k);
            if (f.OnOrAfter(anchor) is not { } point) return null;
            points.Add(f.IndexOnOrAfter(point.Date));
        }

        var down = new List<(double F, double B)>();
        var up = new List<(double F, double B)>();
        for (int k = 0; k < CaptureMonths; k++)
        {
            int newer = points[k], older = points[k + 1];
            double fm = f[newer].Value / f[older].Value - 1;
            double bm = b[newer].Value / b[older].Value - 1;
            if (bm < 0) down.Add((fm, bm));
            else if (bm > 0) up.Add((fm, bm));
        }

        return new CaptureResult(f[points[^1]].Date, lastCommon, CaptureMonths, down.Count, up.Count, Ratio(down), Ratio(up));

        static double? Ratio(List<(double F, double B)> months)
        {
            if (months.Count < CaptureMinimumMonths) return null;
            double fg = Math.Pow(months.Aggregate(1.0, (acc, m) => acc * (1 + m.F)), 1.0 / months.Count) - 1;
            double bg = Math.Pow(months.Aggregate(1.0, (acc, m) => acc * (1 + m.B)), 1.0 / months.Count) - 1;
            return bg == 0 ? null : fg / bg * 100;
        }
    }

    /// <summary>Returns between consecutive points from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static double[] DailyReturns(NavSeries series, int from, int to)
    {
        if (to <= from) return [];
        var result = new double[to - from];
        for (int i = from + 1; i <= to; i++) result[i - from - 1] = series[i].Value / series[i - 1].Value - 1;
        return result;
    }

    public static double SampleVariance(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        return values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
    }

    public static double SampleStdev(IReadOnlyList<double> values) => Math.Sqrt(SampleVariance(values));

    public static double SampleCovariance(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count) throw new ArgumentException("Both series need the same length.");
        if (x.Count < 2) return 0;
        double mx = x.Average(), my = y.Average();
        double sum = 0;
        for (int i = 0; i < x.Count; i++) sum += (x[i] - mx) * (y[i] - my);
        return sum / (x.Count - 1);
    }

    /// <summary>"3 Jan 2018": how the reasons write a date.</summary>
    public static string Day(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
}
