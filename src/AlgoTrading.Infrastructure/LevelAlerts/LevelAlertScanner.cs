using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.LevelAlerts;

/// <summary>A fire this scan recorded, with what delivery needs to know about its owner.</summary>
public sealed record RecordedLevelFire(LevelAlertFire Fire, bool OwnerIsAdmin, bool Notify);

/// <summary>What one scan found and did.</summary>
public sealed record LevelScanOutcome(
    DateTime ScannedUtc,
    int ActiveRules,
    IReadOnlyList<LevelReading> Readings,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<RecordedLevelFire> Fired,
    int Rearmed);

/// <summary>
/// One look at every active level alert: read each watched target once, compare
/// the reading with the last one, and record what crossed.
/// </summary>
/// <remarks>
/// <para>
/// Batched by target, not by rule: twenty rules on NIFTY are one row of
/// <c>live_quotes_latest</c>, and every price is read in one query. A chain is
/// read once per underlying, no more than every <see cref="PcrInterval"/>, and
/// added up only when a new capture has arrived. Nothing at all is read while
/// no exchange is in session.
/// </para>
/// <para>
/// A rule is written only when it changes: it fires, or a repeating rule arms
/// again. The write re-reads the rule and skips it if its owner saved it,
/// paused it or deleted it since this scan read it; the next scan judges the
/// rule as it now stands.
/// </para>
/// </remarks>
public sealed class LevelAlertScanner
{
    /// <summary>The most rules one user may hold, active or not.</summary>
    public const int MaxRulesPerUser = 50;

    /// <summary>The most distinct symbols read in one scan, a bound on its one query; past it the newest rules wait.</summary>
    public const int MaxPriceTargets = 500;

    /// <summary>The most chains read in one scan.</summary>
    public const int MaxPcrTargets = 20;

    /// <summary>How often the chains are looked at. The recorder captures the nearest expiry about once a minute.</summary>
    public static readonly TimeSpan PcrInterval = TimeSpan.FromSeconds(15);

    private readonly TradingDbContext _db;
    private readonly LevelAlertReader _reader;
    private readonly LevelAlertState _state;
    private readonly ILogger<LevelAlertScanner> _logger;

    public LevelAlertScanner(TradingDbContext db, LevelAlertReader reader, LevelAlertState state, ILogger<LevelAlertScanner> logger)
    {
        _db = db;
        _reader = reader;
        _state = state;
        _logger = logger;
    }

    private sealed record WatchedRule(
        long Id, long UserId, bool OwnerIsAdmin, string Kind, string Target, string Condition, decimal Level,
        bool Repeat, decimal? RearmBy, string? Note, bool Notify, bool Armed, DateTime UpdatedUtc);

    private sealed record Change(WatchedRule Rule, bool Fire, LevelSample Previous, LevelSample Current, string? Context);

    public async Task<LevelScanOutcome> ScanAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // The calendar is in memory: outside every session the scan costs no query at all.
        if (!_reader.AnyExchangeOpen(nowUtc))
        {
            var last = _state.Snapshot;
            var closed = ClosedReadings(last.Readings);
            _state.Observed(nowUtc, last.ActiveRules, closed, []);
            return new LevelScanOutcome(nowUtc, last.ActiveRules, closed, [], [], 0);
        }

        var today = IstTime.DateOf(nowUtc);
        var rules = await (
                from r in _db.LevelAlertRules.AsNoTracking()
                join u in _db.AppUsers.AsNoTracking() on r.UserId equals u.Id
                where r.Status == LevelAlertStatus.Active && (r.ExpiresOn == null || r.ExpiresOn >= today) && u.IsActive
                orderby r.Id
                select new WatchedRule(r.Id, r.UserId, u.Role == UserRoles.Admin, r.Kind, r.Target, r.Condition, r.Level,
                    r.Repeat, r.RearmBy, r.Note, r.Notify, r.Armed, r.UpdatedUtc))
            .ToListAsync(cancellationToken);

        var byTarget = rules.GroupBy(r => LevelAlertReader.KeyOf(r.Kind, r.Target), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _state.KeepOnly(byTarget.Keys.ToHashSet(StringComparer.Ordinal));

        var skipped = new List<string>();
        var priceTargets = Distinct(rules, LevelAlertKinds.Price, MaxPriceTargets, "symbols", skipped);
        var pcrTargets = Distinct(rules, LevelAlertKinds.Pcr, MaxPcrTargets, "chains", skipped);

        var sessions = new Dictionary<string, LevelSession>(StringComparer.Ordinal);
        foreach (var s in priceTargets) sessions[LevelAlertReader.KeyOf(LevelAlertKinds.Price, s)] = _reader.SessionOf(LevelAlertKinds.Price, s, nowUtc);
        foreach (var u in pcrTargets) sessions[LevelAlertReader.KeyOf(LevelAlertKinds.Pcr, u)] = _reader.SessionOf(LevelAlertKinds.Pcr, u, nowUtc);

        var readings = new List<LevelReading>();

        // Prices: one query for every symbol whose exchange is open.
        var openSymbols = priceTargets.Where(s => sessions[LevelAlertReader.KeyOf(LevelAlertKinds.Price, s)].Open).ToList();
        var prices = await _reader.PricesAsync(openSymbols,
            openSymbols.ToDictionary(s => s, s => sessions[LevelAlertReader.KeyOf(LevelAlertKinds.Price, s)], StringComparer.Ordinal),
            nowUtc, cancellationToken);
        foreach (var s in priceTargets)
        {
            readings.Add(prices.TryGetValue(s, out var reading)
                ? reading
                : NotRead(LevelAlertKinds.Price, s, sessions[LevelAlertReader.KeyOf(LevelAlertKinds.Price, s)]));
        }

        // Chains: when due, one look per underlying; between looks, the last reading stands.
        bool pcrDue = _state.PcrDue(nowUtc, PcrInterval);
        var lastReadings = _state.Snapshot.Readings.ToDictionary(r => LevelAlertReader.KeyOf(r.Kind, r.Target), StringComparer.Ordinal);
        foreach (var u in pcrTargets)
        {
            var key = LevelAlertReader.KeyOf(LevelAlertKinds.Pcr, u);
            var session = sessions[key];
            if (!session.Open)
            {
                readings.Add(NotRead(LevelAlertKinds.Pcr, u, session));
            }
            else if (pcrDue)
            {
                readings.Add(await _reader.PcrAsync(u, session, nowUtc, _state.Previous(key), valueWhenStale: false, cancellationToken));
            }
            else if (lastReadings.TryGetValue(key, out var last))
            {
                readings.Add(last);
            }
        }

        if (pcrDue && pcrTargets.Count > 0) _state.PcrRead(nowUtc);

        // Compare each fresh reading with the last, rule by rule.
        var changes = new List<Change>();
        var remember = new List<(string Key, LevelSample Sample)>();
        foreach (var reading in readings.Where(r => r.IsLive))
        {
            var key = LevelAlertReader.KeyOf(reading.Kind, reading.Target);
            var current = new LevelSample(reading.Value!.Value, reading.AsOfUtc!.Value, sessions[key].Day);
            var previous = _state.Previous(key);
            if (previous is { } same && same.Session == current.Session && current.AsOfUtc <= same.AsOfUtc) continue;   // the reading it already has
            remember.Add((key, current));
            if (previous is not { } prev || !LevelCross.Comparable(prev, current)) continue;                           // the first of its session: a baseline

            foreach (var rule in byTarget.GetValueOrDefault(key) ?? [])
            {
                if (!LevelCross.TryParse(rule.Condition, out var condition)) continue;
                var gap = LevelCross.GapFor(rule.Kind, rule.Level, rule.RearmBy);
                var step = LevelCross.Step(condition, rule.Level, gap, rule.Armed, prev.Value, current.Value);
                if (step.Fired || step.Armed != rule.Armed) changes.Add(new Change(rule, step.Fired, prev, current, reading.Context));
            }
        }

        var (fired, rearmed) = changes.Count > 0 ? await ApplyAsync(changes, nowUtc, cancellationToken) : ([], 0);

        // Only once the fires are stored: a scan whose write failed leaves the
        // last readings as they were, and the next one sees the same cross.
        foreach (var (key, sample) in remember) _state.Remember(key, sample);
        _state.Observed(nowUtc, rules.Count, readings, skipped);

        return new LevelScanOutcome(nowUtc, rules.Count, readings, skipped, fired, rearmed);
    }

    /// <summary>Writes the fires and re-arms, each only if the rule is still as this scan read it.</summary>
    private async Task<(IReadOnlyList<RecordedLevelFire> Fired, int Rearmed)> ApplyAsync(
        List<Change> changes,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var ids = changes.Select(c => c.Rule.Id).Distinct().ToList();
        var rows = await _db.LevelAlertRules.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, cancellationToken);

        var fired = new List<RecordedLevelFire>();
        int rearmed = 0;
        foreach (var change in changes)
        {
            var rule = change.Rule;
            if (!rows.TryGetValue(rule.Id, out var row)) continue;   // deleted since it was read
            if (row.UpdatedUtc != rule.UpdatedUtc || row.Status != LevelAlertStatus.Active || row.Armed != rule.Armed)
            {
                _logger.LogDebug("Level alert {RuleId} changed during the scan; it is judged again at the next one.", rule.Id);
                continue;
            }

            if (!change.Fire)
            {
                row.Armed = true;
                rearmed++;
                continue;
            }

            row.Armed = false;
            row.FireCount++;
            row.LastFiredUtc = nowUtc;
            if (!row.Repeat) row.Status = LevelAlertStatus.Fired;

            var fire = new LevelAlertFire
            {
                RuleId = rule.Id,
                UserId = rule.UserId,
                FiredUtc = nowUtc,
                Kind = rule.Kind,
                Target = rule.Target,
                Condition = rule.Condition,
                Level = rule.Level,
                Value = change.Current.Value,
                ValueAsOfUtc = change.Current.AsOfUtc,
                PreviousValue = change.Previous.Value,
                PreviousAsOfUtc = change.Previous.AsOfUtc,
                Title = LevelAlertText.Title(rule.Kind, rule.Target, rule.Condition, rule.Level),
                Message = LevelAlertText.Message(rule.Kind, rule.Target, rule.Condition, rule.Level, change.Previous, change.Current, change.Context, rule.Note),
                TelegramNote = !rule.OwnerIsAdmin ? "A trader's rule: shown in the console only."
                    : !rule.Notify ? "Telegram is off for this rule."
                    : null,
            };
            _db.LevelAlertFires.Add(fire);
            fired.Add(new RecordedLevelFire(fire, rule.OwnerIsAdmin, rule.Notify));
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (fired, rearmed);
    }

    /// <summary>The distinct targets of one kind in rule order, at most <paramref name="max"/>; the rest are named.</summary>
    private static List<string> Distinct(List<WatchedRule> rules, string kind, int max, string what, List<string> skipped)
    {
        var all = rules.Where(r => r.Kind == kind).Select(r => r.Target).Distinct(StringComparer.Ordinal).ToList();
        if (all.Count > max) skipped.Add($"{all.Count - max} {what} past the first {max} are not watched: {string.Join(", ", all.Skip(max).Take(5))}{(all.Count - max > 5 ? "…" : string.Empty)}");
        return all.Take(max).ToList();
    }

    private static LevelReading NotRead(string kind, string target, LevelSession session) => session.Known
        ? new LevelReading(kind, target, session.Exchange, false, null, null, LevelReading.Closed, $"{session.Exchange} is not in session")
        : new LevelReading(kind, target, session.Exchange, false, null, null, LevelReading.UnknownExchange,
            $"no session hours for exchange '{session.Exchange}'; the rule is never read");

    /// <summary>Outside every session the page still lists what is watched, each marked closed.</summary>
    private static IReadOnlyList<LevelReading> ClosedReadings(IReadOnlyList<LevelReading> last) =>
        last.Select(r => r.State == LevelReading.UnknownExchange ? r : r with { InSession = false, State = LevelReading.Closed, Detail = $"{r.Exchange} is not in session" }).ToList();
}
