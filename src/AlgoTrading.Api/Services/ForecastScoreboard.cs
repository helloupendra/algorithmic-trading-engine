using System.Text.Json;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>A model version as the scoreboard needs it.</summary>
/// <param name="Backtest">The walk-forward backtest as registered, or null when none was sent.</param>
public sealed record ScoreboardModel(string Key, string Version, string Target, string? Description, JsonElement? Backtest);

/// <summary>One forecast as the scoreboard needs it; the losses are null until it is scored.</summary>
/// <param name="Covered80">Whether the outcome fell inside the 80% band; null for targets that have none.</param>
/// <param name="Calibration">Every probability the forecast stated, with whether that event happened.</param>
public sealed record ScoreboardForecast(
    string ModelKey,
    string ModelVersion,
    string Target,
    string Underlying,
    DateOnly SessionDate,
    double? Loss,
    double? BaselineLoss,
    bool? Covered80,
    IReadOnlyList<CalibrationPair> Calibration)
{
    /// <summary>Scored, with both losses usable: the only forecasts any statistic counts.</summary>
    public bool IsScored => Loss is double l && BaselineLoss is double b && double.IsFinite(l) && double.IsFinite(b);
}

/// <summary>A stated probability and whether the event happened (1) or not (0).</summary>
public readonly record struct CalibrationPair(double P, int Y);

/// <summary>One tenth of the probability scale: how often the events said to have this chance happened.</summary>
public sealed record CalibrationBin(double From, double To, int N, double MeanP, double HitRate);

/// <summary>
/// One line of the scoreboard: a model version on one underlying, or on all of
/// them (<see cref="ForecastScoreboard.AllUnderlyings"/>).
/// </summary>
/// <param name="LiveCount">Scored forecasts. Issued ones still waiting for their close are not counted.</param>
/// <param name="Skill">1 − meanLoss / meanBaselineLoss; positive is better than the baseline. Null without scored forecasts.</param>
/// <param name="DiffCiLow">Lower end of the 95% bootstrap interval of mean(baselineLoss − loss); null before two sessions are scored.</param>
/// <param name="DiffCiHigh">Upper end of the same interval.</param>
/// <param name="Coverage80">Share of scored forecasts whose outcome fell inside their 80% band; null for targets without one.</param>
/// <param name="Calibration">Only the tenths that hold at least one stated probability, lowest first.</param>
/// <param name="FirstSession">The earliest scored session; null when none is scored.</param>
/// <param name="LastSession">The latest scored session.</param>
public sealed record ScoreboardRow(
    string ModelKey,
    string ModelVersion,
    string Target,
    string Underlying,
    string? Description,
    int LiveCount,
    double? MeanLoss,
    double? MeanBaselineLoss,
    double? Skill,
    double? DiffCiLow,
    double? DiffCiHigh,
    string Status,
    string StatusReason,
    double? Coverage80,
    IReadOnlyList<CalibrationBin> Calibration,
    DateOnly? FirstSession,
    DateOnly? LastSession,
    JsonElement? Backtest);

/// <summary>
/// Turns scored forecasts into the scoreboard: mean losses, skill, a bootstrap
/// interval, the verdict, calibration and band coverage.
/// </summary>
/// <remarks>
/// <para>
/// Pure: data in, rows out, no clock and no database, so every rule the page
/// shows is pinned by a test. The rules are the ones written down before any
/// live forecast existed (docs/modules/analysis.md, "the four rules"), and the
/// point of fixing them in code is that nobody moves the pass mark after
/// seeing the data.
/// </para>
/// <para>
/// The bootstrap resamples <em>sessions</em>, not forecasts. On a row for one
/// underlying that is the same thing, since each session has one forecast. On
/// the ALL row it is not: NIFTY, BANKNIFTY and SENSEX share most of a day's
/// move, so their three forecasts for one session are close to one piece of
/// evidence, not three. Resampling them as independent would make the interval
/// about √3 too narrow and let a model be called Proven on a third of the
/// evidence the rule asks for. Resampled by session, a day's forecasts go in
/// or out together.
/// </para>
/// <para>
/// The generator is a fixed-seed SplitMix64 written out here rather than
/// <see cref="Random"/>, whose seeded sequence .NET does not promise to keep
/// across versions: the same data must give the same interval on every load,
/// or the page would flicker and a verdict could change with a runtime upgrade.
/// </para>
/// </remarks>
public static class ForecastScoreboard
{
    /// <summary>The underlying of the row that pools every underlying of a model version.</summary>
    public const string AllUnderlyings = "ALL";

    /// <summary>Below this many scored forecasts a model is still collecting.</summary>
    public const int CollectingBelow = 20;

    /// <summary>Proven needs at least this many, with the whole interval above zero.</summary>
    public const int ProvenAt = 60;

    /// <summary>Retired needs at least this many, with the whole interval below zero.</summary>
    public const int RetiredAt = 120;

    /// <summary>Bootstrap resamples, as the contract fixes them.</summary>
    public const int Resamples = 2000;

    /// <summary>The bootstrap's seed. Changing it changes every interval on the page; don't.</summary>
    public const ulong Seed = 20260927;

    public const string Collecting = "collecting";
    public const string Testing = "testing";
    public const string Proven = "proven";
    public const string Retired = "retired";

    /// <summary>
    /// One row per model version and underlying that has forecasts, plus an
    /// ALL row for every model version, registered ones without forecasts included.
    /// </summary>
    /// <remarks>
    /// Ordered by target (range, trend, direction), model key, newest version
    /// first, then ALL before NIFTY, BANKNIFTY and SENSEX.
    /// </remarks>
    public static IReadOnlyList<ScoreboardRow> Build(
        IEnumerable<ScoreboardModel> models,
        IEnumerable<ScoreboardForecast> forecasts)
    {
        var modelsByVersion = models
            .GroupBy(m => (m.Key, m.Version))
            .ToDictionary(g => g.Key, g => g.First());

        var forecastsByVersion = forecasts
            .GroupBy(f => (f.ModelKey, f.ModelVersion, f.Target))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Every registered version gets its ALL row, so a model registered this
        // morning is on the page with its backtest before its first forecast.
        var versions = modelsByVersion.Values
            .Select(m => (m.Key, m.Version, m.Target))
            .Concat(forecastsByVersion.Keys)
            .Distinct()
            .ToList();

        var rows = new List<ScoreboardRow>();
        foreach (var (key, version, target) in versions)
        {
            modelsByVersion.TryGetValue((key, version), out var model);
            var mine = forecastsByVersion.GetValueOrDefault((key, version, target)) ?? [];

            rows.Add(Summarize(key, version, target, AllUnderlyings, model, mine));

            foreach (var underlying in mine.Select(f => f.Underlying).Distinct())
            {
                rows.Add(Summarize(key, version, target, underlying, model, mine.Where(f => f.Underlying == underlying).ToList()));
            }
        }

        return rows
            .OrderBy(r => OrderOf(ForecastTargets.All, r.Target))
            .ThenBy(r => r.Target, StringComparer.Ordinal)
            .ThenBy(r => r.ModelKey, StringComparer.Ordinal)
            .ThenByDescending(r => r.ModelVersion, StringComparer.Ordinal)
            .ThenBy(r => r.Underlying == AllUnderlyings ? -1 : OrderOf(ForecastUnderlyings.All, r.Underlying))
            .ThenBy(r => r.Underlying, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The row for one set of forecasts: one underlying's, or all of a version's.</summary>
    internal static ScoreboardRow Summarize(
        string key,
        string version,
        string target,
        string underlying,
        ScoreboardModel? model,
        IReadOnlyList<ScoreboardForecast> forecasts)
    {
        // A fixed order before anything is summed or resampled: the database
        // returns rows in whatever order it likes, and floating-point sums and
        // the bootstrap's draws both depend on order.
        var scored = forecasts
            .Where(f => f.IsScored)
            .OrderBy(f => f.SessionDate)
            .ThenBy(f => f.Underlying, StringComparer.Ordinal)
            .ToList();

        int n = scored.Count;
        double? meanLoss = n > 0 ? scored.Average(f => f.Loss!.Value) : null;
        double? meanBaseline = n > 0 ? scored.Average(f => f.BaselineLoss!.Value) : null;
        double? skill = meanLoss is double ml && meanBaseline is double mb && mb > 0 ? 1 - ml / mb : null;

        var interval = BootstrapInterval(scored);
        var (status, reason) = Judge(n, interval?.Low, interval?.High);

        var covered = scored.Where(f => f.Covered80 is not null).ToList();
        double? coverage80 = covered.Count > 0 ? covered.Count(f => f.Covered80 == true) / (double)covered.Count : null;

        return new ScoreboardRow(
            key,
            version,
            target,
            underlying,
            model?.Description,
            n,
            meanLoss,
            meanBaseline,
            skill,
            interval?.Low,
            interval?.High,
            status,
            reason,
            coverage80,
            Calibrate(scored.SelectMany(f => f.Calibration)),
            n > 0 ? scored[0].SessionDate : null,
            n > 0 ? scored[^1].SessionDate : null,
            model?.Backtest);
    }

    /// <summary>
    /// The verdict, by the fixed rule: collecting under 20 scored; proven at 60
    /// or more with the interval's lower end above zero; retired at 120 or more
    /// with its upper end below zero; testing otherwise.
    /// </summary>
    public static (string Status, string Reason) Judge(int n, double? ciLow, double? ciHigh)
    {
        if (n < CollectingBelow)
        {
            return (Collecting, n == 0
                ? $"No scored forecasts yet; {CollectingBelow} are needed before it is judged"
                : $"{n} of {CollectingBelow} scored forecasts; too few to judge yet");
        }

        if (n >= ProvenAt && ciLow > 0)
        {
            return (Proven, $"{n} scored forecasts; the whole 95% confidence interval is above zero, so it beats the baseline");
        }

        if (n >= RetiredAt && ciHigh < 0)
        {
            return (Retired, $"{n} scored forecasts; the whole 95% confidence interval is below zero, so the baseline is better");
        }

        if (ciLow > 0)
        {
            return (Testing, $"{n} of {ProvenAt} scored forecasts; ahead of the baseline so far, but not yet enough to call it proven");
        }

        if (ciHigh < 0)
        {
            return (Testing, $"{n} of {RetiredAt} scored forecasts; behind the baseline so far, but not yet enough to retire it");
        }

        return (Testing, n < ProvenAt
            ? $"{n} of {ProvenAt} scored forecasts; the confidence interval still includes no improvement"
            : $"{n} scored forecasts; the confidence interval still includes no improvement");
    }

    /// <summary>
    /// The 95% percentile bootstrap interval of mean(baselineLoss − loss),
    /// resampling whole sessions; null before two sessions are scored.
    /// </summary>
    /// <remarks>
    /// Each resample draws as many sessions as there are, with replacement, and
    /// takes the mean over every forecast in the drawn sessions. The ends are
    /// the 2.5th and 97.5th percentiles of the 2,000 means, interpolated
    /// linearly between neighbours (numpy's default), so a Python check of the
    /// same numbers lands in the same place. One session is not an interval:
    /// every resample would be that session.
    /// </remarks>
    public static (double Low, double High)? BootstrapInterval(IReadOnlyList<ScoreboardForecast> scored)
    {
        var sessions = scored
            .Where(f => f.IsScored)
            .GroupBy(f => f.SessionDate)
            .OrderBy(g => g.Key)
            .Select(g => (
                Sum: g.OrderBy(f => f.Underlying, StringComparer.Ordinal).Sum(f => f.BaselineLoss!.Value - f.Loss!.Value),
                Count: g.Count()))
            .ToArray();

        int k = sessions.Length;
        if (k < 2)
        {
            return null;
        }

        var rng = new SplitMix64(Seed);
        var means = new double[Resamples];
        for (int b = 0; b < Resamples; b++)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < k; i++)
            {
                var (s, c) = sessions[rng.NextIndex(k)];
                sum += s;
                count += c;
            }

            means[b] = sum / count;
        }

        Array.Sort(means);
        return (Percentile(means, 0.025), Percentile(means, 0.975));
    }

    /// <summary>
    /// Stated probabilities grouped into tenths ([0, 0.1), [0.1, 0.2) … [0.9, 1.0]),
    /// with the mean stated probability and the share that happened; empty tenths
    /// are left out.
    /// </summary>
    public static IReadOnlyList<CalibrationBin> Calibrate(IEnumerable<CalibrationPair> pairs)
    {
        return pairs
            .Where(p => double.IsFinite(p.P) && p.P >= 0 && p.P <= 1)
            // 1.0 belongs to the last tenth. The small nudge keeps a stated 0.3,
            // stored as 0.29999999999999999, in the tenth it was written as.
            .GroupBy(p => Math.Min(9, (int)Math.Floor(p.P * 10 + 1e-9)))
            .OrderBy(g => g.Key)
            .Select(g => new CalibrationBin(
                g.Key / 10.0,
                (g.Key + 1) / 10.0,
                g.Count(),
                g.Average(p => p.P),
                g.Average(p => (double)p.Y)))
            .ToList();
    }

    /// <summary>
    /// What the scoreboard reads from a stored <c>scores</c> object:
    /// <c>metrics.covered80</c> and the <c>calibration</c> pairs.
    /// </summary>
    /// <remarks>
    /// The scores were validated when they were written, but the column is
    /// text: anything that cannot be read is left out of these two statistics
    /// rather than failing the whole page. The losses do not come from here;
    /// they have their own columns.
    /// </remarks>
    public static (bool? Covered80, IReadOnlyList<CalibrationPair> Calibration) ReadScores(string? scoresJson)
    {
        if (string.IsNullOrWhiteSpace(scoresJson))
        {
            return (null, []);
        }

        try
        {
            using var doc = JsonDocument.Parse(scoresJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, []);
            }

            bool? covered = null;
            if (root.TryGetProperty("metrics", out var metrics)
                && metrics.ValueKind == JsonValueKind.Object
                && metrics.TryGetProperty("covered80", out var c)
                && c.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                covered = c.GetBoolean();
            }

            var pairs = new List<CalibrationPair>();
            if (root.TryGetProperty("calibration", out var calibration) && calibration.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in calibration.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Number
                        && item.TryGetProperty("y", out var y) && TryReadOutcome(y, out int happened))
                    {
                        pairs.Add(new CalibrationPair(p.GetDouble(), happened));
                    }
                }
            }

            return (covered, pairs);
        }
        catch (JsonException)
        {
            return (null, []);
        }
    }

    /// <summary>1 or 0, written as a number or as true/false.</summary>
    internal static bool TryReadOutcome(JsonElement y, out int happened)
    {
        happened = 0;
        switch (y.ValueKind)
        {
            case JsonValueKind.True:
                happened = 1;
                return true;
            case JsonValueKind.False:
                return true;
            case JsonValueKind.Number when y.TryGetDouble(out double v) && (v == 0 || v == 1):
                happened = (int)v;
                return true;
            default:
                return false;
        }
    }

    private static double Percentile(double[] sorted, double q)
    {
        double position = q * (sorted.Length - 1);
        int below = (int)Math.Floor(position);
        int above = Math.Min(below + 1, sorted.Length - 1);
        return sorted[below] + (sorted[above] - sorted[below]) * (position - below);
    }

    private static int OrderOf(IReadOnlyList<string> known, string value)
    {
        for (int i = 0; i < known.Count; i++)
        {
            if (string.Equals(known[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return known.Count;
    }

    /// <summary>
    /// SplitMix64 (Steele, Lea and Flood, 2014): tiny, fast, and the same
    /// sequence for the same seed on every runtime and machine.
    /// </summary>
    private struct SplitMix64
    {
        private ulong _state;

        public SplitMix64(ulong seed) => _state = seed;

        public ulong Next()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>
        /// A draw in [0, <paramref name="count"/>): the high word of a 64×64-bit
        /// product (Lemire), with no modulo bias worth the name at these sizes.
        /// </summary>
        public int NextIndex(int count) => (int)Math.BigMul(Next(), (ulong)count, out _);
    }
}
