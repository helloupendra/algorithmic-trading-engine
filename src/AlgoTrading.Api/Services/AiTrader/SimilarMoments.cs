using System.Collections.Concurrent;
using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.OptionHistory;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>A stored moment as the base rates read it: its day, time, EMA side, features (<see cref="SituationMath.Vector"/>) and outcomes.</summary>
public sealed record SituationPoint(
    DateOnly Day, TimeOnly Slot, string EmaSide, double[] Vector,
    double Return30MinPct, double Return60MinPct, double ReturnToClosePct, double MaxUpPct, double MaxDownPct);

/// <summary>
/// The nearest past moments to now (<see cref="SimilarMoments.Nearest"/>): how many earlier rows there were, the
/// chosen ones with their distances, and the features left out of the match because they are not known now.
/// </summary>
public sealed record SimilarResult(int PastRows, IReadOnlyList<(SituationPoint Point, double Distance)> Nearest, IReadOnlyList<string> Unmatched);

/// <summary>
/// "What happened after moments like this one": the K past moments of an index nearest to now, and their outcomes
/// in a line of the brief. Base rates, not a forecast.
/// </summary>
/// <remarks>
/// <para>
/// The leakage rule: only rows of days before the brief's day are read, so a replay of 16 Sep sees nothing of
/// 16 Sep or after, and the features are scaled over those rows alone.
/// </para>
/// <para>
/// Similar means: the same side of the EMAs (above both, below both, between), a moment within 30 minutes of the
/// same time of day, and then the smallest standardised Euclidean distance over the numeric features known now
/// (<see cref="SituationMath.VectorNames"/>; each scaled by its standard deviation over the earlier rows). A row
/// missing a feature known now is not a candidate; a feature not known now (India VIX unrecorded) is left out of
/// the match and the line says so. One moment per day, its nearest: the moments of one day share most of their
/// future, and counting a day several times would only look like more evidence.
/// </para>
/// </remarks>
public static class SimilarMoments
{
    /// <summary>How many past moments a base rate rests on.</summary>
    public const int K = 50;

    /// <summary>A candidate's time of day is at most this far from now's.</summary>
    public const int TimeWindowMinutes = 30;

    private static readonly int MinutesIndex = IndexOf("minutes since the open");

    private static readonly int OutcomeEndMinutes = (int)(SituationMath.OutcomeEnd - SituationMath.SessionOpen).TotalMinutes;

    private static int IndexOf(string name)
    {
        for (int i = 0; i < SituationMath.VectorNames.Count; i++)
        {
            if (SituationMath.VectorNames[i] == name) return i;
        }

        throw new InvalidOperationException($"No feature named {name}.");
    }

    /// <summary>The <paramref name="k"/> past moments nearest to <paramref name="now"/>, one per day, from rows of days before now's day only.</summary>
    public static SimilarResult Nearest(IReadOnlyList<SituationPoint> pool, SituationFacts now, int k = K)
    {
        var past = pool.Where(p => p.Day < now.Day).ToList();
        var x = SituationMath.Vector(now);
        var dims = Enumerable.Range(0, x.Length).Where(i => !double.IsNaN(x[i])).ToList();
        var unmatched = Enumerable.Range(0, x.Length).Where(i => double.IsNaN(x[i])).Select(i => SituationMath.VectorNames[i]).ToList();

        // Each feature's spread over the earlier rows, so a point of VIX and a minute of the day weigh alike.
        var scale = new double[x.Length];
        foreach (int i in dims)
        {
            var values = past.Select(p => p.Vector[i]).Where(v => !double.IsNaN(v)).ToList();
            if (values.Count < 2) continue;
            double mean = values.Average();
            scale[i] = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
        }

        var scaled = dims.Where(i => scale[i] > 0).ToList();
        var nearest = past
            .Where(p => p.EmaSide == now.EmaSide
                        && Math.Abs(p.Vector[MinutesIndex] - now.MinutesSinceOpen) <= TimeWindowMinutes
                        && dims.All(i => !double.IsNaN(p.Vector[i])))
            .Select(p => (Point: p, Distance: Math.Sqrt(scaled.Sum(i => Math.Pow((p.Vector[i] - x[i]) / scale[i], 2)))))
            .GroupBy(t => t.Point.Day)
            .Select(g => g.MinBy(t => t.Distance))
            .OrderBy(t => t.Distance).ThenByDescending(t => t.Point.Day)
            .Take(k)
            .ToList();
        return new SimilarResult(past.Count, nearest, unmatched);
    }

    /// <summary>The brief's line for one index.</summary>
    public static string Describe(string name, SituationFacts now, SimilarResult result, int k = K)
    {
        if (result.PastRows == 0) return $"{name}: no past moments stored yet, so no base rate.";
        if (result.Nearest.Count < k)
        {
            return $"{name}: too little history for a base rate: {result.Nearest.Count} similar past day{(result.Nearest.Count == 1 ? string.Empty : "s")} before today, {k} needed.";
        }

        var points = result.Nearest.Select(t => t.Point).ToList();
        var first = points.Min(p => p.Day);
        string hour = now.MinutesSinceOpen + 60 > OutcomeEndMinutes ? "next hour (cut at 15:25)" : "next hour";
        var hourly = points.Select(p => p.Return60MinPct).ToList();
        var toClose = points.Select(p => p.ReturnToClosePct).ToList();
        string line = string.Create(CultureInfo.InvariantCulture,
            $"{name} ({points.Count} days since {first:MMM yyyy}, price {SideWords(now.EmaSide)} as now): "
            + $"{hour} up {Share(hourly)} of the time, median {Pct(Median(hourly))} (middle half {Pct(Quantile(hourly, 0.25))} to {Pct(Quantile(hourly, 0.75))}); "
            + $"to 15:25 up {Share(toClose)}, median {Pct(Median(toClose))} (middle half {Pct(Quantile(toClose, 0.25))} to {Pct(Quantile(toClose, 0.75))}); "
            + $"biggest move to the close: up median {Pct(Median(points.Select(p => p.MaxUpPct).ToList()))}, down median {Pct(Median(points.Select(p => p.MaxDownPct).ToList()))}");
        return result.Unmatched.Count == 0 ? line : line + $" (matched without {string.Join(" and ", result.Unmatched)}: not known now)";
    }

    private static string SideWords(string side) => side switch
    {
        SituationMath.Above => "above both EMAs",
        SituationMath.Below => "below both EMAs",
        _ => "between the EMAs",
    };

    private static string Share(IReadOnlyList<double> values) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(100.0 * values.Count(v => v > 0) / values.Count):0}%");

    /// <summary>Signed, two decimals, with a true minus sign: "+0.03%", "−0.18%".</summary>
    public static string Pct(double value) => value.ToString("+0.00;−0.00;0.00", CultureInfo.InvariantCulture) + "%";

    public static double Median(IReadOnlyList<double> values) => Quantile(values, 0.5);

    /// <summary>The <paramref name="q"/> quantile, interpolating between the two nearest values.</summary>
    public static double Quantile(IReadOnlyList<double> values, double q)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return double.NaN;
        double at = q * (sorted.Count - 1);
        int below = (int)Math.Floor(at);
        int above = Math.Min(below + 1, sorted.Count - 1);
        return sorted[below] + (sorted[above] - sorted[below]) * (at - below);
    }

    /// <summary>A stored row as a point; null when an outcome is missing (the bars after it had a gap).</summary>
    public static SituationPoint? Point(AiTraderSituation s) =>
        s is { Return30MinPct: double r30, Return60MinPct: double r60, ReturnToClosePct: double close, MaxUpPct: double up, MaxDownPct: double down }
            ? new SituationPoint(s.Day, s.Slot, s.EmaSide, SituationMath.Vector(SituationMath.Facts(s)), r30, r60, close, up, down)
            : null;
}

/// <summary>
/// The stored moments of each index, kept in memory so a look reads no table: about 50,000 points an index. Loaded
/// on first use and again after the builder has written new days (<see cref="Invalidate"/>), so refreshed each night
/// after the day's build. <see cref="AiTraderSituationsService"/> loads it outside the session (<see cref="WarmAsync"/>),
/// so the morning's first look does not read the table.
/// </summary>
public sealed class AiTraderSituationLibrary(IServiceScopeFactory scopes)
{
    private readonly ConcurrentDictionary<string, Loaded> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _version;

    /// <summary>New rows were written: the next read loads them.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    /// <summary>Every index loaded and current; a no-op when it already is.</summary>
    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, _, _) in MarketBriefBuilder.Indices) await ForAsync(name, cancellationToken);
    }

    public async Task<IReadOnlyList<SituationPoint>> ForAsync(string underlying, CancellationToken cancellationToken)
    {
        int version = Volatile.Read(ref _version);
        if (_loaded.TryGetValue(underlying, out var kept) && kept.Version == version) return kept.Points;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded.TryGetValue(underlying, out kept) && kept.Version == version) return kept.Points;
            await using var scope = scopes.CreateAsyncScope();
            var points = await LoadAsync(scope.ServiceProvider.GetRequiredService<TradingDbContext>(), underlying, cancellationToken);
            _loaded[underlying] = new Loaded(version, points);
            return points;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>An index's stored moments with every outcome known, oldest first.</summary>
    public static async Task<IReadOnlyList<SituationPoint>> LoadAsync(TradingDbContext db, string underlying, CancellationToken cancellationToken)
    {
        var rows = await db.AiTraderSituations.AsNoTracking()
            .Where(s => s.Underlying == underlying && s.Return30MinPct != null && s.Return60MinPct != null && s.ReturnToClosePct != null
                        && s.MaxUpPct != null && s.MaxDownPct != null)
            .OrderBy(s => s.Day).ThenBy(s => s.Slot)
            .ToListAsync(cancellationToken);
        return rows.Select(SimilarMoments.Point).OfType<SituationPoint>().ToList();
    }

    private sealed record Loaded(int Version, IReadOnlyList<SituationPoint> Points);
}

/// <summary>
/// The trading days from a day to an index's nearest option expiry, from the exchanges' own expiry calendar
/// (<see cref="IndexOptionExpiryCalendar"/>, from Aug 2020) together with the expiries of the instrument master
/// (listed, and expired ones it keeps disabled), and the session service's trading days. Holidays before the year
/// the holiday calendar holds count as trading days, so a count across one is a day long.
/// </summary>
public sealed class SituationCalendar(TradingDbContext db, IndexOptionExpiryCalendar expiries, IMarketSessionService sessions)
{
    private readonly Dictionary<string, IReadOnlyList<DateOnly>> _byUnderlying = new(StringComparer.OrdinalIgnoreCase);

    public async Task<int?> TradingDaysToExpiryAsync(string underlying, string exchange, DateOnly day, CancellationToken cancellationToken)
    {
        if (!_byUnderlying.TryGetValue(underlying, out var dates))
        {
            var listed = await db.Instruments.AsNoTracking()
                .Where(i => i.Underlying == underlying && i.ExpiryDate != null && (i.InstrumentType == "CE" || i.InstrumentType == "PE"))
                .Select(i => i.ExpiryDate!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);
            dates = expiries.For(underlying).Concat(listed).Distinct().Order().ToList();
            _byUnderlying[underlying] = dates;
        }

        return SituationMath.TradingDaysToExpiry(day, dates, d => sessions.GetSessionInfo(IstTime.MiddayUtc(d), exchange, "FO").IsTradingDay);
    }
}

/// <summary>
/// The brief's "similar past moments" section: each index's moment described now (<see cref="SituationMath.Features"/>,
/// from the recorded 1-minute bars up to the brief's moment, live or a replay's) and the base rates of the past
/// moments nearest to it (<see cref="SimilarMoments"/>).
/// </summary>
public sealed class SimilarMomentsSection(
    ILiveDataService live,
    SituationCalendar calendar,
    AiTraderSituationLibrary library,
    ILogger<SimilarMomentsSection> logger)
{
    public static readonly string Header = string.Create(CultureInfo.InvariantCulture,
        $"SIMILAR PAST MOMENTS (base rates from the {SimilarMoments.K} most similar past moments of each index, one a day, only days before today; not a forecast)");

    public async Task<string> BuildAsync(DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var vix = await MinutesAsync(MarketBriefBuilder.VixSymbol, asOfUtc, cancellationToken);
        var day = IstTime.DateOf(asOfUtc);
        var lines = new List<string>();
        foreach (var (name, spot, exchange) in MarketBriefBuilder.Indices)
        {
            try
            {
                int? dte = await calendar.TradingDaysToExpiryAsync(name, exchange, day, cancellationToken);
                var (now, why) = SituationMath.Features(asOfUtc, await MinutesAsync(spot, asOfUtc, cancellationToken), vix, dte);
                if (now is null)
                {
                    lines.Add($"{name}: cannot be compared now: {why}.");
                    continue;
                }

                var pool = await library.ForAsync(name, cancellationToken);
                lines.Add(SimilarMoments.Describe(name, now, SimilarMoments.Nearest(pool, now)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogInformation(ex, "AI Trader brief: the similar moments of {Index} could not be read", name);
                lines.Add($"{name}: not available just now.");
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>The recorded minutes that had ended by the moment, oldest first: what the brief's other sections read too.</summary>
    private async Task<IReadOnlyList<LiveBarResponse>> MinutesAsync(string symbol, DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var newestFirst = await live.GetBarsUntilAsync(symbol, "1m", SituationMath.MinutesToRead, asOfUtc, null, cancellationToken);
        return newestFirst.OrderBy(b => b.BarStartUtc).ToList();
    }
}
