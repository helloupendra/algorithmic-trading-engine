namespace AlgoTrading.Contracts.MutualFunds;

// GET api/MutualFunds/{code}/report and GET api/MutualFunds/compare: the fund analyst report.
// Units, as the console reads them: every field named ...Pct is a percent (7.38 is 7.38%); money is in rupees;
// dates are IST days (yyyy-MM-dd). A null is a figure that is not known, never a zero, and the matching Missing text
// says why. Facts only: nothing here ranks a fund except by a stated return, rates it or recommends it.

/// <summary>Where one kind of figure comes from and how current it is.</summary>
public class MutualFundSourceDto
{
    /// <summary>"nav", "benchmark", "category", "ter", "risk-free".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>"NAVs", "NIFTY 50 TRI", ...</summary>
    public string What { get; set; } = string.Empty;

    /// <summary>"AMFI (portal.amfiindia.com)", "niftyindices.com", ...</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The newest day the figures use; null when nothing is collected.</summary>
    public DateOnly? AsOf { get; set; }

    /// <summary>One line, e.g. "Not collected yet." or "Computed 31 Oct 2026 00:10 IST."</summary>
    public string? Note { get; set; }
}

/// <summary>What NAV history the report rests on.</summary>
public class MutualFundHistoryDto
{
    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }
    public int Navs { get; set; }

    /// <summary>
    /// True when the stored history reaches the scheme's first NAV in AMFI's history (or April 2006): the history
    /// backfill has passed the first date. False while older NAVs may still arrive.
    /// </summary>
    public bool Complete { get; set; }

    /// <summary>"NAVs from 3 Jan 2018 to 30 Oct 2026 (2,163 days)." and, when not complete, why longer periods may be empty.</summary>
    public string Note { get; set; } = string.Empty;
}

/// <summary>The scheme's place among its peers for one period, from the nightly ranking.</summary>
public class MutualFundCategoryPeriodDto
{
    public int Years { get; set; }

    /// <summary>1 for the highest return; null when the scheme has no return for the period.</summary>
    public int? Rank { get; set; }

    /// <summary>Peers with a return for the period, this scheme included.</summary>
    public int Peers { get; set; }

    public double? ReturnPct { get; set; }
    public double? MedianPct { get; set; }
    public double? HighestPct { get; set; }
    public double? LowestPct { get; set; }
}

/// <summary>One trailing period: the fund, its benchmark over the same dates, and its place in the category.</summary>
public class MutualFundPeriodDto
{
    public int Years { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly End { get; set; }

    /// <summary>Annualised (CAGR), percent.</summary>
    public double? CagrPct { get; set; }

    /// <summary>Not annualised, percent.</summary>
    public double? AbsolutePct { get; set; }

    public string? Missing { get; set; }
    public double? BenchmarkCagrPct { get; set; }

    /// <summary>Fund CAGR minus benchmark CAGR, percentage points; for an index fund this is the tracking difference.</summary>
    public double? ExcessPct { get; set; }

    public string? BenchmarkMissing { get; set; }
}

public class MutualFundCalendarYearDto
{
    public int Year { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }
    public double? ReturnPct { get; set; }
    public double? BenchmarkReturnPct { get; set; }

    /// <summary>The as-of year: from the last NAV of the year before to the as-of day.</summary>
    public bool YearToDate { get; set; }

    /// <summary>Why the fund's return for the year is not known.</summary>
    public string? Missing { get; set; }

    /// <summary>Why the benchmark's return for the year is not known, when the fund's is.</summary>
    public string? BenchmarkMissing { get; set; }
}

/// <summary>The trailing 3-year return on every day it can be computed.</summary>
public class MutualFundRollingDto
{
    public int Years { get; set; }
    public int Windows { get; set; }
    public DateOnly FirstEnd { get; set; }
    public DateOnly LastEnd { get; set; }
    public double LowestPct { get; set; }
    public DateOnly LowestEnd { get; set; }
    public double HighestPct { get; set; }
    public DateOnly HighestEnd { get; set; }
    public double MedianPct { get; set; }

    /// <summary>Percent of windows with a return below zero.</summary>
    public double BelowZeroPct { get; set; }

    /// <summary>Windows with a benchmark return over the same dates.</summary>
    public int? BenchmarkCompared { get; set; }

    /// <summary>Percent of those windows in which the fund's return was higher than the benchmark's.</summary>
    public double? AboveBenchmarkPct { get; set; }
}

public class MutualFundSipDto
{
    public double Instalment { get; set; }
    public int Instalments { get; set; }
    public DateOnly FirstDate { get; set; }
    public DateOnly LastDate { get; set; }
    public DateOnly ValuedOn { get; set; }
    public double Invested { get; set; }
    public double Units { get; set; }
    public double Value { get; set; }
    public double Gain { get; set; }
    public double? XirrPct { get; set; }
}

public class MutualFundLumpSumDto
{
    public double Amount { get; set; }
    public DateOnly Start { get; set; }
    public double StartNav { get; set; }
    public DateOnly ValuedOn { get; set; }
    public double EndNav { get; set; }
    public double Value { get; set; }
    public double Gain { get; set; }
    public double CagrPct { get; set; }
}

/// <summary>The ₹10,000 monthly SIP and the ₹2,00,000 lump sum over one period.</summary>
public class MutualFundExampleDto
{
    public int Years { get; set; }
    public MutualFundSipDto? Sip { get; set; }
    public MutualFundLumpSumDto? LumpSum { get; set; }
    public string? Missing { get; set; }
}

public class MutualFundVolatilityDto
{
    public int Years { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public int Returns { get; set; }
    public double ReturnsPerYear { get; set; }
    public double AnnualisedPct { get; set; }
    public double? Sharpe { get; set; }
    public double? Sortino { get; set; }
}

/// <summary>The risk-free rate Sharpe and Sortino use, with where it comes from.</summary>
public class MutualFundRiskFreeDto
{
    public double RatePct { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateOnly? AsOf { get; set; }
}

public class MutualFundDrawdownDto
{
    /// <summary>The window in years; null for the whole stored history.</summary>
    public int? Years { get; set; }

    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }

    /// <summary>The fall, negative percent (-15.91).</summary>
    public double MaxDrawdownPct { get; set; }

    public DateOnly Peak { get; set; }
    public double PeakNav { get; set; }
    public DateOnly Trough { get; set; }
    public double TroughNav { get; set; }

    /// <summary>The first day the NAV was back at the peak; null when it has not been yet.</summary>
    public DateOnly? Recovery { get; set; }
}

public class MutualFundRiskDto
{
    public MutualFundVolatilityDto? Volatility { get; set; }

    /// <summary>Why there is no volatility, when there is none.</summary>
    public string? VolatilityMissing { get; set; }

    public MutualFundRiskFreeDto? RiskFree { get; set; }

    /// <summary>Why Sharpe and Sortino are not shown, when they are not.</summary>
    public string? SharpeMissing { get; set; }

    public List<MutualFundDrawdownDto> Drawdowns { get; set; } = new();

    /// <summary>The 3, 5 or 10-year drawdown windows left out, each with why ("10 years: not enough history: ...").</summary>
    public List<string> DrawdownsMissing { get; set; } = new();

    /// <summary>The as-of NAV against the highest NAV stored, negative percent; zero at a new high.</summary>
    public double? CurrentDrawdownPct { get; set; }

    public DateOnly? HighestNavDate { get; set; }
    public double? HighestNav { get; set; }
}

public class MutualFundBetaDto
{
    public int Years { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public int Returns { get; set; }
    public double Beta { get; set; }
}

public class MutualFundTrackingErrorDto
{
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public int Returns { get; set; }
    public double ValuePct { get; set; }
}

public class MutualFundCaptureDto
{
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public int Months { get; set; }
    public int DownMonths { get; set; }
    public int UpMonths { get; set; }
    public double? DownsidePct { get; set; }
    public double? UpsidePct { get; set; }
}

/// <summary>The benchmark the scheme is compared with, and the figures against it.</summary>
public class MutualFundBenchmarkDto
{
    /// <summary>niftyindices.com's name ("NIFTY 50"); null when no benchmark applies.</summary>
    public string? Index { get; set; }

    /// <summary>"NIFTY 50 TRI".</summary>
    public string? Name { get; set; }

    /// <summary>"index-name", "category" or "none".</summary>
    public string Basis { get; set; } = "none";

    /// <summary>The sentence the page shows: what the benchmark is and why it was chosen, or why there is none.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Whether any values of the index are stored.</summary>
    public bool Collected { get; set; }

    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }

    /// <summary>The last day both the scheme and the index have a value.</summary>
    public DateOnly? LastCommonDate { get; set; }

    public bool IndexFund { get; set; }
    public MutualFundBetaDto? Beta { get; set; }
    public MutualFundTrackingErrorDto? TrackingError { get; set; }
    public MutualFundCaptureDto? Capture { get; set; }
    public List<string> Missing { get; set; } = new();
}

/// <summary>The scheme among its peers, from the nightly ranking.</summary>
public class MutualFundCategoryDto
{
    /// <summary>"Equity Scheme - Large Cap Fund (open-ended)"; null for a pre-2017 category.</summary>
    public string? PeerGroup { get; set; }

    /// <summary>"direct" or "regular": peers have the same plan, growth option.</summary>
    public string? Plan { get; set; }

    public DateOnly? AsOf { get; set; }
    public DateTime? ComputedUtc { get; set; }
    public List<MutualFundCategoryPeriodDto> Periods { get; set; } = new();

    /// <summary>What the comparison covers: the peers and how they are counted.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Why there is no ranking at all, when there is none.</summary>
    public string? Missing { get; set; }
}

public class MutualFundTerDto
{
    public DateOnly Date { get; set; }
    public string TerSchemeName { get; set; } = string.Empty;
    public double? RegularPct { get; set; }
    public double? DirectPct { get; set; }

    /// <summary>The TER of this scheme's own plan, when its plan is known.</summary>
    public double? ThisPlanPct { get; set; }
}

/// <summary>The direct and the regular plan's ₹ outcomes over one period, on the same dates.</summary>
public class MutualFundPlanGapDto
{
    public int Years { get; set; }
    public double? SipDirect { get; set; }
    public double? SipRegular { get; set; }

    /// <summary>Direct minus regular, rupees.</summary>
    public double? SipGap { get; set; }

    public double? LumpSumDirect { get; set; }
    public double? LumpSumRegular { get; set; }
    public double? LumpSumGap { get; set; }
    public string? Missing { get; set; }
}

public class MutualFundCostDto
{
    public MutualFundTerDto? Ter { get; set; }

    /// <summary>Why no TER is shown, when none is.</summary>
    public string? TerMissing { get; set; }

    public int? DirectCode { get; set; }
    public int? RegularCode { get; set; }
    public List<MutualFundPlanGapDto> PlanGap { get; set; } = new();

    /// <summary>Why the direct and regular plans are not compared, when they are not.</summary>
    public string? PlanGapMissing { get; set; }
}

/// <summary>GET api/MutualFunds/{code}/report.</summary>
public class MutualFundReportResponse
{
    public MutualFundSchemeDto Scheme { get; set; } = new();
    public string CategoryName { get; set; } = string.Empty;
    public string SchemeType { get; set; } = string.Empty;

    /// <summary>The NAV day every figure ends on.</summary>
    public DateOnly AsOf { get; set; }

    public double Nav { get; set; }
    public MutualFundHistoryDto History { get; set; } = new();
    public List<MutualFundSourceDto> Sources { get; set; } = new();
    public List<MutualFundPeriodDto> Returns { get; set; } = new();
    public List<MutualFundCalendarYearDto> CalendarYears { get; set; } = new();
    public MutualFundRollingDto? Rolling { get; set; }
    public string? RollingMissing { get; set; }
    public List<MutualFundExampleDto> Examples { get; set; } = new();
    public MutualFundRiskDto Risk { get; set; } = new();
    public MutualFundBenchmarkDto Benchmark { get; set; } = new();
    public MutualFundCategoryDto Category { get; set; } = new();
    public MutualFundCostDto Cost { get; set; } = new();

    /// <summary>Everything missing or partial, said plainly.</summary>
    public List<string> Notes { get; set; } = new();

    public string Disclaimer { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
}

/// <summary>GET api/MutualFunds/compare: 2 to 4 schemes' reports, all ending on the same NAV day.</summary>
public class MutualFundCompareResponse
{
    /// <summary>The latest NAV day every scheme has: the reports end here.</summary>
    public DateOnly AsOf { get; set; }

    public List<MutualFundReportResponse> Funds { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    public string Disclaimer { get; set; } = string.Empty;
}

/// <summary>The written summary that passed the desk's checks.</summary>
public class MutualFundSummaryDto
{
    public string Text { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public DateTime WrittenUtc { get; set; }
}

/// <summary>GET api/MutualFunds/{code}/report/summary: a summary, or the plain reason there is none.</summary>
public class MutualFundSummaryResponse
{
    public int SchemeCode { get; set; }
    public DateOnly AsOf { get; set; }
    public MutualFundSummaryDto? Summary { get; set; }
    public string? Note { get; set; }
}

/// <summary>One benchmark index's stored values.</summary>
public class MutualFundBenchmarkCoverageDto
{
    public string Index { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Rows { get; set; }
    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }

    /// <summary>The one-time history fetch reached 2006, or the index's first value.</summary>
    public bool HistoryComplete { get; set; }
}

/// <summary>One run of a fund-report job.</summary>
public class MutualFundJobRunDto
{
    public string Trigger { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateOnly? Reached { get; set; }
}

/// <summary>What the fund report's jobs have collected: benchmarks, TER and the category ranking.</summary>
public class MutualFundAnalyticsCoverageDto
{
    public List<MutualFundBenchmarkCoverageDto> Benchmarks { get; set; } = new();
    public MutualFundJobRunDto? BenchmarkSync { get; set; }
    public DateOnly? TerLatestDate { get; set; }
    public int TerSchemes { get; set; }
    public MutualFundJobRunDto? TerSync { get; set; }
    public DateOnly? StatsAsOf { get; set; }
    public int StatsSchemes { get; set; }
    public DateTime? StatsComputedUtc { get; set; }
    public MutualFundJobRunDto? StatsRun { get; set; }

    /// <summary>Jobs an admin asked for that wait for their turn (outside market hours).</summary>
    public List<string> Requested { get; set; } = new();
}
