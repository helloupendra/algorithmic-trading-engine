using System.Collections.Concurrent;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>The metadata stored with every indicator alert (camelCase JSON).</summary>
/// <param name="Rule">The rule's key, "rsi-above(14,70)".</param>
/// <param name="RuleName">"rsi-above".</param>
/// <param name="What">"RSI(14) crossed above 70".</param>
/// <param name="Direction">"up" or "down".</param>
/// <param name="Values">The numbers quoted in the message, rounded for display ("rsi", "rsiBefore", "emaFast", …).</param>
/// <param name="ConfigLines">The config file's lines that watch this symbol and timeframe.</param>
/// <param name="CoolingSinceUtc">Set when it fell inside the cooldown of the alert that closed then.</param>
public sealed record IndicatorEventMetadata(
    string Kind,
    string Rule,
    string RuleName,
    string What,
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
    IReadOnlyDictionary<string, decimal> Values,
    IReadOnlyList<int> ConfigLines,
    bool Notify,
    string? NotifySkippedReason,
    DateTime? CoolingSinceUtc)
{
    public const string KindValue = "indicator";

    public static IndicatorEventMetadata? TryRead(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<IndicatorEventMetadata>(json, PatternEventMetadata.Json);
            return meta?.Kind == KindValue ? meta : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where one rule stands on one watched symbol and timeframe, for the page.</summary>
/// <param name="State">"ready", "warming up", "waiting" (VWAP before anything traded) or "skipped" (VWAP on an index).</param>
public sealed record IndicatorRuleState(string Rule, string Label, string State, string? Detail);

/// <summary>One watched symbol and timeframe at the last scan.</summary>
/// <param name="HistoryCandles">Closed candles read from earlier sessions (the warm-up).</param>
/// <param name="TodayCandles">Closed candles today.</param>
public sealed record IndicatorWatchState(
    string Symbol,
    int TimeframeMinutes,
    string Exchange,
    bool ExchangeInSession,
    int HistoryCandles,
    int TodayCandles,
    DateTime? LastBarUtc,
    IReadOnlyList<IndicatorRuleState> Rules,
    string? Problem);

/// <summary>An indicator alert this scan recorded for the first time.</summary>
public sealed record RecordedIndicator(long EventId, IndicatorOccurrence Occurrence, bool Notify);

/// <summary>What one scan found and did.</summary>
public sealed record IndicatorScanOutcome(
    DateTime ScannedUtc,
    int LineCount,
    IReadOnlyList<IndicatorWatchState> Watches,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<RecordedIndicator> Recorded);

/// <summary>Every rule on one symbol and timeframe, merged across the config's lines.</summary>
public sealed class IndicatorWatch
{
    public required string Symbol { get; init; }
    public required int TimeframeMinutes { get; init; }

    /// <summary>The rules computed here, in the catalog's order.</summary>
    public List<IndicatorRule> Rules { get; } = [];

    /// <summary>Rules at least one line sends to Telegram (the others are page-only).</summary>
    public HashSet<string> NotifyRules { get; } = new(StringComparer.Ordinal);

    /// <summary>Rules a line asks for that cannot be computed here, and why (VWAP on an index).</summary>
    public Dictionary<string, (IndicatorRule Rule, string Reason)> Skipped { get; } = new(StringComparer.Ordinal);

    public SortedSet<int> Lines { get; } = [];
}

/// <summary>
/// The earlier sessions' candles each watch warms up on, kept for the day:
/// they cannot change once the day has begun, and reading two weeks of 1-minute
/// bars every twenty seconds would be a waste of the database.
/// </summary>
public sealed class IndicatorHistoryCache
{
    private readonly ConcurrentDictionary<(string Symbol, int Timeframe, DateOnly Day, int Warmup), IReadOnlyList<TimeframeBar>> _candles = new();

    public bool TryGet(string symbol, int timeframe, DateOnly day, int warmup, out IReadOnlyList<TimeframeBar> candles)
    {
        if (_candles.TryGetValue((symbol, timeframe, day, warmup), out var found))
        {
            candles = found;
            return true;
        }

        candles = [];
        return false;
    }

    public void Set(string symbol, int timeframe, DateOnly day, int warmup, IReadOnlyList<TimeframeBar> candles)
    {
        // Yesterday's warm-ups are no use today.
        foreach (var key in _candles.Keys.Where(k => k.Day != day)) _candles.TryRemove(key, out _);
        _candles[(symbol, timeframe, day, warmup)] = candles;
    }
}

/// <summary>
/// Computes RSI, EMA crosses, Supertrend and VWAP on the closed candles of
/// every symbol the indicator config watches, and records one alert per
/// (symbol, timeframe, candle, rule).
/// </summary>
/// <remarks>
/// <para>
/// The candle-pattern scanner's sibling, and built the same way: the same live
/// 1-minute bars, the same session-aligned candles
/// (<see cref="SessionBarAggregator"/>), the same session and holiday gate, the
/// same <c>alert_events</c> table with a unique key per alert, so re-reading
/// the whole day every scan is safe and a restart picks up where it left off.
/// </para>
/// <para>
/// Warm-up. An EMA(21) or an RSI(14) read on the day's first candles alone
/// would be wrong until dozens of candles had passed, and the first alerts of
/// the day would be the wrong ones. So each watch reads the closing candles of
/// earlier sessions first (<see cref="IndicatorAlertConfig.WarmupCandles"/>,
/// built from the stored live 1-minute bars exactly as today's are), and a rule
/// alerts only once it has read the candles it needs
/// (<see cref="IndicatorRule.SettleCandles"/>). Until then the page says
/// "warming up, 40 of 84 candles".
/// </para>
/// <para>
/// No repainting: only closed candles are read, a settle delay behind the clock
/// (<see cref="PatternScanSettings.Settle"/>), and see <see cref="IndicatorEvaluator"/>.
/// </para>
/// </remarks>
public sealed class IndicatorAlertScanner
{
    public const string Source = "indicators";

    /// <summary>Longest history read for a warm-up, in calendar days (an hourly warm-up would otherwise read months).</summary>
    public const int MaxLookbackDays = 45;

    /// <summary>
    /// The most (symbol, timeframe) pairs scanned. Each reads its warm-up with
    /// a query of its own, all of them in the first scan of the day at 09:15,
    /// the busiest minute on the desk, and again after any restart; a line
    /// such as "recording-stocks 5,15 ema-cross" had no limit. The same figure
    /// as the candle-pattern rules' symbol cap. Pairs past it are left out, in
    /// the config's order, and the page says so.
    /// </summary>
    public const int MaxWatches = 200;

    /// <summary>An NSE session, for estimating how many days hold a warm-up; MCX sessions are longer, so this over-reads for them.</summary>
    private const double SessionMinutes = 375;

    private readonly TradingDbContext _db;
    private readonly IMarketSessionService _sessions;
    private readonly PatternWatchPlanner _planner;
    private readonly IndicatorHistoryCache _history;
    private readonly ILogger<IndicatorAlertScanner> _logger;

    public IndicatorAlertScanner(
        TradingDbContext db,
        IMarketSessionService sessions,
        PatternWatchPlanner planner,
        IndicatorHistoryCache history,
        ILogger<IndicatorAlertScanner> logger)
    {
        _db = db;
        _sessions = sessions;
        _planner = planner;
        _history = history;
        _logger = logger;
    }

    private sealed record ExchangeSession(SessionWindow Window, bool InSession);

    public async Task<IndicatorScanOutcome> ScanAsync(
        DateTime nowUtc,
        IndicatorAlertConfig config,
        PatternScanSettings settings,
        CancellationToken cancellationToken = default)
    {
        var today = IstTime.DateOf(nowUtc);
        var (watches, unresolved) = await PlanAsync(config, today, cancellationToken);
        var symbols = watches.Select(w => w.Symbol).Distinct(StringComparer.Ordinal).ToList();
        var sessions = SessionsFor(symbols, nowUtc, settings.AfterCloseGrace);

        var scannable = symbols.Where(s => sessions.TryGetValue(CandlePatternRules.ExchangeOf(s), out var x) && x.InSession).ToList();
        var minutes = await LoadTodayAsync(scannable, sessions, cancellationToken);

        var asOf = nowUtc - settings.Settle;
        var states = new List<IndicatorWatchState>();
        var found = new List<(IndicatorOccurrence Occurrence, CooledHit Hit, IndicatorWatch Watch)>();

        // Every alert is stamped with its candle's close, inside today's
        // session, so the earliest open bounds the keys today can hold.
        var since = sessions.Values.Where(s => s.InSession).Select(s => s.Window.OpenUtc).DefaultIfEmpty(nowUtc.AddDays(-1)).Min();
        var (recordedKeys, deliveredKeys) = scannable.Count > 0
            ? await TodayAsync(since, cancellationToken)
            : (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        foreach (var watch in watches)
        {
            var exchange = CandlePatternRules.ExchangeOf(watch.Symbol);
            var skipped = watch.Skipped.Values
                .Select(s => new IndicatorRuleState(s.Rule.Key, s.Rule.Label, "skipped", s.Reason))
                .ToList();

            if (!sessions.TryGetValue(exchange, out var session))
            {
                states.Add(new IndicatorWatchState(watch.Symbol, watch.TimeframeMinutes, exchange, false, 0, 0, null, skipped,
                    $"no session rules for {exchange}"));
                continue;
            }

            if (!session.InSession)
            {
                states.Add(new IndicatorWatchState(watch.Symbol, watch.TimeframeMinutes, exchange, false, 0, 0, null, skipped, null));
                continue;
            }

            var todayMinutes = minutes.GetValueOrDefault(watch.Symbol) ?? [];
            var closedToday = SessionBarAggregator.Aggregate(todayMinutes, session.Window, watch.TimeframeMinutes, asOf)
                .Where(b => b.IsClosed)
                .ToList();
            var history = await HistoryAsync(watch.Symbol, exchange, watch.TimeframeMinutes, today, config.WarmupCandles, cancellationToken);

            var candles = new List<TimeframeBar>(history.Count + closedToday.Count);
            candles.AddRange(history);
            candles.AddRange(closedToday);

            bool hasVwap = watch.Rules.Any(r => r.NeedsVolume);
            var vwap = hasVwap ? IndicatorMath.SessionVwap(candles, todayMinutes, session.Window) : null;
            bool tradedToday = vwap is not null && vwap.Skip(history.Count).Any(v => v is not null);

            // A cooldown window starts only at an alert that reached Telegram,
            // or that this scan is about to send. Until 28 Sep every alert
            // started one: an API down across the 10:05 close found that cross
            // late and did not send it, and the cross back at 10:20 was then
            // withheld as inside its window, so Telegram heard of neither. A
            // batch the limiter dropped, or Telegram refused, did the same.
            // Decided from stored rows, so a restart decides it the same way.
            bool StartsWindow(IndicatorHit h)
            {
                var key = IndicatorOccurrence.From(watch.Symbol, h).DedupeKey;
                if (deliveredKeys.Contains(key)) return true;
                return !recordedKeys.Contains(key)
                       && config.Telegram
                       && watch.NotifyRules.Contains(h.Rule.Key)
                       && h.Candle.EndUtc >= nowUtc - settings.NotifyWindow;
            }

            var hits = IndicatorEvaluator.Evaluate(candles, history.Count, watch.Rules, vwap);
            foreach (var hit in IndicatorEvaluator.ApplyCooldown(hits, config.Cooldown, StartsWindow))
            {
                found.Add((IndicatorOccurrence.From(watch.Symbol, hit.Hit), hit, watch));
            }

            var ruleStates = watch.Rules.Select(r => RuleState(r, candles.Count, closedToday.Count, tradedToday)).Concat(skipped).ToList();
            DateTime? lastBar = todayMinutes.Count > 0 ? todayMinutes.Max(m => m.StartUtc) : null;
            states.Add(new IndicatorWatchState(watch.Symbol, watch.TimeframeMinutes, exchange, true, history.Count, closedToday.Count,
                lastBar, ruleStates, Problem(session.Window, lastBar, nowUtc, settings)));
        }

        var recorded = await RecordAsync(found, config, recordedKeys, nowUtc, settings, cancellationToken);
        return new IndicatorScanOutcome(nowUtc, config.Lines.Count, states, unresolved, recorded);
    }

    /// <summary>Today's indicator alerts already recorded, and those of them that reached Telegram, by key.</summary>
    private async Task<(HashSet<string> Recorded, HashSet<string> Delivered)> TodayAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        var rows = await _db.AlertEvents.AsNoTracking()
            .Where(e => e.Source == Source && e.OccurredUtc >= sinceUtc && e.DedupeKey != null)
            .Select(e => new { e.DedupeKey, e.DeliveredToTelegram })
            .ToListAsync(cancellationToken);

        return (rows.Select(r => r.DedupeKey!).ToHashSet(StringComparer.Ordinal),
            rows.Where(r => r.DeliveredToTelegram).Select(r => r.DedupeKey!).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>The config's lines as (symbol, timeframe) watches, groups resolved as of today.</summary>
    public async Task<(IReadOnlyList<IndicatorWatch> Watches, IReadOnlyList<string> Unresolved)> PlanAsync(
        IndicatorAlertConfig config,
        DateOnly istToday,
        CancellationToken cancellationToken)
    {
        var groups = config.Lines.SelectMany(l => l.Groups).Distinct(StringComparer.Ordinal).ToList();
        var byGroup = groups.Count > 0
            ? await _planner.ResolveGroupsAsync(groups, istToday, cancellationToken)
            : new Dictionary<string, IReadOnlyList<string>>();

        var unresolved = new List<string>();
        var watches = new Dictionary<(string, int), IndicatorWatch>();
        int leftOut = 0;
        int? firstLeftOutLine = null;
        foreach (var line in config.Lines)
        {
            var symbols = new List<string>(line.Symbols);
            foreach (var group in line.Groups)
            {
                var members = byGroup.GetValueOrDefault(group) ?? [];
                if (members.Count == 0) unresolved.Add($"Line {line.Number}: \"{group}\" resolved to no symbols.");
                symbols.AddRange(members);
            }

            foreach (var symbol in symbols.Distinct(StringComparer.Ordinal))
            {
                foreach (var tf in line.Timeframes)
                {
                    if (!watches.TryGetValue((symbol, tf), out var watch))
                    {
                        if (watches.Count >= MaxWatches)
                        {
                            leftOut++;
                            firstLeftOutLine ??= line.Number;
                            continue;
                        }

                        watch = new IndicatorWatch { Symbol = symbol, TimeframeMinutes = tf };
                        watches[(symbol, tf)] = watch;
                    }

                    watch.Lines.Add(line.Number);
                    foreach (var rule in line.Rules)
                    {
                        if (rule.NeedsVolume && IndicatorAlertConfig.IsIndexSymbol(symbol))
                        {
                            watch.Skipped[rule.Key] = (rule, "an index has no traded volume, so it has no VWAP");
                            continue;
                        }

                        if (!watch.Rules.Any(r => r.Key == rule.Key)) watch.Rules.Add(rule);
                        if (!line.PageOnly) watch.NotifyRules.Add(rule.Key);
                    }
                }
            }
        }

        if (leftOut > 0)
        {
            unresolved.Add($"Line {firstLeftOutLine}: the config asks for more than {MaxWatches} symbol × timeframe pairs; " +
                           $"the first {MaxWatches} are scanned and {leftOut} from this line on are left out.");
        }

        foreach (var watch in watches.Values)
        {
            var ordered = watch.Rules.OrderBy(r => r.Kind).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
            watch.Rules.Clear();
            watch.Rules.AddRange(ordered);
        }

        return (watches.Values.OrderBy(w => w.Symbol, StringComparer.Ordinal).ThenBy(w => w.TimeframeMinutes).ToList(), unresolved);
    }

    private static IndicatorRuleState RuleState(IndicatorRule rule, int candles, int todayCandles, bool tradedToday)
    {
        if (rule.NeedsVolume)
        {
            return tradedToday
                ? new IndicatorRuleState(rule.Key, rule.Label, "ready", null)
                : new IndicatorRuleState(rule.Key, rule.Label, "waiting",
                    todayCandles == 0 ? "no closed candle yet today" : "no traded volume in today's bars yet");
        }

        return candles >= rule.SettleCandles
            ? new IndicatorRuleState(rule.Key, rule.Label, "ready", $"{candles} candles read")
            : new IndicatorRuleState(rule.Key, rule.Label, "warming up", $"{candles} of {rule.SettleCandles} candles");
    }

    private static string? Problem(SessionWindow window, DateTime? lastBar, DateTime nowUtc, PatternScanSettings settings)
    {
        var liveUntil = nowUtc < window.CloseUtc ? nowUtc : window.CloseUtc;
        if (liveUntil - window.OpenUtc < settings.StaleBarsAfter) return null;
        if (lastBar is null) return "no live bars — not streamed by any feed";
        return liveUntil - lastBar.Value.AddMinutes(1) >= settings.StaleBarsAfter
            ? $"no live bars since {PatternAlertText.IstClock(lastBar.Value)} IST"
            : null;
    }

    private Dictionary<string, ExchangeSession> SessionsFor(IEnumerable<string> symbols, DateTime nowUtc, TimeSpan afterCloseGrace)
    {
        var result = new Dictionary<string, ExchangeSession>(StringComparer.Ordinal);
        foreach (var exchange in symbols.Select(CandlePatternRules.ExchangeOf).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var info = _sessions.GetSessionInfo(nowUtc, exchange, Segment(exchange));
                var window = new SessionWindow(info.SessionOpenUtc, info.SessionCloseUtc);
                bool inSession = info.IsTradingDay && nowUtc >= window.OpenUtc && nowUtc < window.CloseUtc + afterCloseGrace;
                result[exchange] = new ExchangeSession(window, inSession);
            }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
            {
                _logger.LogWarning("Indicator alerts: no session rules for exchange {Exchange}; its symbols are skipped.", exchange);
            }
        }

        return result;
    }

    private static string Segment(string exchange) => exchange == "MCX" ? "COM" : "CM";

    private async Task<Dictionary<string, List<MinuteBar>>> LoadTodayAsync(
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
            .Select(b => new { b.Symbol, b.BarStartUtc, b.Open, b.High, b.Low, b.Close, b.VolumeDelta })
            .ToListAsync(cancellationToken);

        foreach (var symbol in symbols) result[symbol] = [];
        foreach (var r in rows.OrderBy(r => r.BarStartUtc))
        {
            result[r.Symbol].Add(new MinuteBar(DateTime.SpecifyKind(r.BarStartUtc, DateTimeKind.Utc), r.Open, r.High, r.Low, r.Close, r.VolumeDelta));
        }

        return result;
    }

    /// <summary>
    /// The last <paramref name="warmup"/> closed candles before today's session,
    /// from earlier sessions' live 1-minute bars, built exactly as today's are:
    /// each trading day on its own session window (holidays and weekends have
    /// none, MCX half-days have theirs), pre-open and after-close minutes left out.
    /// </summary>
    private async Task<IReadOnlyList<TimeframeBar>> HistoryAsync(
        string symbol,
        string exchange,
        int timeframe,
        DateOnly today,
        int warmup,
        CancellationToken cancellationToken)
    {
        if (warmup <= 0) return [];
        if (_history.TryGet(symbol, timeframe, today, warmup, out var cached)) return cached;

        int days = Math.Clamp((int)Math.Ceiling(warmup * timeframe / SessionMinutes * 7 / 5) + 5, 5, MaxLookbackDays);
        var from = IstTime.StartOfDayUtc(today.AddDays(-days));
        var before = IstTime.StartOfDayUtc(today);

        var rows = await _db.LiveBars.AsNoTracking()
            .Where(b => b.Resolution == ResolutionCodes.LiveBarResolution
                        && b.Symbol == symbol
                        && b.BarStartUtc >= from && b.BarStartUtc < before)
            .Select(b => new { b.BarStartUtc, b.Open, b.High, b.Low, b.Close })
            .ToListAsync(cancellationToken);

        var candles = new List<TimeframeBar>();
        foreach (var day in rows
                     .Select(r => new MinuteBar(DateTime.SpecifyKind(r.BarStartUtc, DateTimeKind.Utc), r.Open, r.High, r.Low, r.Close))
                     .GroupBy(m => IstTime.DateOf(m.StartUtc))
                     .OrderBy(g => g.Key))
        {
            var info = _sessions.GetSessionInfo(IstTime.MiddayUtc(day.Key), exchange, Segment(exchange));
            if (!info.IsTradingDay) continue;
            var window = new SessionWindow(info.SessionOpenUtc, info.SessionCloseUtc);
            candles.AddRange(SessionBarAggregator.Aggregate(day, window, timeframe, window.CloseUtc).Where(b => b.IsClosed));
        }

        IReadOnlyList<TimeframeBar> result = candles.Count > warmup ? candles.Skip(candles.Count - warmup).ToList() : candles;
        _history.Set(symbol, timeframe, today, warmup, result);
        return result;
    }

    /// <param name="existing">Today's keys already recorded; this scan's are added.</param>
    private async Task<List<RecordedIndicator>> RecordAsync(
        List<(IndicatorOccurrence Occurrence, CooledHit Hit, IndicatorWatch Watch)> found,
        IndicatorAlertConfig config,
        HashSet<string> existing,
        DateTime nowUtc,
        PatternScanSettings settings,
        CancellationToken cancellationToken)
    {
        if (found.Count == 0) return [];

        var fresh = new List<(AlertEvent Row, IndicatorOccurrence Occurrence, bool Notify)>();
        foreach (var (o, hit, watch) in found)
        {
            if (!existing.Add(o.DedupeKey)) continue;

            bool lineSends = watch.NotifyRules.Contains(o.Rule.Key);
            bool recent = o.BarEndUtc >= nowUtc - settings.NotifyWindow;
            string? skipped = !config.Telegram ? "telegram: off in the indicator config"
                : !lineSends ? "page-only in the indicator config"
                : hit.CoolingSinceUtc is { } coolingSince
                    ? $"within the {config.Cooldown.TotalMinutes:0}-minute cooldown after the alert on the candle that closed {PatternAlertText.IstClock(coolingSince)} IST"
                : !recent ? "the candle closed before the scanner saw it (restart or config change)"
                : null;
            bool notify = skipped is null;

            var row = new AlertEvent
            {
                OccurredUtc = o.BarEndUtc,
                Source = Source,
                Underlying = Truncate(UnderlyingCatalog.InferUnderlying(o.Symbol), 40),
                Symbol = o.Symbol,
                Severity = "info",
                Title = Truncate(IndicatorAlertText.Title(o), 200),
                Message = Truncate(IndicatorAlertText.Message(o), 1000),
                MetadataJson = JsonSerializer.Serialize(new IndicatorEventMetadata(
                    IndicatorEventMetadata.KindValue,
                    o.Rule.Key,
                    o.Rule.Name,
                    IndicatorAlertText.What(o),
                    o.Direction,
                    o.TimeframeMinutes,
                    o.BarStartUtc,
                    o.BarEndUtc,
                    o.Open, o.High, o.Low, o.Close,
                    o.MinutesWithData,
                    o.MinutesExpected,
                    o.Values.ToDictionary(v => v.Name, v => Math.Round((decimal)v.Value, 2, MidpointRounding.AwayFromZero)),
                    watch.Lines.ToList(),
                    notify,
                    skipped,
                    hit.CoolingSinceUtc), PatternEventMetadata.Json),
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
            return fresh.Select(f => new RecordedIndicator(f.Row.Id, f.Occurrence, f.Notify)).ToList();
        }
        catch (DbUpdateException ex)
        {
            // Another writer (a second API on this database) holds some of the
            // keys: keep whichever rows are still new; it owns the rest.
            _logger.LogWarning(ex, "Indicator alerts: a batch of {Count} collided with existing keys; retrying one by one.", fresh.Count);
            foreach (var f in fresh) _db.Entry(f.Row).State = EntityState.Detached;

            var kept = new List<RecordedIndicator>();
            foreach (var f in fresh)
            {
                f.Row.Id = 0;
                _db.AlertEvents.Add(f.Row);
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                    kept.Add(new RecordedIndicator(f.Row.Id, f.Occurrence, f.Notify));
                }
                catch (DbUpdateException)
                {
                    _db.Entry(f.Row).State = EntityState.Detached;
                }
            }

            return kept;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
