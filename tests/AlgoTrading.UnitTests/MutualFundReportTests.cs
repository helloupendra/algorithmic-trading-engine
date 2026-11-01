using System.Globalization;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MutualFunds;
using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The fund report as the API returns it, built from the real NAV and TRI fixtures: the figures, their dates and
/// sources, and every gap said in words.
/// </summary>
public class MutualFundReportTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 30);
    private const string IndexFunds = "Open Ended Schemes(Other Scheme - Index Funds)";

    private static NavSeries Fixture(string file) => MutualFundAnalyticsReferenceTests.Series(file);

    private static JsonElement Expected(string code) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(MutualFundAnalyticsReferenceTests.Folder(), "expected.json"))).RootElement.GetProperty("funds").GetProperty(code);

    private static AlgoTrading.Contracts.MutualFunds.MutualFundSchemeDto Scheme(int code, string name, string plan, string category = IndexFunds, string option = "growth") =>
        new() { SchemeCode = code, SchemeName = name, Amc = "UTI Mutual Fund", Plan = plan, Option = option, Category = category, IsActive = true };

    private static MutualFundReportInputs UtiDirect(Action<MutualFundReportInputsBuilder>? change = null)
    {
        var b = new MutualFundReportInputsBuilder
        {
            Scheme = Scheme(120716, "UTI Nifty 50 Index Fund", "direct"),
            Navs = Fixture("nav_120716.csv"),
            OtherPlan = Scheme(100822, "UTI Nifty 50 Index Fund", "regular"),
            OtherPlanNavs = Fixture("nav_100822.csv"),
            BenchmarkValues = Fixture("tri_nifty50.csv"),
            BackfillCursor = new DateOnly(2006, 4, 1),
            BackfillDone = true,
            Ter = new MutualFundTer { NsdlSchemeCode = "UTIM/O/O/EIN/00/03/0013", Date = AsOf, SchemeName = "UTI Nifty 50 Index Fund", RegularTer = 0.37m, DirectTer = 0.25m },
            RiskFreePct = 5.73,
            RiskFreeSource = "RBI, National Summary Data Page (Ratios and Rates): 91-day Treasury Bill (primary) yield, week ended 16 Oct 2026",
            RiskFreeAsOf = new DateOnly(2026, 10, 16),
        };
        change?.Invoke(b);
        return b.Build();
    }

    /// <summary>A mutable stand-in for the init-only inputs, so each test changes one thing.</summary>
    private sealed class MutualFundReportInputsBuilder
    {
        public AlgoTrading.Contracts.MutualFunds.MutualFundSchemeDto Scheme { get; set; } = new();
        public NavSeries Navs { get; set; } = NavSeries.Empty;
        public DateOnly? AsOf { get; set; }
        public AlgoTrading.Contracts.MutualFunds.MutualFundSchemeDto? OtherPlan { get; set; }
        public NavSeries? OtherPlanNavs { get; set; }
        public string? OtherPlanMissing { get; set; }
        public NavSeries? BenchmarkValues { get; set; }
        public DateOnly? BackfillCursor { get; set; }
        public bool BackfillDone { get; set; }
        public MutualFundSchemeStats? Stats { get; set; }
        public List<MutualFundSchemeStats> Peers { get; set; } = [];
        public DateTime? StatsComputedUtc { get; set; }
        public MutualFundTer? Ter { get; set; }
        public string? TerMissing { get; set; }
        public double? RiskFreePct { get; set; }
        public string? RiskFreeSource { get; set; }
        public DateOnly? RiskFreeAsOf { get; set; }

        public MutualFundReportInputs Build() => new()
        {
            Scheme = Scheme,
            Navs = Navs,
            AsOf = AsOf,
            OtherPlan = OtherPlan,
            OtherPlanNavs = OtherPlanNavs,
            OtherPlanMissing = OtherPlanMissing,
            Benchmark = MutualFundBenchmarks.Choose(Scheme.SchemeName, MutualFundCategories.Parse(Scheme.Category)),
            BenchmarkValues = BenchmarkValues,
            BackfillCursor = BackfillCursor,
            BackfillDone = BackfillDone,
            Stats = Stats,
            Peers = Peers,
            StatsComputedUtc = StatsComputedUtc,
            Ter = Ter,
            TerMissing = TerMissing,
            RiskFreePct = RiskFreePct,
            RiskFreeSource = RiskFreeSource,
            RiskFreeAsOf = RiskFreeAsOf,
            NowUtc = new DateTime(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc),
        };
    }

    [Fact]
    public void The_uti_nifty_50_report_carries_the_reference_figures_with_dates_and_sources()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect())!;
        var expected = Expected("120716");

        Assert.Equal(AsOf, report.AsOf);
        Assert.Equal(162.3751, report.Nav, 6);
        Assert.Equal("Other Scheme - Index Funds", report.CategoryName);
        var five = report.Returns.Single(r => r.Years == 5);
        Assert.Equal(6.16, Math.Round(five.CagrPct!.Value, 2));
        Assert.Equal(new DateOnly(2021, 11, 1), five.Start);
        Assert.Equal(expected.GetProperty("benchmarkStats").GetProperty("excess").GetProperty("5").GetDouble() * 100, five.ExcessPct!.Value, 9);

        var example = report.Examples.Single(e => e.Years == 5);
        Assert.Equal(679_412, Math.Round(example.Sip!.Value));
        Assert.Equal(4.92, Math.Round(example.Sip.XirrPct!.Value, 2));
        Assert.Equal(269_565, Math.Round(example.LumpSum!.Value));

        Assert.Equal("index-name", report.Benchmark.Basis);
        Assert.Equal("NIFTY 50 TRI", report.Benchmark.Name);
        Assert.True(report.Benchmark.IndexFund);
        Assert.Equal(expected.GetProperty("benchmarkStats").GetProperty("trackingError").GetProperty("value").GetDouble() * 100,
            report.Benchmark.TrackingError!.ValuePct, 9);

        Assert.Equal(-15.91, Math.Round(report.Risk.Drawdowns.Single(d => d.Years == 5).MaxDrawdownPct, 2));
        Assert.Equal(5.73, report.Risk.RiskFree!.RatePct);
        Assert.Null(report.Risk.SharpeMissing);
        Assert.Equal(expected.GetProperty("volatility").GetProperty("sharpe").GetDouble(), report.Risk.Volatility!.Sharpe!.Value, 9);

        // Direct beside regular on the same dates: ₹2,172 more from the SIP and ₹1,545 from the lump sum over 5 years.
        var gap = report.Cost.PlanGap.Single(g => g.Years == 5);
        Assert.Equal((120716, 100822), (report.Cost.DirectCode!.Value, report.Cost.RegularCode!.Value));
        Assert.Equal(2_172, Math.Round(gap.SipGap!.Value));
        Assert.Equal(1_545, Math.Round(gap.LumpSumGap!.Value));
        Assert.Equal((0.37, 0.25, 0.25), (report.Cost.Ter!.RegularPct!.Value, report.Cost.Ter.DirectPct!.Value, report.Cost.Ter.ThisPlanPct!.Value));

        Assert.Equal(["nav", "benchmark", "ter", "risk-free"], report.Sources.Select(s => s.Key));
        Assert.All(report.Sources, s => Assert.NotNull(s.AsOf));
        Assert.Contains("not investment advice", report.Disclaimer, StringComparison.Ordinal);
        Assert.Equal(DateTimeKind.Utc, report.GeneratedUtc.Kind);
    }

    [Fact]
    public void Calendar_years_set_the_benchmark_beside_the_fund_over_the_same_dates()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect())!;
        var tri = Fixture("tri_nifty50.csv");

        var y2022 = report.CalendarYears.Single(y => y.Year == 2022);
        tri.TryGet(y2022.End!.Value, out double end);
        double start = tri.OnOrBefore(y2022.Start!.Value)!.Value.Value;
        Assert.Equal((end / start - 1) * 100, y2022.BenchmarkReturnPct!.Value, 9);
        Assert.True(report.CalendarYears.Single(y => y.Year == 2026).YearToDate);
        // The fixture starts in Sep 2016: 2016 has no start, and its reason says so.
        Assert.StartsWith("the NAVs start on 1 Sep 2016", report.CalendarYears.Single(y => y.Year == 2016).Missing, StringComparison.Ordinal);
    }

    [Fact]
    public void A_young_scheme_says_which_periods_its_history_does_not_cover()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect(b =>
        {
            b.Scheme = Scheme(143341, "UTI - Nifty Next 50 Index Fund", "direct");
            b.Navs = Fixture("nav_143341.csv");
            b.BenchmarkValues = Fixture("tri_niftynext50.csv");
            b.OtherPlan = null;
            b.OtherPlanNavs = null;
            b.OtherPlanMissing = "AMFI lists no regular plan.";
            b.BackfillCursor = new DateOnly(2018, 1, 1);
            b.BackfillDone = false;
        }))!;

        var ten = report.Returns.Single(r => r.Years == 10);
        Assert.Null(ten.CagrPct);
        Assert.Null(ten.BenchmarkCagrPct);
        Assert.Null(ten.BenchmarkMissing);   // the row's own reason covers it
        Assert.Equal("not enough history: the NAVs start on 29 Jun 2018, after 30 Oct 2016", ten.Missing);
        Assert.Equal("10 years: not enough history (the NAVs start on 29 Jun 2018, after 30 Oct 2016)", Assert.Single(report.Risk.DrawdownsMissing));
        Assert.Equal("NIFTY NEXT 50 TRI", report.Benchmark.Name);
        // The backfill has reached back past the scheme's first NAV: its history is complete.
        Assert.True(report.History.Complete);
        Assert.Equal("AMFI lists no regular plan.", report.Cost.PlanGapMissing);
    }

    [Fact]
    public void While_the_backfill_has_not_reached_a_scheme_s_start_the_report_says_older_navs_may_arrive()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect(b =>
        {
            b.Navs = new NavSeries(b.Navs.Points.Where(p => p.Date >= new DateOnly(2023, 1, 2)));
            b.BackfillCursor = new DateOnly(2023, 1, 1);
            b.BackfillDone = false;
        }))!;

        Assert.False(report.History.Complete);
        Assert.Contains("back to 1 Jan 2023 so far", report.History.Note, StringComparison.Ordinal);
        Assert.Contains("older NAVs of this scheme may still arrive", report.History.Note, StringComparison.Ordinal);
        Assert.Contains(report.Notes, n => n.StartsWith("The NAV history is not complete yet", StringComparison.Ordinal));
        Assert.Null(report.Returns.Single(r => r.Years == 5).CagrPct);
        Assert.Equal("not enough history: the NAVs start on 2 Jan 2023, after 30 Oct 2021", report.Returns.Single(r => r.Years == 5).Missing);
        Assert.Equal(3, report.Risk.Volatility!.Years);
    }

    [Fact]
    public void Without_a_risk_free_rate_sharpe_and_sortino_are_left_out_and_the_report_says_so()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect(b => b.RiskFreePct = null))!;
        Assert.Null(report.Risk.Volatility!.Sharpe);
        Assert.Null(report.Risk.RiskFree);
        Assert.StartsWith("Sharpe and Sortino are not shown: no risk-free rate is configured", report.Risk.SharpeMissing, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Sources, s => s.Key == "risk-free");
    }

    [Fact]
    public void A_benchmark_not_collected_yet_leaves_its_columns_empty_with_the_reason()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect(b => b.BenchmarkValues = NavSeries.Empty))!;
        Assert.False(report.Benchmark.Collected);
        Assert.All(report.Returns, r => Assert.Equal("NIFTY 50 TRI not collected yet", r.BenchmarkMissing));
        Assert.Contains(report.Notes, n => n.StartsWith("NIFTY 50 TRI is not collected yet", StringComparison.Ordinal));
        Assert.Equal("Not collected yet.", report.Sources.Single(s => s.Key == "benchmark").Note);
    }

    [Fact]
    public void The_category_position_comes_from_the_nightly_ranking_with_the_peers_median()
    {
        var mine = new MutualFundSchemeStats { SchemeCode = 120716, AsOfDate = AsOf, PeerKey = "open ended|other scheme - index funds", Plan = "direct", Return3y = 0.0738, Rank3y = 2, Peers3y = 3, ComputedUtc = DateTime.UtcNow };
        var peers = new List<MutualFundSchemeStats>
        {
            mine,
            new() { SchemeCode = 1, PeerKey = mine.PeerKey, Plan = "direct", Return3y = 0.10 },
            new() { SchemeCode = 2, PeerKey = mine.PeerKey, Plan = "direct", Return3y = 0.05 },
        };

        var report = MutualFundReportBuilder.Build(UtiDirect(b =>
        {
            b.Stats = mine;
            b.Peers = peers;
            b.StatsComputedUtc = DateTime.UtcNow;
        }))!;

        var three = report.Category.Periods.Single(p => p.Years == 3);
        Assert.Equal((2, 3), (three.Rank, three.Peers));
        Assert.Equal(7.38, three.MedianPct!.Value, 9);
        Assert.Equal(10, three.HighestPct!.Value, 9);
        Assert.Equal(5, three.LowestPct!.Value, 9);
        Assert.Null(report.Category.Missing);
        Assert.Equal("Other Scheme - Index Funds (open-ended)", report.Category.PeerGroup);
        Assert.Equal(AsOf, report.Sources.Single(s => s.Key == "category").AsOf);
    }

    [Theory]
    [InlineData("idcw", "direct", "Open Ended Schemes(Other Scheme - Index Funds)", "Only growth options are ranked")]
    [InlineData("growth", "unknown", "Open Ended Schemes(Other Scheme - Index Funds)", "The scheme's plan is not stated")]
    [InlineData("growth", "direct", "Open Ended Schemes(Income)", "AMFI lists the scheme under a category from before")]
    [InlineData("growth", "direct", "Open Ended Schemes(Other Scheme - Index Funds)", "The category ranking has not run yet")]
    public void Without_a_ranking_the_category_section_says_why(string option, string plan, string category, string reason)
    {
        var report = MutualFundReportBuilder.Build(UtiDirect(b => b.Scheme = Scheme(120716, "UTI Nifty 50 Index Fund", plan, category, option)))!;
        Assert.Empty(report.Category.Periods);
        Assert.StartsWith(reason, report.Category.Missing, StringComparison.Ordinal);
    }

    [Fact]
    public void An_idcw_option_s_report_says_its_returns_leave_the_payouts_out()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect(b => b.Scheme = Scheme(120717, "UTI Nifty 50 Index Fund", "direct", option: "idcw")))!;
        Assert.Contains(report.Notes, n => n.StartsWith("This is an IDCW option", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_as_of_an_earlier_day_ends_there()
    {
        var report = MutualFundReportBuilder.Build(UtiDirect(b => b.AsOf = new DateOnly(2025, 12, 31)))!;
        Assert.Equal(new DateOnly(2025, 12, 31), report.AsOf);
        Assert.Equal(new DateOnly(2025, 12, 31), report.Returns[0].End);
        Assert.Null(MutualFundReportBuilder.Build(UtiDirect(b => b.AsOf = new DateOnly(2010, 1, 1))));
    }

    [Fact]
    public void The_report_never_uses_rating_or_advice_words()
    {
        var reports = new[]
        {
            MutualFundReportBuilder.Build(UtiDirect())!,
            MutualFundReportBuilder.Build(UtiDirect(b =>
            {
                b.BenchmarkValues = null;
                b.RiskFreePct = null;
                b.BackfillDone = false;
                b.BackfillCursor = null;
                b.Scheme = Scheme(120717, "UTI Nifty 50 Index Fund", "direct", option: "idcw");
            }))!,
        };
        var banned = new System.Text.RegularExpressions.Regex(
            @"\b(best|worst|top|star|rating|recommend\w*|buy|sell|hold|avoid|should|consider|outperform\w*|underperform\w*)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var report in reports)
        {
            string text = JsonSerializer.Serialize(report);
            Assert.False(banned.IsMatch(text), banned.Match(text).Value);
        }
    }

    // ---------- the loader, on an in-memory database ----------

    private static async Task<(TradingDbContext Db, MutualFundReports Reports)> Seeded()
    {
        var db = MutualFundReportJobsTests.Db();
        void Add(int code, string name, string plan, string category, string file)
        {
            var navs = Fixture(file);
            db.MutualFundSchemes.Add(new MutualFundScheme
            {
                SchemeCode = code, SchemeName = name, Amc = "UTI Mutual Fund", Plan = plan, Option = "growth", Category = category,
                FirstNavDate = navs.First, LastNavDate = navs.Last, IsActive = true,
            });
            db.MutualFundNavs.AddRange(navs.Points.Select(p => new MutualFundNav { SchemeCode = code, Date = p.Date, Nav = (decimal)p.Value }));
        }

        Add(120716, "UTI Nifty 50 Index Fund", "direct", IndexFunds, "nav_120716.csv");
        Add(100822, "UTI Nifty 50 Index Fund", "regular", IndexFunds, "nav_100822.csv");
        Add(143341, "UTI - Nifty Next 50 Index Fund", "direct", "Open Ended Schemes(Index Funds - Equity Funds)", "nav_143341.csv");
        db.MutualFundBenchmarkValues.AddRange(Fixture("tri_nifty50.csv").Points.Select(p => new MutualFundBenchmarkValue { IndexName = "NIFTY 50", Date = p.Date, Value = (decimal)p.Value }));
        db.MutualFundTers.Add(new MutualFundTer
        {
            NsdlSchemeCode = "UTIM/O/O/EIN/00/03/0013", Date = AsOf, SchemeName = "UTI Nifty 50 Index Fund", NameKey = "uti nifty 50 index fund",
            SchemeType = "Open Ended", Category = "Other Scheme - Index Funds", RegularTer = 0.37m, DirectTer = 0.25m,
        });
        // One more day for the Next 50 fund only: compare must end on the day all have.
        db.MutualFundNavs.Add(new MutualFundNav { SchemeCode = 143341, Date = new DateOnly(2026, 11, 2), Nav = 30m });
        await db.SaveChangesAsync();
        var reports = new MutualFundReports(db, new MutualFundReportJobsTests.CountingSettings(), Options.Create(new MutualFundsOptions()));
        return (db, reports);
    }

    [Fact]
    public async Task The_loader_finds_the_other_plan_the_benchmark_and_the_ter_by_name()
    {
        var (db, reports) = await Seeded();
        await using var _ = db;

        var (report, notFound) = await reports.ReportAsync(100822, null, default);

        Assert.Null(notFound);
        Assert.Equal(677_240, Math.Round(report!.Examples.Single(e => e.Years == 5).Sip!.Value));
        Assert.Equal((120716, 100822), (report.Cost.DirectCode!.Value, report.Cost.RegularCode!.Value));
        Assert.Equal(2_172, Math.Round(report.Cost.PlanGap.Single(g => g.Years == 5).SipGap!.Value));
        Assert.Equal(0.37, report.Cost.Ter!.ThisPlanPct);
        Assert.True(report.Benchmark.Collected);
        Assert.Null(report.Risk.RiskFree);    // none configured in these options
        Assert.Equal("The category ranking has not run yet: it runs after the nightly NAV sync.", report.Category.Missing);

        Assert.Equal("No scheme 1 in the master.", (await reports.ReportAsync(1, null, default)).NotFound);
        Assert.StartsWith("Scheme 120716 has no NAV on or before 1 Jan 2010", (await reports.ReportAsync(120716, new DateOnly(2010, 1, 1), default)).NotFound, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_ter_disclosure_without_the_scheme_s_name_is_said_plainly()
    {
        var (db, reports) = await Seeded();
        await using var _ = db;

        var (report, _) = await reports.ReportAsync(143341, null, default);

        Assert.Null(report!.Cost.Ter);
        Assert.StartsWith("No TER found: AMFI's TER disclosure has no scheme named \"UTI - Nifty Next 50 Index Fund\"", report.Cost.TerMissing, StringComparison.Ordinal);
        Assert.False(report.Benchmark.Collected);   // NIFTY NEXT 50 TRI is not seeded
    }

    [Fact]
    public async Task Compare_ends_every_report_on_the_newest_day_all_the_schemes_have()
    {
        var (db, reports) = await Seeded();
        await using var _ = db;

        var (compare, problem) = await reports.CompareAsync([120716, 143341, 120716], default);

        Assert.Null(problem);
        Assert.Equal(AsOf, compare!.AsOf);
        Assert.Equal(2, compare.Funds.Count);
        Assert.All(compare.Funds, f => Assert.Equal(AsOf, f.AsOf));
        Assert.Contains("UTI Nifty 50 Index Fund (120716) has no later NAV", Assert.Single(compare.Notes), StringComparison.Ordinal);

        Assert.Equal("Compare takes 2 to 4 different scheme codes.", (await reports.CompareAsync([120716], default)).Problem);
        Assert.Equal("Compare takes 2 to 4 different scheme codes.", (await reports.CompareAsync([1, 2, 3, 4, 5], default)).Problem);
        Assert.Equal("No scheme 7 in the master.", (await reports.CompareAsync([120716, 7], default)).Problem);
    }
}
