using System.Globalization;
using System.Text.Json;
using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The analytics against real NAVs and total-return indices: six schemes from mfapi.in's copy of AMFI's history and
/// four NIFTY TRIs from niftyindices.com, 1 Sep 2016 to 30 Oct 2026 (Fixtures/MutualFunds/Analytics). The expected
/// numbers come from reference.py in the same folder, an independent Python implementation of the definitions in
/// docs/modules/mutual_funds_module.md, written without reading this code.
/// </summary>
public class MutualFundAnalyticsReferenceTests
{
    private const double RiskFree = 0.0573;
    private static readonly DateOnly AsOf = new(2026, 10, 30);

    private static readonly Dictionary<string, string> TriFiles = new()
    {
        ["NIFTY 50"] = "tri_nifty50.csv",
        ["NIFTY 100"] = "tri_nifty100.csv",
        ["NIFTY SMALLCAP 250"] = "tri_niftysmallcap250.csv",
        ["NIFTY NEXT 50"] = "tri_niftynext50.csv",
    };

    internal static string Folder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "MutualFunds", "Analytics")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "tests", "AlgoTrading.UnitTests", "Fixtures", "MutualFunds", "Analytics");
    }

    /// <summary>A fixture CSV (date,value), oldest first.</summary>
    internal static NavSeries Series(string file) =>
        new(File.ReadLines(Path.Combine(Folder(), file)).Skip(1).Where(l => l.Length > 0).Select(line =>
        {
            var parts = line.Split(',');
            return new NavPoint(DateOnly.ParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture));
        }));

    private static JsonElement Expected() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder(), "expected.json"))).RootElement;

    private static FundAnalysis Analyse(string code, JsonElement expected)
    {
        string? benchmark = expected.GetProperty("benchmark").ValueKind == JsonValueKind.Null ? null : expected.GetProperty("benchmark").GetString();
        var analysis = FundAnalytics.Analyse(Series($"nav_{code}.csv"), AsOf,
            benchmark is null ? null : Series(TriFiles[benchmark]), expected.GetProperty("indexFund").GetBoolean(), RiskFree);
        Assert.NotNull(analysis);
        return analysis!;
    }

    public static TheoryData<string> Codes => new() { "120716", "100822", "118825", "118778", "119091", "143341" };

    private static void Near(double expected, double? actual, string what)
    {
        Assert.True(actual is not null, $"{what}: expected {expected}, got null");
        double tolerance = Math.Max(1e-9, Math.Abs(expected) * 1e-9);
        Assert.True(Math.Abs(expected - actual!.Value) <= tolerance, $"{what}: expected {expected:R}, got {actual:R}");
    }

    private static DateOnly Date(JsonElement e) => DateOnly.ParseExact(e.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? DateOrNull(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : Date(e);

    [Theory]
    [MemberData(nameof(Codes))]
    public void Trailing_and_calendar_year_returns_match_the_reference(string code)
    {
        var expected = Expected().GetProperty("funds").GetProperty(code);
        var a = Analyse(code, expected);

        Assert.Equal(Date(expected.GetProperty("asOf")), a.AsOf);
        Assert.Equal(Date(expected.GetProperty("firstDate")), a.FirstDate);
        foreach (var period in a.Trailing)
        {
            var e = expected.GetProperty("trailing").GetProperty(period.Years.ToString(CultureInfo.InvariantCulture));
            if (e.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(period.Cagr);
                Assert.NotNull(period.Missing);
                continue;
            }

            Assert.Equal(Date(e.GetProperty("start")), period.Start);
            Near(e.GetProperty("cagr").GetDouble(), period.Cagr, $"{code} {period.Years}y CAGR");
            Near(e.GetProperty("absolute").GetDouble(), period.Absolute, $"{code} {period.Years}y absolute");
        }

        var years = a.CalendarYears.Where(y => y.Return is not null).ToList();
        var expectedYears = expected.GetProperty("calendarYears").EnumerateArray().ToList();
        Assert.Equal(expectedYears.Count, years.Count);
        foreach (var (e, y) in expectedYears.Zip(years))
        {
            Assert.Equal(e.GetProperty("year").GetInt32(), y.Year);
            Assert.Equal(Date(e.GetProperty("start")), y.Start);
            Assert.Equal(Date(e.GetProperty("end")), y.End);
            Assert.Equal(e.GetProperty("ytd").GetBoolean(), y.YearToDate);
            Near(e.GetProperty("return").GetDouble(), y.Return, $"{code} {y.Year}");
        }
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Rolling_returns_match_the_reference(string code)
    {
        var expected = Expected().GetProperty("funds").GetProperty(code);
        var a = Analyse(code, expected);
        var e = expected.GetProperty("rolling3y");
        var rolling = a.Benchmark?.Rolling ?? a.Rolling;
        Assert.NotNull(rolling);

        Assert.Equal(e.GetProperty("windows").GetInt32(), rolling!.Windows);
        Assert.Equal(Date(e.GetProperty("firstEnd")), rolling.FirstEnd);
        Assert.Equal(Date(e.GetProperty("lastEnd")), rolling.LastEnd);
        Near(e.GetProperty("min").GetDouble(), rolling.Lowest, $"{code} rolling lowest");
        Assert.Equal(Date(e.GetProperty("minEnd")), rolling.LowestEnd);
        Near(e.GetProperty("max").GetDouble(), rolling.Highest, $"{code} rolling highest");
        Assert.Equal(Date(e.GetProperty("maxEnd")), rolling.HighestEnd);
        Near(e.GetProperty("median").GetDouble(), rolling.Median, $"{code} rolling median");
        Near(e.GetProperty("negativeShare").GetDouble(), rolling.NegativeShare, $"{code} share below zero");
        if (e.GetProperty("benchmarkCompared").ValueKind == JsonValueKind.Null)
        {
            Assert.Null(rolling.BenchmarkCompared);
        }
        else
        {
            Assert.Equal(e.GetProperty("benchmarkCompared").GetInt32(), rolling.BenchmarkCompared);
            Near(e.GetProperty("benchmarkBeatShare").GetDouble(), rolling.BenchmarkBeatShare, $"{code} share beating the benchmark");
        }

        // The rolling figures without a benchmark are the same windows.
        Assert.Equal(rolling.Median, a.Rolling!.Median);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Sip_and_lump_sum_match_the_reference(string code)
    {
        var expected = Expected().GetProperty("funds").GetProperty(code);
        var a = Analyse(code, expected);
        foreach (var example in a.Examples)
        {
            string years = example.Years.ToString(CultureInfo.InvariantCulture);
            var sip = expected.GetProperty("sip").GetProperty(years);
            var lump = expected.GetProperty("lumpSum").GetProperty(years);
            if (sip.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(example.Sip);
                Assert.Null(example.LumpSum);
                Assert.NotNull(example.Missing);
                continue;
            }

            Assert.Equal(Date(sip.GetProperty("firstDate")), example.Sip!.FirstDate);
            Assert.Equal(sip.GetProperty("instalments").GetInt32(), example.Sip.Instalments);
            Near(sip.GetProperty("invested").GetDouble(), example.Sip.Invested, $"{code} {years}y SIP invested");
            Near(sip.GetProperty("value").GetDouble(), example.Sip.Value, $"{code} {years}y SIP value");
            Near(sip.GetProperty("xirr").GetDouble(), example.Sip.Xirr, $"{code} {years}y SIP XIRR");
            Assert.Equal(Date(lump.GetProperty("start")), example.LumpSum!.Start);
            Near(lump.GetProperty("value").GetDouble(), example.LumpSum.Value, $"{code} {years}y lump sum");
            Near(lump.GetProperty("cagr").GetDouble(), example.LumpSum.Cagr, $"{code} {years}y lump sum CAGR");
        }
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Risk_figures_match_the_reference(string code)
    {
        var expected = Expected().GetProperty("funds").GetProperty(code);
        var a = Analyse(code, expected);

        var v = expected.GetProperty("volatility");
        Assert.NotNull(a.Volatility);
        Assert.Equal(v.GetProperty("years").GetInt32(), a.Volatility!.Years);
        Assert.Equal(Date(v.GetProperty("start")), a.Volatility.Start);
        Assert.Equal(v.GetProperty("returns").GetInt32(), a.Volatility.Returns);
        Near(v.GetProperty("perYear").GetDouble(), a.Volatility.PerYear, $"{code} returns a year");
        Near(v.GetProperty("annualised").GetDouble(), a.Volatility.Annualised, $"{code} volatility");
        Near(v.GetProperty("sharpe").GetDouble(), a.Volatility.Sharpe, $"{code} Sharpe");
        Near(v.GetProperty("sortino").GetDouble(), a.Volatility.Sortino, $"{code} Sortino");

        foreach (var window in new[] { "3", "5", "10", "all" })
        {
            var e = expected.GetProperty("drawdowns").GetProperty(window);
            var dd = a.Drawdowns.SingleOrDefault(d => (d.Years?.ToString(CultureInfo.InvariantCulture) ?? "all") == window);
            if (e.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(dd);
                continue;
            }

            Assert.NotNull(dd);
            Assert.Equal(Date(e.GetProperty("start")), dd!.Start);
            Near(e.GetProperty("maxDrawdown").GetDouble(), dd.MaxDrawdown, $"{code} drawdown {window}");
            Assert.Equal(Date(e.GetProperty("peak")), dd.Peak);
            Assert.Equal(Date(e.GetProperty("trough")), dd.Trough);
            Assert.Equal(DateOrNull(e.GetProperty("recovery")), dd.Recovery);
        }

        var current = expected.GetProperty("currentDrawdown");
        Near(current.GetProperty("value").GetDouble(), a.Current!.Value, $"{code} current drawdown");
        Assert.Equal(Date(current.GetProperty("peakDate")), a.Current.PeakDate);
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void Benchmark_figures_match_the_reference(string code)
    {
        var expected = Expected().GetProperty("funds").GetProperty(code);
        var a = Analyse(code, expected);
        var e = expected.GetProperty("benchmarkStats");
        if (e.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(a.Benchmark);
            return;
        }

        var b = a.Benchmark!;
        foreach (var period in b.Periods)
        {
            string years = period.Years.ToString(CultureInfo.InvariantCulture);
            var excess = e.GetProperty("excess").GetProperty(years);
            var benchCagr = e.GetProperty("benchmarkCagr").GetProperty(years);
            if (excess.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(period.Excess);
                continue;
            }

            Near(benchCagr.GetDouble(), period.BenchmarkCagr, $"{code} {years}y benchmark CAGR");
            Near(excess.GetDouble(), period.Excess, $"{code} {years}y excess");
        }

        var beta = e.GetProperty("beta");
        Assert.Equal(beta.GetProperty("years").GetInt32(), b.Beta!.Years);
        Assert.Equal(Date(beta.GetProperty("start")), b.Beta.Start);
        Assert.Equal(Date(beta.GetProperty("end")), b.Beta.End);
        Assert.Equal(beta.GetProperty("returns").GetInt32(), b.Beta.Returns);
        Near(beta.GetProperty("value").GetDouble(), b.Beta.Beta, $"{code} beta");

        var te = e.GetProperty("trackingError");
        if (te.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(b.TrackingError);
        }
        else
        {
            Assert.Equal(Date(te.GetProperty("start")), b.TrackingError!.Start);
            Assert.Equal(te.GetProperty("returns").GetInt32(), b.TrackingError.Returns);
            Near(te.GetProperty("value").GetDouble(), b.TrackingError.Value, $"{code} tracking error");
        }

        Assert.Equal(e.GetProperty("downMonths").GetInt32(), b.Capture!.DownMonths);
        Assert.Equal(e.GetProperty("upMonths").GetInt32(), b.Capture.UpMonths);
        Near(e.GetProperty("downsideCapture").GetDouble(), b.Capture.Downside, $"{code} downside capture");
        Near(e.GetProperty("upsideCapture").GetDouble(), b.Capture.Upside, $"{code} upside capture");
    }

    [Fact]
    public void Direct_and_regular_gap_matches_the_reference()
    {
        var expected = Expected().GetProperty("directVsRegular").GetProperty("120716/100822");
        var direct = FundAnalytics.Analyse(Series("nav_120716.csv"), AsOf)!;
        var regular = FundAnalytics.Analyse(Series("nav_100822.csv"), AsOf)!;
        foreach (int years in FundAnalytics.PeriodYears)
        {
            string key = years.ToString(CultureInfo.InvariantCulture);
            var d = direct.Examples.Single(x => x.Years == years);
            var r = regular.Examples.Single(x => x.Years == years);
            var sip = expected.GetProperty("sip").GetProperty(key);
            var lump = expected.GetProperty("lumpSum").GetProperty(key);
            Near(sip.GetProperty("gap").GetDouble(), d.Sip!.Value - r.Sip!.Value, $"{years}y SIP gap");
            Near(lump.GetProperty("gap").GetDouble(), d.LumpSum!.Value - r.LumpSum!.Value, $"{years}y lump-sum gap");
        }
    }

    /// <summary>
    /// The worked example in the module's research note (UTI Nifty 50 Index Fund, as of 30 Oct 2026), as a reader
    /// sees it: rounded rupees and percentages.
    /// </summary>
    [Fact]
    public void Uti_nifty_50_worked_example_reproduces()
    {
        var direct = FundAnalytics.Analyse(Series("nav_120716.csv"), AsOf)!;
        var regular = FundAnalytics.Analyse(Series("nav_100822.csv"), AsOf)!;

        var d5 = direct.Examples.Single(x => x.Years == 5);
        Assert.Equal(60, d5.Sip!.Instalments);
        Assert.Equal(new DateOnly(2021, 11, 1), d5.Sip.FirstDate);
        Assert.Equal(600_000, d5.Sip.Invested);
        Assert.Equal(679_412, Math.Round(d5.Sip.Value));
        Assert.Equal(4.92, Math.Round(d5.Sip.Xirr!.Value * 100, 2));
        Assert.Equal(269_565, Math.Round(d5.LumpSum!.Value));

        var r5 = regular.Examples.Single(x => x.Years == 5);
        Assert.Equal(677_240, Math.Round(r5.Sip!.Value));
        Assert.Equal(4.79, Math.Round(r5.Sip.Xirr!.Value * 100, 2));
        Assert.Equal(268_021, Math.Round(r5.LumpSum!.Value));

        Assert.Equal([-10.16, 7.38, 6.16], direct.Trailing.Where(t => t.Years <= 5).Select(t => Math.Round(t.Cagr!.Value * 100, 2)));
        Assert.Equal([-10.26, 7.25, 6.03], regular.Trailing.Where(t => t.Years <= 5).Select(t => Math.Round(t.Cagr!.Value * 100, 2)));

        var dd = direct.Drawdowns.Single(x => x.Years == 5);
        Assert.Equal(-15.91, Math.Round(dd.MaxDrawdown * 100, 2));
        Assert.Equal((new DateOnly(2022, 1, 17), new DateOnly(2022, 6, 17)), (dd.Peak, dd.Trough));
        Assert.Equal(-15.95, Math.Round(regular.Drawdowns.Single(x => x.Years == 5).MaxDrawdown * 100, 2));
    }
}
