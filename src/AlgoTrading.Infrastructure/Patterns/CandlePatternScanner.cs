using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>The timings the scanner runs by. Defaults are what the API ships with.</summary>
public sealed class PatternScanSettings
{
    /// <summary>Time between scans.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How far behind the clock a candle's close is judged, so the last ticks of
    /// its final minute (stamped by the exchange, delivered a moment later) are in
    /// the bar before it is read.
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Scanning continues this long after the close, for the candle that closes with the session.</summary>
    public TimeSpan AfterCloseGrace { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Only candles that closed this recently are sent to Telegram. Older ones —
    /// found after an API restart, or when a rule is added mid-session — are
    /// recorded for the page but would only be noise on a phone.
    /// </summary>
    public TimeSpan NotifyWindow { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>A symbol with no 1-minute bar for this long, in session, is reported as not receiving data.</summary>
    public TimeSpan StaleBarsAfter { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>The metadata stored with every pattern alert (camelCase JSON).</summary>
public sealed record PatternEventMetadata(
    string Kind,
    string Pattern,
    string PatternName,
    string Direction,
    int Timeframe,
    DateTime BarStartUtc,
    DateTime BarEndUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    int MinutesInBar,
    int MinutesExpected,
    IReadOnlyList<string> Rules,
    bool Notify,
    string? NotifySkippedReason)
{
    public const string KindValue = "candle-pattern";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PatternEventMetadata? TryRead(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<PatternEventMetadata>(json, Json);
            return meta?.Kind == KindValue ? meta : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>How one watched symbol stood at the last scan.</summary>
public sealed record PatternSymbolState(
    string Symbol,
    string Exchange,
    bool ExchangeInSession,
    int BarsToday,
    DateTime? LastBarUtc,
    string? Problem);

/// <summary>A pattern alert this scan recorded for the first time.</summary>
public sealed record RecordedPattern(long EventId, PatternOccurrence Occurrence, bool Notify);

/// <summary>What one scan found and did.</summary>
public sealed record PatternScanOutcome(
    DateTime ScannedUtc,
    int RuleCount,
    int WatchCount,
    IReadOnlyList<PatternSymbolState> Symbols,
    IReadOnlyList<string> Unresolved,
    int ClosedCandlesRead,
    IReadOnlyList<RecordedPattern> Recorded);

/// <summary>The candle still forming for one watched symbol and timeframe.</summary>
public sealed record FormingCandle(
    string Symbol,
    int TimeframeMinutes,
    string Exchange,
    bool ExchangeInSession,
    DateTime? BarStartUtc,
    DateTime? BarEndUtc,
    TimeframeBar? Bar,
    int MinutesElapsed,
    int MinutesExpected,
    IReadOnlyList<PatternHit> WouldBe,
    IReadOnlyList<PatternHit> LastClosed,
    DateTime? LastClosedStartUtc);

/// <summary>
/// Finds newly closed candles on every watched symbol, recognises patterns on
/// them, and records one alert per (symbol, timeframe, candle, pattern).
/// </summary>
/// <remarks>
/// <para>
/// Idempotent by construction: each alert carries a <see cref="AlertEvent.DedupeKey"/>
/// with a unique index, and a scan first reads the keys the session already has.
/// Re-reading a whole session every scan is therefore safe, and is what makes
/// an API restart at 11:00 pick up exactly where it left off.
/// </para>
/// <para>
/// The scanner never sends anything itself. It says which new alerts deserve a
/// notification (a rule asked for Telegram, and the candle closed within
/// <see cref="PatternScanSettings.NotifyWindow"/>); the hosted service owns
/// delivery and its rate limit.
/// </para>
/// </remarks>
public sealed class CandlePatternScanner
{
    public const string Source = "patterns";

    private readonly TradingDbContext _db;
    private readonly IMarketSessionService _sessions;
    private readonly PatternWatchPlanner _planner;
    private readonly ILogger<CandlePatternScanner> _logger;

    public CandlePatternScanner(
        TradingDbContext db,
        IMarketSessionService sessions,
        PatternWatchPlanner planner,
        ILogger<CandlePatternScanner> logger)
    {
        _db = db;
        _sessions = sessions;
        _planner = planner;
        _logger = logger;
    }

    private sealed record ExchangeSession(string Exchange, SessionWindow Window, bool TradingDay, bool InSession);

    public async Task<PatternScanOutcome> ScanAsync(DateTime nowUtc, PatternScanSettings settings, CancellationToken cancellationToken = default)
    {
        var (ruleCount, plan) = await PlanAsync(nowUtc, cancellationToken);
        var sessions = SessionsFor(plan.Symbols, nowUtc, settings.AfterCloseGrace);

        var scannable = plan.Symbols.Where(s => sessions.TryGetValue(CandlePatternRules.ExchangeOf(s), out var x) && x.InSession).ToList();
        var bars = await LoadMinuteBarsAsync(scannable, sessions, cancellationToken);

        var asOf = nowUtc - settings.Settle;
        var occurrences = new List<(PatternOccurrence Occurrence, PatternWatch Watch)>();
        int closedRead = 0;

        foreach (var watch in plan.Watches)
        {
            if (!bars.TryGetValue(watch.Symbol, out var minutes)) continue;
            var session = sessions[CandlePatternRules.ExchangeOf(watch.Symbol)];
            var closed = SessionBarAggregator.Aggregate(minutes, session.Window, watch.TimeframeMinutes, asOf)
                .Where(b => b.IsClosed)
                .ToList();
            closedRead += closed.Count;

            for (int i = 0; i < closed.Count; i++)
            {
                foreach (var hit in CandlePatternDetector.Detect(closed, i))
                {
                    if (!watch.Patterns.Contains(hit.Pattern)) continue;
                    var b = closed[i];
                    occurrences.Add((new PatternOccurrence(
                        watch.Symbol, watch.TimeframeMinutes, b.StartUtc, b.EndUtc,
                        b.Open, b.High, b.Low, b.Close, b.MinutesWithData, b.MinutesExpected,
                        hit.Pattern, hit.Direction), watch));
                }
            }
        }

        var recorded = await RecordAsync(occurrences, sessions, nowUtc, settings, cancellationToken);

        return new PatternScanOutcome(
            nowUtc,
            ruleCount,
            plan.Watches.Count,
            SymbolStates(plan.Symbols, sessions, bars, nowUtc, settings),
            plan.Unresolved,
            closedRead,
            recorded);
    }

    /// <summary>
    /// The candle forming right now on every watched symbol and timeframe, and
    /// the patterns it would complete if it closed at this instant. For the page
    /// only: nothing here is recorded or sent.
    /// </summary>
    public async Task<IReadOnlyList<FormingCandle>> GetFormingAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var (_, plan) = await PlanAsync(nowUtc, cancellationToken);
        var sessions = SessionsFor(plan.Symbols, nowUtc, TimeSpan.Zero);
        var open = plan.Symbols.Where(s => sessions.TryGetValue(CandlePatternRules.ExchangeOf(s), out var x) && x.InSession).ToList();
        var bars = await LoadMinuteBarsAsync(open, sessions, cancellationToken);

        var result = new List<FormingCandle>();
        foreach (var watch in plan.Watches)
        {
            var exchange = CandlePatternRules.ExchangeOf(watch.Symbol);
            if (!sessions.TryGetValue(exchange, out var session) || !session.InSession)
            {
                result.Add(new FormingCandle(watch.Symbol, watch.TimeframeMinutes, exchange, false, null, null, null, 0, 0, [], [], null));
                continue;
            }

            var bucket = SessionBarAggregator.CurrentBucket(session.Window, watch.TimeframeMinutes, nowUtc);
            var all = SessionBarAggregator.Aggregate(bars.GetValueOrDefault(watch.Symbol) ?? [], session.Window, watch.TimeframeMinutes, nowUtc);

            int formingAt = bucket is { } bk ? IndexOf(all, b => b.Index == bk.Index && !b.IsClosed) : -1;
            var forming = formingAt >= 0 ? all[formingAt] : null;
            var wouldBe = formingAt >= 0
                ? CandlePatternDetector.Detect(all, formingAt).Where(h => watch.Patterns.Contains(h.Pattern)).ToList()
                : [];

            int lastClosedAt = LastIndexOf(all, b => b.IsClosed);
            var lastClosed = lastClosedAt >= 0
                ? CandlePatternDetector.Detect(all, lastClosedAt).Where(h => watch.Patterns.Contains(h.Pattern)).ToList()
                : [];

            int expected = bucket is { } bx ? (int)Math.Round((bx.EndUtc - bx.StartUtc).TotalMinutes) : 0;
            int elapsed = bucket is { } be ? Math.Clamp((int)Math.Floor((nowUtc - be.StartUtc).TotalMinutes), 0, expected) : 0;

            result.Add(new FormingCandle(
                watch.Symbol, watch.TimeframeMinutes, exchange, bucket is not null,
                bucket?.StartUtc, bucket?.EndUtc, forming, elapsed, expected, wouldBe,
                lastClosed, lastClosedAt >= 0 ? all[lastClosedAt].StartUtc : null));
        }

        return result;
    }

    /// <summary>The enabled rules, resolved to concrete symbols as of today.</summary>
    public async Task<(int RuleCount, PatternWatchPlan Plan)> PlanAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var rows = await _db.CandlePatternRules.AsNoTracking().ToListAsync(cancellationToken);
        var rules = rows.Select(CandlePatternRules.Parse).ToList();
        var plan = await _planner.PlanAsync(rules, IstTime.DateOf(nowUtc), cancellationToken);
        return (rules.Count(r => r.IsEnabled), plan);
    }

    private Dictionary<string, ExchangeSession> SessionsFor(IEnumerable<string> symbols, DateTime nowUtc, TimeSpan afterCloseGrace)
    {
        var result = new Dictionary<string, ExchangeSession>(StringComparer.Ordinal);
        foreach (var exchange in symbols.Select(CandlePatternRules.ExchangeOf).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var info = _sessions.GetSessionInfo(nowUtc, exchange, exchange == "MCX" ? "COM" : "CM");
                var window = new SessionWindow(info.SessionOpenUtc, info.SessionCloseUtc);
                bool inSession = info.IsTradingDay && nowUtc >= window.OpenUtc && nowUtc < window.CloseUtc + afterCloseGrace;
                result[exchange] = new ExchangeSession(exchange, window, info.IsTradingDay, inSession);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                _logger.LogWarning("Pattern alerts: no session rules for exchange {Exchange}; its symbols are skipped.", exchange);
            }
        }

        return result;
    }

    private async Task<Dictionary<string, List<MinuteBar>>> LoadMinuteBarsAsync(
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, ExchangeSession> sessions,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<MinuteBar>>(StringComparer.Ordinal);
        if (symbols.Count == 0) return result;

        var windows = symbols.Select(s => sessions[CandlePatternRules.ExchangeOf(s)].Window).ToList();
        var from = windows.Min(w => w.OpenUtc);
        var to = windows.Max(w => w.CloseUtc);

        var rows = await _db.LiveBars.AsNoTracking()
            .Where(b => b.Resolution == ResolutionCodes.LiveBarResolution
                        && symbols.Contains(b.Symbol)
                        && b.BarStartUtc >= from && b.BarStartUtc < to)
            .Select(b => new { b.Symbol, b.BarStartUtc, b.Open, b.High, b.Low, b.Close })
            .ToListAsync(cancellationToken);

        foreach (var symbol in symbols) result[symbol] = [];
        foreach (var r in rows.OrderBy(r => r.BarStartUtc))
        {
            result[r.Symbol].Add(new MinuteBar(DateTime.SpecifyKind(r.BarStartUtc, DateTimeKind.Utc), r.Open, r.High, r.Low, r.Close));
        }

        return result;
    }

    private async Task<List<RecordedPattern>> RecordAsync(
        List<(PatternOccurrence Occurrence, PatternWatch Watch)> occurrences,
        IReadOnlyDictionary<string, ExchangeSession> sessions,
        DateTime nowUtc,
        PatternScanSettings settings,
        CancellationToken cancellationToken)
    {
        if (occurrences.Count == 0) return [];

        // Every alert is stamped with its candle's close, which is inside the
        // session, so the earliest open bounds the keys this session can hold.
        var since = sessions.Values.Where(s => s.InSession).Select(s => s.Window.OpenUtc).DefaultIfEmpty(nowUtc.AddDays(-1)).Min();
        var existing = (await _db.AlertEvents.AsNoTracking()
                .Where(e => e.Source == Source && e.OccurredUtc >= since && e.DedupeKey != null)
                .Select(e => e.DedupeKey!)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = new List<(AlertEvent Row, PatternOccurrence Occurrence, bool Notify)>();
        foreach (var (o, watch) in occurrences)
        {
            if (!existing.Add(o.DedupeKey)) continue;

            bool ruleNotifies = watch.NotifyPatterns.Contains(o.Pattern);
            bool recent = o.BarEndUtc >= nowUtc - settings.NotifyWindow;
            bool notify = ruleNotifies && recent;
            string? skipped = !ruleNotifies ? "no rule sends this pattern to Telegram"
                : !recent ? "the candle closed before the scanner saw it (restart or new rule)"
                : null;

            var info = CandlePatternCatalog.Info(o.Pattern);
            var row = new AlertEvent
            {
                OccurredUtc = o.BarEndUtc,
                Source = Source,
                Underlying = Truncate(UnderlyingCatalog.InferUnderlying(o.Symbol), 40),
                Symbol = o.Symbol,
                Severity = "info",
                Title = Truncate(PatternAlertText.Title(o), 200),
                Message = Truncate(PatternAlertText.Message(o), 1000),
                MetadataJson = JsonSerializer.Serialize(new PatternEventMetadata(
                    PatternEventMetadata.KindValue,
                    info.Key,
                    info.Name,
                    CandlePatternCatalog.DirectionKey(o.Direction),
                    o.TimeframeMinutes,
                    o.BarStartUtc,
                    o.BarEndUtc,
                    o.Open, o.High, o.Low, o.Close,
                    o.MinutesWithData,
                    o.MinutesExpected,
                    watch.RuleNames.ToList(),
                    notify,
                    skipped), PatternEventMetadata.Json),
                DeliveredToTelegram = false,
                DedupeKey = o.DedupeKey,
            };
            fresh.Add((row, o, notify));
        }

        if (fresh.Count == 0) return [];

        _db.AlertEvents.AddRange(fresh.Select(f => f.Row));
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return fresh.Select(f => new RecordedPattern(f.Row.Id, f.Occurrence, f.Notify)).ToList();
        }
        catch (DbUpdateException ex)
        {
            // Only another writer holding the same key gets here (a second API
            // pointed at this database). Fall back to one row at a time and keep
            // whichever rows are still new; the other writer owns the rest,
            // including their notification.
            _logger.LogWarning(ex, "Pattern alerts: a batch of {Count} collided with existing keys; retrying one by one.", fresh.Count);
            foreach (var f in fresh) _db.Entry(f.Row).State = EntityState.Detached;

            var kept = new List<RecordedPattern>();
            foreach (var f in fresh)
            {
                f.Row.Id = 0;
                _db.AlertEvents.Add(f.Row);
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    kept.Add(new RecordedPattern(f.Row.Id, f.Occurrence, f.Notify));
                }
                catch (DbUpdateException)
                {
                    _db.Entry(f.Row).State = EntityState.Detached;
                }
            }

            return kept;
        }
    }

    private static List<PatternSymbolState> SymbolStates(
        IReadOnlyList<string> symbols,
        IReadOnlyDictionary<string, ExchangeSession> sessions,
        IReadOnlyDictionary<string, List<MinuteBar>> bars,
        DateTime nowUtc,
        PatternScanSettings settings)
    {
        var states = new List<PatternSymbolState>();
        foreach (var symbol in symbols)
        {
            var exchange = CandlePatternRules.ExchangeOf(symbol);
            if (!sessions.TryGetValue(exchange, out var session))
            {
                states.Add(new PatternSymbolState(symbol, exchange, false, 0, null, $"no session rules for {exchange}"));
                continue;
            }

            if (!session.InSession)
            {
                states.Add(new PatternSymbolState(symbol, exchange, false, 0, null, null));
                continue;
            }

            var minutes = bars.GetValueOrDefault(symbol) ?? [];
            DateTime? last = minutes.Count > 0 ? minutes[^1].StartUtc : null;
            var liveUntil = nowUtc < session.Window.CloseUtc ? nowUtc : session.Window.CloseUtc;

            string? problem = null;
            if (liveUntil - session.Window.OpenUtc >= settings.StaleBarsAfter)
            {
                if (last is null)
                    problem = "no live bars — not streamed by any feed";
                else if (liveUntil - last.Value.AddMinutes(1) >= settings.StaleBarsAfter)
                    problem = $"no live bars since {PatternAlertText.IstClock(last.Value)} IST";
            }

            states.Add(new PatternSymbolState(symbol, exchange, true, minutes.Count, last, problem));
        }

        return states;
    }

    private static int IndexOf(IReadOnlyList<TimeframeBar> bars, Func<TimeframeBar, bool> predicate)
    {
        for (int i = 0; i < bars.Count; i++) if (predicate(bars[i])) return i;
        return -1;
    }

    private static int LastIndexOf(IReadOnlyList<TimeframeBar> bars, Func<TimeframeBar, bool> predicate)
    {
        for (int i = bars.Count - 1; i >= 0; i--) if (predicate(bars[i])) return i;
        return -1;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
