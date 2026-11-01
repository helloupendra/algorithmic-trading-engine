using System.Globalization;
using AlgoTrading.Contracts.MutualFunds;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>Everything one report is built from, already read from the database.</summary>
public sealed record MutualFundReportInputs
{
    public required MutualFundSchemeDto Scheme { get; init; }
    public required NavSeries Navs { get; init; }

    /// <summary>The day the report ends on (its last NAV on or before it); null for the last NAV.</summary>
    public DateOnly? AsOf { get; init; }

    /// <summary>The same fund's other plan (direct for regular, regular for direct), same option; null with a reason when there is none.</summary>
    public MutualFundSchemeDto? OtherPlan { get; init; }

    public NavSeries? OtherPlanNavs { get; init; }
    public string? OtherPlanMissing { get; init; }

    public required BenchmarkChoice Benchmark { get; init; }

    /// <summary>The benchmark's stored values; empty when the index is chosen but not collected yet.</summary>
    public NavSeries? BenchmarkValues { get; init; }

    /// <summary>The NAV backfill's progress: the first day of the oldest window stored, and whether it reached April 2006.</summary>
    public DateOnly? BackfillCursor { get; init; }

    public bool BackfillDone { get; init; }

    /// <summary>The scheme's row in the nightly ranking, and its peers' rows (same group and plan, itself included).</summary>
    public MutualFundSchemeStats? Stats { get; init; }

    public IReadOnlyList<MutualFundSchemeStats> Peers { get; init; } = [];

    /// <summary>When the ranking last ran successfully, if ever.</summary>
    public DateTime? StatsComputedUtc { get; init; }

    public MutualFundTer? Ter { get; init; }
    public string? TerMissing { get; init; }

    public double? RiskFreePct { get; init; }
    public string? RiskFreeSource { get; init; }
    public DateOnly? RiskFreeAsOf { get; init; }

    public DateTime NowUtc { get; init; }
}

/// <summary>
/// Turns a scheme's NAVs and what is known around them into the fund report: every figure from
/// <see cref="FundAnalytics"/>, each with its dates and where it comes from, and every gap said in words.
/// </summary>
/// <remarks>
/// Facts only. Nothing here rates a fund or says what to do with one: the category position is a rank by a stated
/// return among stated peers, and the words "best" or "top" never appear. A figure the stored history cannot support
/// is null with its reason, never a zero.
/// </remarks>
public static class MutualFundReportBuilder
{
    public const string Disclaimer =
        "Information about a mutual fund scheme's past NAVs, computed from public data. It is not investment advice: " +
        "OpenFNO is not registered with SEBI as an investment adviser or research analyst, and past returns say nothing " +
        "certain about future ones.";

    /// <summary>The report, or null when the scheme has no NAV on or before the day asked for.</summary>
    public static MutualFundReportResponse? Build(MutualFundReportInputs input)
    {
        var navs = input.AsOf is { } day ? input.Navs.Until(day) : input.Navs;
        if (navs.IsEmpty) return null;

        var benchmarkValues = input.BenchmarkValues is { IsEmpty: false } values ? values : null;
        bool indexFund = input.Benchmark.Basis == "index-name";
        double? rf = input.RiskFreePct / 100;
        var analysis = FundAnalytics.Analyse(navs, null, benchmarkValues, indexFund, rf)!;
        var category = MutualFundCategories.Parse(input.Scheme.Category);
        var notes = new List<string>();

        var report = new MutualFundReportResponse
        {
            Scheme = input.Scheme,
            CategoryName = category.Name,
            SchemeType = category.Type,
            AsOf = analysis.AsOf,
            Nav = analysis.LastValue,
            History = History(input, navs, notes),
            GeneratedUtc = DateTime.SpecifyKind(input.NowUtc, DateTimeKind.Utc),
            Disclaimer = Disclaimer,
        };

        report.Returns = Returns(analysis, input, benchmarkValues);
        report.CalendarYears = CalendarYears(analysis, benchmarkValues);
        report.Rolling = Rolling(analysis.Benchmark?.Rolling ?? analysis.Rolling);
        report.RollingMissing = analysis.RollingMissing;
        report.Examples = analysis.Examples.Select(Example).ToList();
        report.Risk = Risk(analysis, navs, input);
        report.Benchmark = Benchmark(input, analysis, benchmarkValues, indexFund);
        report.Category = Category(input, category, notes);
        report.Cost = Cost(input, navs, analysis.AsOf);
        report.Sources = Sources(input, analysis, benchmarkValues, report);

        if (input.Scheme.Option is "idcw")
            notes.Add("This is an IDCW option: its NAV falls by each payout, so these returns leave the payouts out. The growth option of the same scheme shows the return with nothing paid out.");
        else if (input.Scheme.Option is "other" or "unknown")
            notes.Add("The scheme's option is not stated as growth: if it pays out (IDCW) or issues bonus units, its NAV falls by each payout and these returns leave the payouts out.");
        if (!input.Scheme.IsActive)
            notes.Add($"The scheme has no NAV in the 30 days before AMFI's newest NAV day: it may be closed or merged. Its last NAV is from {FundAnalytics.Day(analysis.AsOf)}.");
        if (input.Benchmark.Index is not null && benchmarkValues is null)
            notes.Add($"{input.Benchmark.Index.Name} is not collected yet: the benchmark sync fetches it after 23:45 IST, so the comparisons with it are empty.");
        if (input.TerMissing is { } terMissing) notes.Add(terMissing);
        report.Notes = notes;
        return report;
    }

    private static MutualFundHistoryDto History(MutualFundReportInputs input, NavSeries navs, List<string> notes)
    {
        // The backfill stores AMFI's history from its cursor up. A scheme's first stored NAV is its real first NAV only
        // once the backfill has covered at least a week before it and found nothing: a first NAV on the cursor's own
        // week is just where the backfill has got to.
        var first = input.Navs.First;
        bool complete = input.BackfillDone
                        || (input.BackfillCursor is { } cursor && cursor.AddDays(NavSeries.ToleranceDays) <= first)
                        || first <= MutualFundHistory.Earliest.AddDays(NavSeries.ToleranceDays);
        string range = $"NAVs from {FundAnalytics.Day(navs.First)} to {FundAnalytics.Day(navs.Last)} ({navs.Count:N0} days with a NAV).";
        string note = complete
            ? range + (first <= MutualFundHistory.Earliest.AddDays(NavSeries.ToleranceDays)
                ? " AMFI's NAV history starts on 1 Apr 2006, so an older scheme's history starts there."
                : " That is the scheme's first NAV in AMFI's history.")
            : range + (input.BackfillCursor is { } c
                ? $" The NAV history backfill has stored AMFI's history back to {FundAnalytics.Day(c)} so far; older NAVs of this scheme may still arrive, and the longer periods below stay empty until they do."
                : " AMFI's NAV history has not been fetched yet; older NAVs of this scheme may exist, and the longer periods below stay empty until the history backfill stores them.");
        if (!complete) notes.Add("The NAV history is not complete yet: " + note);
        return new MutualFundHistoryDto { FirstDate = navs.First, LastDate = navs.Last, Navs = navs.Count, Complete = complete, Note = note };
    }

    private static List<MutualFundPeriodDto> Returns(FundAnalysis analysis, MutualFundReportInputs input, NavSeries? benchmark)
    {
        var result = new List<MutualFundPeriodDto>();
        foreach (var period in analysis.Trailing)
        {
            var dto = new MutualFundPeriodDto
            {
                Years = period.Years,
                Start = period.Start,
                End = period.End,
                CagrPct = Pct(period.Cagr),
                AbsolutePct = Pct(period.Absolute),
                Missing = period.Missing,
            };

            // Without the fund's own return the row's reason covers the benchmark columns too.
            var vs = analysis.Benchmark?.Periods.FirstOrDefault(p => p.Years == period.Years);
            if (period.Cagr is not null)
            {
                if (input.Benchmark.Index is null) dto.BenchmarkMissing = "no benchmark for this scheme";
                else if (benchmark is null) dto.BenchmarkMissing = $"{input.Benchmark.Index.Name} not collected yet";
                else if (vs is not null)
                {
                    dto.BenchmarkCagrPct = Pct(vs.BenchmarkCagr);
                    dto.ExcessPct = Pct(vs.Excess);
                    dto.BenchmarkMissing = vs.BenchmarkCagr is null ? vs.Missing : null;
                }
            }

            result.Add(dto);
        }

        return result;
    }

    private static List<MutualFundCalendarYearDto> CalendarYears(FundAnalysis analysis, NavSeries? benchmark) =>
        analysis.CalendarYears.Select(y =>
        {
            var dto = new MutualFundCalendarYearDto
            {
                Year = y.Year,
                Start = y.Start,
                End = y.End,
                ReturnPct = Pct(y.Return),
                YearToDate = y.YearToDate,
                Missing = y.Missing,
            };
            if (y is { Start: { } start, End: { } end, Return: not null } && benchmark is not null)
            {
                var (value, missing) = BenchmarkReturn(benchmark, start, end);
                dto.BenchmarkReturnPct = Pct(value);
                dto.BenchmarkMissing = missing;
            }

            return dto;
        }).ToList();

    /// <summary>
    /// The benchmark's return between two fund NAV days: its value on the end day exactly, and its last value on or
    /// before the start day (within 7 days), as for the period returns.
    /// </summary>
    public static (double? Return, string? Missing) BenchmarkReturn(NavSeries benchmark, DateOnly start, DateOnly end)
    {
        if (!benchmark.TryGet(end, out double last)) return (null, $"the benchmark has no value for {FundAnalytics.Day(end)}");
        if (benchmark.OnOrBefore(start) is not { } first) return (null, $"the benchmark has no value within {NavSeries.ToleranceDays} days before {FundAnalytics.Day(start)}");
        return (last / first.Value - 1, null);
    }

    private static MutualFundRollingDto? Rolling(RollingReturns? r) => r is null
        ? null
        : new MutualFundRollingDto
        {
            Years = r.Years,
            Windows = r.Windows,
            FirstEnd = r.FirstEnd,
            LastEnd = r.LastEnd,
            LowestPct = r.Lowest * 100,
            LowestEnd = r.LowestEnd,
            HighestPct = r.Highest * 100,
            HighestEnd = r.HighestEnd,
            MedianPct = r.Median * 100,
            BelowZeroPct = r.NegativeShare * 100,
            BenchmarkCompared = r.BenchmarkCompared,
            AboveBenchmarkPct = Pct(r.BenchmarkBeatShare),
        };

    private static MutualFundExampleDto Example(InvestmentExample e) => new()
    {
        Years = e.Years,
        Missing = e.Missing,
        Sip = e.Sip is not { } sip
            ? null
            : new MutualFundSipDto
            {
                Instalment = sip.Instalment,
                Instalments = sip.Instalments,
                FirstDate = sip.FirstDate,
                LastDate = sip.LastDate,
                ValuedOn = sip.ValuedOn,
                Invested = sip.Invested,
                Units = sip.Units,
                Value = sip.Value,
                Gain = sip.Gain,
                XirrPct = Pct(sip.Xirr),
            },
        LumpSum = e.LumpSum is not { } lump
            ? null
            : new MutualFundLumpSumDto
            {
                Amount = lump.Amount,
                Start = lump.Start,
                StartNav = lump.StartNav,
                ValuedOn = lump.ValuedOn,
                EndNav = lump.EndNav,
                Value = lump.Value,
                Gain = lump.Gain,
                CagrPct = lump.Cagr * 100,
            },
    };

    private static MutualFundRiskDto Risk(FundAnalysis analysis, NavSeries navs, MutualFundReportInputs input)
    {
        var risk = new MutualFundRiskDto();
        if (analysis.Volatility is { } v)
        {
            risk.Volatility = new MutualFundVolatilityDto
            {
                Years = v.Years,
                Start = v.Start,
                End = v.End,
                Returns = v.Returns,
                ReturnsPerYear = v.PerYear,
                AnnualisedPct = v.Annualised * 100,
                Sharpe = v.Sharpe,
                Sortino = v.Sortino,
            };
        }
        else
        {
            var year = FundDates.YearsBefore(navs.Last, 1);
            risk.VolatilityMissing = navs.First > year
                ? $"not enough history: a year of NAVs is needed (from {FundAnalytics.Day(year)}); they start on {FundAnalytics.Day(navs.First)}"
                : $"a gap in the history: no NAV within {NavSeries.ToleranceDays} days after the window's start";
        }

        if (input.RiskFreePct is { } rate)
            risk.RiskFree = new MutualFundRiskFreeDto { RatePct = rate, Source = input.RiskFreeSource ?? "set in configuration", AsOf = input.RiskFreeAsOf };
        risk.SharpeMissing = input.RiskFreePct is null
            ? "Sharpe and Sortino are not shown: no risk-free rate is configured (MutualFunds:RiskFreeRatePct), and the report does not use one it cannot name."
            : risk.Volatility is null ? "Sharpe and Sortino need the volatility, which is not known." : null;

        risk.Drawdowns = analysis.Drawdowns.Select(d => new MutualFundDrawdownDto
        {
            Years = d.Years,
            Start = d.Start,
            End = d.End,
            MaxDrawdownPct = d.MaxDrawdown * 100,
            Peak = d.Peak,
            PeakNav = d.PeakValue,
            Trough = d.Trough,
            TroughNav = d.TroughValue,
            Recovery = d.Recovery,
        }).ToList();
        foreach (int years in FundAnalytics.DrawdownYears.Where(y => analysis.Drawdowns.All(d => d.Years != y)))
        {
            var target = FundDates.YearsBefore(navs.Last, years);
            risk.DrawdownsMissing.Add(navs.First > target
                ? $"{years} years: not enough history (the NAVs start on {FundAnalytics.Day(navs.First)}, after {FundAnalytics.Day(target)})"
                : $"{years} years: a gap in the history (no NAV within {NavSeries.ToleranceDays} days after {FundAnalytics.Day(target)})");
        }

        if (analysis.Current is { } current)
        {
            risk.CurrentDrawdownPct = current.Value * 100;
            risk.HighestNavDate = current.PeakDate;
            risk.HighestNav = current.PeakValue;
        }

        return risk;
    }

    private static MutualFundBenchmarkDto Benchmark(MutualFundReportInputs input, FundAnalysis analysis, NavSeries? values, bool indexFund)
    {
        var choice = input.Benchmark;
        var dto = new MutualFundBenchmarkDto
        {
            Index = choice.Index?.Key,
            Name = choice.Index?.Name,
            Basis = choice.Basis,
            Note = choice.Note,
            Collected = values is not null,
            FirstDate = values?.First,
            LastDate = values?.Last,
            IndexFund = indexFund,
        };
        if (choice.Index is null) return dto;
        if (values is null)
        {
            dto.Missing.Add($"{choice.Index.Name} is not collected yet.");
            return dto;
        }

        var b = analysis.Benchmark!;
        dto.LastCommonDate = b.LastCommonDate;
        dto.Missing.AddRange(b.Missing);
        if (b.Beta is { } beta)
            dto.Beta = new MutualFundBetaDto { Years = beta.Years, Start = beta.Start, End = beta.End, Returns = beta.Returns, Beta = beta.Beta };
        if (b.TrackingError is { } te)
            dto.TrackingError = new MutualFundTrackingErrorDto { Start = te.Start, End = te.End, Returns = te.Returns, ValuePct = te.Value * 100 };
        if (b.Capture is { } c)
        {
            dto.Capture = new MutualFundCaptureDto
            {
                Start = c.Start,
                End = c.End,
                Months = c.Months,
                DownMonths = c.DownMonths,
                UpMonths = c.UpMonths,
                DownsidePct = c.Downside,
                UpsidePct = c.Upside,
            };
        }

        return dto;
    }

    private static MutualFundCategoryDto Category(MutualFundReportInputs input, FundCategory category, List<string> notes)
    {
        var dto = new MutualFundCategoryDto { PeerGroup = category.PeerKey is null ? null : category.PeerName, Plan = input.Scheme.Plan };
        string plan = input.Scheme.Plan is "direct" or "regular" ? input.Scheme.Plan + "-plan" : input.Scheme.Plan;
        dto.Note = category.PeerKey is null
            ? "No peer group: AMFI lists the scheme under a category from before SEBI's 2017 categories."
            : $"Among the active {plan} growth-option schemes AMFI lists as {category.PeerName} with a NAV on the day ranked, by trailing return to that day; 1 is the highest. " +
              "AMFI writes some categories in more than one way (\"Equity Scheme\" and \"Equity Schemes\"); those are counted together.";

        string? missing = category.PeerKey is null ? "AMFI lists the scheme under a category from before SEBI's 2017 categories, which has no peer group."
            : input.Scheme.Option != "growth" ? "Only growth options are ranked: an IDCW option's NAV falls by its payouts, so its returns are not comparable."
            : input.Scheme.Plan is not ("direct" or "regular") ? "The scheme's plan is not stated (an ETF, or a plan from before direct plans existed), so it has no peer group by plan."
            : input.StatsComputedUtc is null ? "The category ranking has not run yet: it runs after the nightly NAV sync."
            : input.Stats is null ? "The scheme is not in the latest ranking: it ranks active schemes with a NAV on the newest full NAV day."
            : null;
        if (missing is not null)
        {
            dto.Missing = missing;
            return dto;
        }

        var stats = input.Stats!;
        dto.AsOf = stats.AsOfDate;
        dto.ComputedUtc = DateTime.SpecifyKind(stats.ComputedUtc, DateTimeKind.Utc);
        foreach (int years in MutualFundStatsJob.Years)
        {
            var returns = input.Peers.Select(p => MutualFundStatsJob.Value(p, years)).Where(v => v is not null).Select(v => v!.Value).OrderBy(v => v).ToList();
            var (rank, peers) = MutualFundStatsJob.Place(stats, years);
            dto.Periods.Add(new MutualFundCategoryPeriodDto
            {
                Years = years,
                Rank = rank,
                Peers = peers ?? returns.Count,
                ReturnPct = Pct(MutualFundStatsJob.Value(stats, years)),
                MedianPct = returns.Count == 0 ? null : Median(returns) * 100,
                HighestPct = returns.Count == 0 ? null : returns[^1] * 100,
                LowestPct = returns.Count == 0 ? null : returns[0] * 100,
            });
        }

        if (!input.BackfillDone)
            notes.Add("The category counts include only schemes whose stored NAV history covers each period; while the NAV history backfill runs, the 3 and 5-year counts are smaller than they will be.");
        return dto;
    }

    private static MutualFundCostDto Cost(MutualFundReportInputs input, NavSeries navs, DateOnly asOf)
    {
        var cost = new MutualFundCostDto { TerMissing = input.TerMissing };
        if (input.Ter is { } ter)
        {
            cost.Ter = new MutualFundTerDto
            {
                Date = ter.Date,
                TerSchemeName = ter.SchemeName,
                RegularPct = (double?)ter.RegularTer,
                DirectPct = (double?)ter.DirectTer,
                ThisPlanPct = input.Scheme.Plan switch
                {
                    "direct" => (double?)ter.DirectTer,
                    "regular" => (double?)ter.RegularTer,
                    _ => null,
                },
            };
        }

        if (input.OtherPlan is not { } other || input.OtherPlanNavs is not { IsEmpty: false } otherNavs)
        {
            cost.PlanGapMissing = input.OtherPlanMissing ?? "The scheme's other plan is not in the master.";
            return cost;
        }

        bool thisIsDirect = input.Scheme.Plan == "direct";
        cost.DirectCode = thisIsDirect ? input.Scheme.SchemeCode : other.SchemeCode;
        cost.RegularCode = thisIsDirect ? other.SchemeCode : input.Scheme.SchemeCode;

        var otherCut = otherNavs.Until(asOf);
        if (otherCut.IsEmpty || asOf.DayNumber - otherCut.Last.DayNumber > NavSeries.ToleranceDays)
        {
            cost.PlanGapMissing = $"The {other.Plan} plan has no NAV within {NavSeries.ToleranceDays} days before {FundAnalytics.Day(asOf)}.";
            return cost;
        }

        foreach (int years in FundAnalytics.PeriodYears)
        {
            var mine = FundAnalytics.Example(navs, years);
            var theirs = FundAnalytics.Example(otherCut, years);
            var (direct, regular) = thisIsDirect ? (mine, theirs) : (theirs, mine);
            var gap = new MutualFundPlanGapDto { Years = years };
            if (direct.Sip is null || regular.Sip is null)
            {
                gap.Missing = direct.Sip is null ? $"direct plan: {direct.Missing}" : $"regular plan: {regular.Missing}";
            }
            else
            {
                gap.SipDirect = direct.Sip.Value;
                gap.SipRegular = regular.Sip.Value;
                gap.SipGap = direct.Sip.Value - regular.Sip.Value;
                gap.LumpSumDirect = direct.LumpSum?.Value;
                gap.LumpSumRegular = regular.LumpSum?.Value;
                gap.LumpSumGap = direct.LumpSum is { } dl && regular.LumpSum is { } rl ? dl.Value - rl.Value : null;
            }

            cost.PlanGap.Add(gap);
        }

        return cost;
    }

    private static List<MutualFundSourceDto> Sources(MutualFundReportInputs input, FundAnalysis analysis, NavSeries? benchmark, MutualFundReportResponse report)
    {
        var sources = new List<MutualFundSourceDto>
        {
            new()
            {
                Key = "nav", What = "NAVs", Source = "AMFI (portal.amfiindia.com): the daily NAV file and the NAV history", AsOf = analysis.AsOf,
                Note = $"{analysis.Points:N0} NAVs from {FundAnalytics.Day(analysis.FirstDate)}. Every return, SIP, lump sum, volatility and drawdown is computed from them by OpenFNO.",
            },
        };

        if (input.Benchmark.Index is { } index)
        {
            sources.Add(new MutualFundSourceDto
            {
                Key = "benchmark", What = index.Name, Source = "NSE Indices (niftyindices.com), total-return index history", AsOf = benchmark?.Last,
                Note = benchmark is null ? "Not collected yet." : input.Benchmark.Basis == "category" ? "The category's usual benchmark; the fund's own SID may name another." : "The index the scheme's name says it tracks.",
            });
        }

        if (report.Category.AsOf is { } rankedOn)
        {
            sources.Add(new MutualFundSourceDto
            {
                Key = "category", What = "Category ranking", Source = "OpenFNO's nightly ranking of AMFI NAVs", AsOf = rankedOn,
                Note = report.Category.ComputedUtc is { } at ? $"Computed {IstTime.ToIst(at).ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} IST." : null,
            });
        }

        sources.Add(new MutualFundSourceDto
        {
            Key = "ter", What = "Total expense ratio", Source = "AMFI's TER disclosure (amfiindia.com)", AsOf = input.Ter?.Date,
            Note = input.Ter is null ? input.TerMissing ?? "Not collected yet." : $"As AMFI lists it for \"{input.Ter.SchemeName}\".",
        });

        if (input.RiskFreePct is { } rate)
        {
            sources.Add(new MutualFundSourceDto
            {
                Key = "risk-free", What = "Risk-free rate", Source = input.RiskFreeSource ?? "set in configuration", AsOf = input.RiskFreeAsOf,
                Note = $"{rate.ToString("0.##", CultureInfo.InvariantCulture)}% a year, used for Sharpe and Sortino only.",
            });
        }

        return sources;
    }

    private static double Median(List<double> sorted)
    {
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double? Pct(double? fraction) => fraction * 100;
}
