using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>
/// The nightly category ranking (<c>mf_scheme_stats</c>): each active growth-option scheme's 1, 3 and 5-year
/// trailing returns to the newest full NAV day, and its place among its peers by each.
/// </summary>
/// <remarks>
/// <para>
/// Light on purpose (the server is a 2-vCPU box shared with the live desk): it reads the NAVs of one day and of three
/// eight-day ranges (each anniversary and the week after it), for every eligible scheme at once, so four reads of
/// one chunk each, and computes in memory. It never reads a whole history.
/// </para>
/// <para>
/// The rules are the report's (<see cref="FundAnalytics.Trailing"/>): a period counts only when the stored history
/// starts on or before its anniversary, and starts on the first NAV on or after the anniversary, within 7 days.
/// Peers: same SEBI category and scheme type (<see cref="MutualFundCategories"/>), same plan, growth option, active,
/// with a NAV on the day. Rank 1 is the highest return; equal returns share a rank.
/// </para>
/// </remarks>
public sealed class MutualFundStatsJob
{
    public static readonly IReadOnlyList<int> Years = [1, 3, 5];

    private readonly TradingDbContext _db;
    private readonly ILogger<MutualFundStatsJob> _logger;

    public MutualFundStatsJob(TradingDbContext db, ILogger<MutualFundStatsJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Ranks every eligible scheme as of the newest full NAV day and replaces the table. Never throws for the database's faults.</summary>
    public async Task<MutualFundJobRun> RunAsync(string trigger, CancellationToken ct)
    {
        var started = Clock();
        try
        {
            var asOf = await MutualFundNavSync.LatestFullDateAsync(_db, ct);
            if (asOf is null) return Run(trigger, started, false, "No NAVs are stored yet.", null, 0);

            var rows = await ComputeAsync(asOf.Value, started, ct);
            var old = await _db.MutualFundSchemeStats.ToListAsync(ct);
            _db.MutualFundSchemeStats.RemoveRange(old);
            _db.MutualFundSchemeStats.AddRange(rows);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();

            string message = $"{rows.Count:N0} schemes ranked as of {FundAnalytics.Day(asOf.Value)}; "
                             + string.Join(", ", Years.Select(y => $"{rows.Count(r => Value(r, y) is not null):N0} with {y} year{(y == 1 ? "" : "s")}"))
                             + $"; {rows.Select(r => (r.PeerKey, r.Plan)).Distinct().Count():N0} peer groups.";
            _logger.LogInformation("Mutual fund ranking ({Trigger}): {Message}", trigger, message);
            return Run(trigger, started, true, message, asOf, rows.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "Mutual fund ranking ({Trigger}) failed: {Error}", trigger, ex.Message);
            return Run(trigger, started, false, $"Failed: {ex.Message}", null, 0);
        }
    }

    private MutualFundJobRun Run(string trigger, DateTime started, bool ok, string message, DateOnly? reached, int rows) => new()
    {
        Trigger = trigger,
        StartedUtc = started,
        FinishedUtc = Clock(),
        Ok = ok,
        Message = MutualFundAnalyticsSettings.Clip(message),
        Reached = reached,
        Rows = rows,
    };

    /// <summary>The rows for <paramref name="asOf"/>: every eligible scheme, its returns and ranks.</summary>
    public async Task<List<MutualFundSchemeStats>> ComputeAsync(DateOnly asOf, DateTime nowUtc, CancellationToken ct)
    {
        var schemes = await _db.MutualFundSchemes.AsNoTracking()
            .Where(s => s.IsActive && s.Option == "growth" && (s.Plan == "direct" || s.Plan == "regular") && s.LastNavDate >= asOf)
            .Select(s => new { s.SchemeCode, s.Plan, s.Category, s.FirstNavDate })
            .ToListAsync(ct);
        var eligible = schemes
            .Select(s => (s.SchemeCode, s.Plan, s.FirstNavDate, Peer: MutualFundCategories.Parse(s.Category).PeerKey))
            .Where(s => s.Peer is not null)
            .ToList();
        var codes = eligible.Select(s => s.SchemeCode).ToList();

        var end = (await NavsAsync(codes, asOf, asOf, ct)).ToDictionary(n => n.Code, n => (double)n.Nav);
        var starts = new Dictionary<int, Dictionary<int, (DateOnly Date, double Nav)>>();
        foreach (int years in Years)
        {
            var target = FundDates.YearsBefore(asOf, years);
            starts[years] = (await NavsAsync(codes, target, target.AddDays(NavSeries.ToleranceDays), ct))
                .GroupBy(n => n.Code)
                .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Date).Select(n => (n.Date, (double)n.Nav)).First());
        }

        var rows = new List<MutualFundSchemeStats>();
        foreach (var s in eligible)
        {
            if (!end.TryGetValue(s.SchemeCode, out double endNav)) continue;
            var row = new MutualFundSchemeStats { SchemeCode = s.SchemeCode, AsOfDate = asOf, PeerKey = s.Peer!, Plan = s.Plan, ComputedUtc = nowUtc };
            foreach (int years in Years)
            {
                var target = FundDates.YearsBefore(asOf, years);
                double? value = null;
                if (s.FirstNavDate is { } first && first <= target && starts[years].TryGetValue(s.SchemeCode, out var start) && start.Date < asOf)
                    value = FundAnalytics.Cagr(start.Nav, start.Date, endNav, asOf);
                Set(row, years, value, null, null);
            }

            rows.Add(row);
        }

        foreach (int years in Years)
        {
            var ranks = Rank(rows.Select(r => (r.SchemeCode, Group: $"{r.PeerKey}#{r.Plan}", Value(r, years))));
            foreach (var row in rows)
            {
                if (ranks.TryGetValue(row.SchemeCode, out var place)) Set(row, years, Value(row, years), place.Rank, place.Peers);
            }
        }

        return rows;
    }

    private async Task<List<(int Code, DateOnly Date, decimal Nav)>> NavsAsync(List<int> codes, DateOnly from, DateOnly to, CancellationToken ct) =>
        (await _db.MutualFundNavs.AsNoTracking()
            .Where(n => n.Date >= from && n.Date <= to && codes.Contains(n.SchemeCode))
            .Select(n => new { n.SchemeCode, n.Date, n.Nav })
            .ToListAsync(ct))
        .Select(n => (n.SchemeCode, n.Date, n.Nav))
        .ToList();

    /// <summary>
    /// Competition ranking within each group, highest value first: 1, 2, 2, 4. Items without a value get no rank
    /// and do not count. Returns each ranked item's rank and its group's count.
    /// </summary>
    public static Dictionary<int, (int Rank, int Peers)> Rank(IEnumerable<(int Code, string Group, double? Value)> items)
    {
        var result = new Dictionary<int, (int Rank, int Peers)>();
        foreach (var group in items.Where(i => i.Value is not null).GroupBy(i => i.Group))
        {
            var values = group.Select(g => g.Value!.Value).ToList();
            foreach (var item in group)
                result[item.Code] = (1 + values.Count(v => v > item.Value!.Value), values.Count);
        }

        return result;
    }

    public static double? Value(MutualFundSchemeStats row, int years) => years switch
    {
        1 => row.Return1y,
        3 => row.Return3y,
        5 => row.Return5y,
        _ => null,
    };

    public static (int? Rank, int? Peers) Place(MutualFundSchemeStats row, int years) => years switch
    {
        1 => (row.Rank1y, row.Peers1y),
        3 => (row.Rank3y, row.Peers3y),
        5 => (row.Rank5y, row.Peers5y),
        _ => (null, null),
    };

    private static void Set(MutualFundSchemeStats row, int years, double? value, int? rank, int? peers)
    {
        switch (years)
        {
            case 1:
                (row.Return1y, row.Rank1y, row.Peers1y) = (value, rank, peers);
                break;
            case 3:
                (row.Return3y, row.Rank3y, row.Peers3y) = (value, rank, peers);
                break;
            case 5:
                (row.Return5y, row.Rank5y, row.Peers5y) = (value, rank, peers);
                break;
        }
    }
}
