namespace AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

// What the analytics compute. Returns are fractions (0.0738 is 7.38%); money is in rupees. Wherever a figure cannot
// be computed, the record says why in plain words (Missing) and every number is null: "not known yet" is never a zero.

/// <summary>The trailing return over <see cref="Years"/> years to <see cref="End"/>.</summary>
public sealed record PeriodReturn(int Years, DateOnly Target, DateOnly End, DateOnly? Start, double? Cagr, double? Absolute, string? Missing)
{
    public bool Known => Cagr is not null;
}

/// <summary>One calendar year's return, from the last NAV of the year before to the year's last NAV (or the as-of day).</summary>
public sealed record CalendarYearReturn(int Year, DateOnly? Start, DateOnly? End, double? Return, bool YearToDate, string? Missing);

/// <summary>The trailing N-year return as it stood on every day it can be computed.</summary>
public sealed record RollingReturns(
    int Years,
    int Windows,
    DateOnly FirstEnd,
    DateOnly LastEnd,
    double Lowest,
    DateOnly LowestEnd,
    double Highest,
    DateOnly HighestEnd,
    double Median,
    double NegativeShare,
    int? BenchmarkCompared,
    double? BenchmarkBeatShare);

/// <summary>A monthly SIP valued on the as-of day.</summary>
public sealed record SipResult(
    int Years,
    double Instalment,
    int Instalments,
    DateOnly FirstDate,
    DateOnly LastDate,
    DateOnly ValuedOn,
    double Invested,
    double Units,
    double Value,
    double Gain,
    double? Xirr);

/// <summary>A lump sum invested on the SIP's first day and valued on the as-of day.</summary>
public sealed record LumpSumResult(int Years, double Amount, DateOnly Start, double StartNav, DateOnly ValuedOn, double EndNav, double Value, double Gain, double Cagr);

/// <summary>The SIP and the lump sum over one period, or why they cannot be computed.</summary>
public sealed record InvestmentExample(int Years, SipResult? Sip, LumpSumResult? LumpSum, string? Missing);

/// <summary>Annualised volatility of daily returns, and Sharpe and Sortino when a risk-free rate was given.</summary>
public sealed record VolatilityResult(int Years, DateOnly Start, DateOnly End, int Returns, double PerYear, double Annualised, double? Sharpe, double? Sortino);

/// <summary>The deepest fall from a running peak within a window, and when (if) it was made good.</summary>
public sealed record DrawdownResult(
    int? Years,
    DateOnly Start,
    DateOnly End,
    double MaxDrawdown,
    DateOnly Peak,
    double PeakValue,
    DateOnly Trough,
    double TroughValue,
    DateOnly? Recovery);

/// <summary>How far the as-of value is below the highest value in the series.</summary>
public sealed record CurrentDrawdown(double Value, DateOnly PeakDate, double PeakValue);

/// <summary>The fund's trailing return beside the benchmark's over the same dates.</summary>
public sealed record BenchmarkPeriod(int Years, DateOnly? Start, DateOnly End, double? FundCagr, double? BenchmarkCagr, double? Excess, string? Missing);

/// <summary>Beta of daily returns against the benchmark's, on the dates both have a value.</summary>
public sealed record BetaResult(int Years, DateOnly Start, DateOnly End, int Returns, double Beta);

/// <summary>Annualised standard deviation of the daily return difference, over one year.</summary>
public sealed record TrackingErrorResult(DateOnly Start, DateOnly End, int Returns, double Value);

/// <summary>Upside and downside capture over 36 monthly returns anchored on the last common date.</summary>
public sealed record CaptureResult(DateOnly Start, DateOnly End, int Months, int DownMonths, int UpMonths, double? Downside, double? Upside);

/// <summary>Everything measured against a benchmark series.</summary>
public sealed record BenchmarkAnalysis(
    DateOnly? LastCommonDate,
    IReadOnlyList<BenchmarkPeriod> Periods,
    RollingReturns? Rolling,
    BetaResult? Beta,
    TrackingErrorResult? TrackingError,
    CaptureResult? Capture,
    IReadOnlyList<string> Missing);

/// <summary>A whole series analysed as of one day.</summary>
public sealed record FundAnalysis(
    DateOnly AsOf,
    DateOnly FirstDate,
    double LastValue,
    int Points,
    IReadOnlyList<PeriodReturn> Trailing,
    IReadOnlyList<CalendarYearReturn> CalendarYears,
    RollingReturns? Rolling,
    string? RollingMissing,
    IReadOnlyList<InvestmentExample> Examples,
    VolatilityResult? Volatility,
    IReadOnlyList<DrawdownResult> Drawdowns,
    CurrentDrawdown? Current,
    BenchmarkAnalysis? Benchmark);
