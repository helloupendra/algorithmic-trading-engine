using System.Globalization;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>One stored daily close, by its IST session.</summary>
public readonly record struct DailyClose(DateOnly Date, decimal Close);

/// <summary>A holding as the risk figures see it: what it is worth now, and its past daily closes by session.</summary>
public sealed record RiskHolding(string Symbol, decimal Value, IReadOnlyDictionary<DateOnly, decimal>? Closes);

/// <summary>The risk figures and what they rest on.</summary>
/// <param name="Sessions">The trading days the figures cover: daily returns, one fewer than the closes.</param>
/// <param name="CoveredWeight">The fraction of value (0 to 1) held in holdings with a close on every one of those days.</param>
/// <param name="MaxDrawdownPct">The largest fall from a peak, in per cent, as a positive number.</param>
/// <param name="Note">One sentence on what was used, for the report's <c>risk.note</c>.</param>
/// <param name="Gaps">What was left out and why, for the report's data notes.</param>
public sealed record RiskFigures(
    int Sessions,
    decimal? VolatilityAnnualPct,
    decimal? BetaVsNifty,
    decimal? MaxDrawdownPct,
    decimal CoveredWeight,
    string Note,
    IReadOnlyList<string> Gaps);

/// <summary>
/// The portfolio's past behaviour at today's weights: annual volatility, beta against NIFTY 50 and the largest fall,
/// from daily closes. Pure arithmetic, so the figures can be checked against ones worked out by hand.
/// </summary>
/// <remarks>
/// <para>
/// Today's holdings are weighted by today's value and their daily simple returns combined on NIFTY 50's sessions:
/// r_p = Σ w_i · r_i. Volatility is the sample standard deviation of r_p times √252; beta is
/// cov(r_p, r_NIFTY) / var(r_NIFTY), both with n − 1; the largest fall is the deepest drop from a running peak of the
/// compounded r_p. It is how this portfolio would have moved, not how the account did move, and not a forecast.
/// </para>
/// <para>
/// A holding counts only with a stored close on every session of the window; a holding without one is left out and
/// the rest re-weighted, and <see cref="RiskFigures.CoveredWeight"/> says how much of the value that is. A daily move
/// beyond <see cref="SuspectMove"/> is taken for a split or bonus the stored prices do not adjust for, never for a
/// real move, and that holding is left out by name: a 1:10 split read as a 90% fall would be an invented drawdown.
/// </para>
/// </remarks>
public static class PortfolioRiskMath
{
    /// <summary>A day's move larger than this (35%) is read as an unadjusted corporate action.</summary>
    public const decimal SuspectMove = 0.35m;

    public const int TradingDaysPerYear = 252;

    public const string Benchmark = "NIFTY 50";

    public static RiskFigures Compute(
        IReadOnlyList<RiskHolding> holdings,
        IReadOnlyList<DailyClose> index,
        int maxSessions = 250,
        int minSessions = 20)
    {
        var gaps = new List<string>();
        decimal totalValue = holdings.Where(h => h.Value > 0).Sum(h => h.Value);

        // The sessions: NIFTY 50's own, the newest maxSessions + 1 closes.
        var axis = index.GroupBy(c => c.Date).Select(g => g.Last()).OrderBy(c => c.Date).ToList();
        if (axis.Count > maxSessions + 1) axis = axis.GetRange(axis.Count - (maxSessions + 1), maxSessions + 1);
        int sessions = Math.Max(0, axis.Count - 1);

        if (totalValue <= 0)
        {
            return new RiskFigures(0, null, null, null, 0, "No holding has a price, so there is nothing to measure.", gaps);
        }

        if (sessions < minSessions)
        {
            return new RiskFigures(sessions, null, null, null, 0,
                $"The desk holds {sessions} trading days of {Benchmark} returns, fewer than the {minSessions} the figures need.", gaps);
        }

        var dates = axis.Select(c => c.Date).ToList();
        var covered = new List<(RiskHolding Holding, double[] Returns)>();
        var missing = new List<string>();
        foreach (var h in holdings.Where(h => h.Value > 0))
        {
            if (h.Closes is null || dates.Any(d => !h.Closes.ContainsKey(d) || h.Closes[d] <= 0))
            {
                missing.Add(h.Symbol);
                continue;
            }

            var returns = new double[sessions];
            bool suspect = false;
            for (int k = 1; k <= sessions; k++)
            {
                decimal r = h.Closes[dates[k]] / h.Closes[dates[k - 1]] - 1;
                if (Math.Abs(r) > SuspectMove) suspect = true;
                returns[k - 1] = (double)r;
            }

            if (suspect)
            {
                gaps.Add($"{h.Symbol} is left out of the risk figures: its stored closes move more than {SuspectMove * 100:0}% in a day, which reads as a split or bonus the prices are not adjusted for.");
                continue;
            }

            covered.Add((h, returns));
        }

        if (missing.Count > 0)
        {
            gaps.Add($"{missing.Count} {(missing.Count == 1 ? "holding has" : "holdings have")} no stored close on every one of the {sessions} trading days and {(missing.Count == 1 ? "is" : "are")} left out of the risk figures: {string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", …" : string.Empty)}.");
        }

        decimal coveredValue = covered.Sum(c => c.Holding.Value);
        decimal coveredWeight = Math.Round(coveredValue / totalValue, 4, MidpointRounding.AwayFromZero);
        string window = $"{dates[0]:yyyy-MM-dd} to {dates[^1]:yyyy-MM-dd}";
        if (covered.Count == 0)
        {
            return new RiskFigures(sessions, null, null, null, 0,
                $"No holding has a stored close on every trading day from {window}, so the figures cannot be measured.", gaps);
        }

        var portfolio = new double[sessions];
        foreach (var (holding, returns) in covered)
        {
            double w = (double)(holding.Value / coveredValue);
            for (int k = 0; k < sessions; k++) portfolio[k] += w * returns[k];
        }

        var market = new double[sessions];
        for (int k = 1; k <= sessions; k++) market[k - 1] = (double)(axis[k].Close / axis[k - 1].Close - 1);

        double vol = StdDev(portfolio) * Math.Sqrt(TradingDaysPerYear) * 100;
        double marketVar = Variance(market);
        double? beta = marketVar > 0 ? Covariance(portfolio, market) / marketVar : null;
        double drawdown = MaxDrawdown(portfolio) * 100;

        string note = string.Create(CultureInfo.InvariantCulture,
            $"Daily closes of {covered.Count} {(covered.Count == 1 ? "holding" : "holdings")} ({coveredWeight * 100:0.##}% of value) over {sessions} trading days, " +
            $"{window}, weighted by today's values, against {Benchmark}. How today's portfolio would have moved, not how the account moved, and not a forecast.");

        return new RiskFigures(
            sessions,
            Round(vol),
            beta is double b ? Round(b) : null,
            Round(drawdown),
            coveredWeight,
            note,
            gaps);
    }

    private static decimal Round(double value) => Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);

    private static double Mean(double[] x) => x.Average();

    private static double Variance(double[] x)
    {
        if (x.Length < 2) return 0;
        double m = Mean(x);
        return x.Sum(v => (v - m) * (v - m)) / (x.Length - 1);
    }

    private static double StdDev(double[] x) => Math.Sqrt(Variance(x));

    private static double Covariance(double[] x, double[] y)
    {
        double mx = Mean(x), my = Mean(y);
        double sum = 0;
        for (int i = 0; i < x.Length; i++) sum += (x[i] - mx) * (y[i] - my);
        return sum / (x.Length - 1);
    }

    /// <summary>The deepest fall from a running peak of the compounded returns, as a fraction.</summary>
    public static double MaxDrawdown(IEnumerable<double> returns)
    {
        double value = 1, peak = 1, worst = 0;
        foreach (double r in returns)
        {
            value *= 1 + r;
            peak = Math.Max(peak, value);
            worst = Math.Max(worst, (peak - value) / peak);
        }

        return worst;
    }
}
