// src/AlgoTrading.Infrastructure/Services/StraddleService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.OptionHistory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The ATM straddle through a session and the ATM IV day by day, read from
/// the recorded option chain.
/// </summary>
/// <remarks>
/// <para>
/// One expiry per day, always: the nearest one on or after the day that was
/// recorded on it, which is the contract the chain recorder writes every
/// minute. Later expiries are recorded too, at a slower cadence, and a
/// straddle that took whichever contract a minute's capture happened to be
/// would jump between two prices that never traded as one line.
/// </para>
/// <para>
/// The table is some 15 GB, so nothing here scans it. Every read is a seek or
/// a short range on (Underlying, ExpiryDate, CapturedUtc): one expiry's one
/// day for a straddle, one capture for an IV reading, the first or last row
/// of an expiry to learn when it was recorded. Only strikes within
/// <see cref="StrikeBand"/> of the spot come back. A session that has ended
/// cannot change, so it is computed once and kept in memory; today is read
/// incrementally, each request taking only the captures since the last.
/// </para>
/// <para>
/// The IV history is two single captures a day (one near the open, one near
/// the close), kept per day once the day has closed. That is a few hundred
/// small reads the first time a process is asked, rather than a pass over a
/// year of chains, and a handful for today on every request after.
/// </para>
/// </remarks>
public class StraddleService
{
    /// <summary>
    /// How far from the spot a strike may be and still be read: 3%. The money
    /// is half a strike step away at most, and every chain recorded steps in
    /// well under 3% of its price (NIFTY 50 points on 25,000; NATURALGAS 5 on 300).
    /// </summary>
    public const decimal StrikeBand = 0.03m;

    /// <summary>The IV history goes back a year: the window IV rank is conventionally read over.</summary>
    public const int HistoryDays = 366;

    /// <summary>The open and close readings are taken this long inside the session, past the auction and the last-minute rush.</summary>
    public static readonly TimeSpan ReadingInset = TimeSpan.FromMinutes(5);

    /// <summary>A reading further than this from its target time is no reading of it.</summary>
    public static readonly TimeSpan ReadingReach = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How far behind the newest capture already read today's next read starts.
    /// A capture is stamped before it is written, so one could commit just
    /// behind the last read; two minutes covers that with room to spare.
    /// </summary>
    private static readonly TimeSpan Reread = TimeSpan.FromMinutes(2);

    /// <summary>Captures tried around a reading's target before giving up on an unpriced stretch.</summary>
    private const int ReadingAttempts = 5;

    /// <summary>
    /// How long the list of expiries and their first and last captures is
    /// kept. It changes only at a day's edges, so the cost is that a
    /// session's first capture can take up to a minute to be seen.
    /// </summary>
    private static readonly TimeSpan SpansFor = TimeSpan.FromMinutes(1);

    /// <summary>Today's IV readings, between requests.</summary>
    private static readonly TimeSpan TodayIvFor = TimeSpan.FromSeconds(30);

    /// <summary>A closed day nobody has looked at for this long is let go.</summary>
    private static readonly TimeSpan ClosedDayIdle = TimeSpan.FromHours(12);

    /// <summary>
    /// One cold IV history at a time. After a deploy every open console asks
    /// at once; the first fills the per-day cache and the rest read it.
    /// </summary>
    private static readonly SemaphoreSlim HistoryGate = new(1, 1);

    private readonly TradingDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly IMarketSessionService? _sessions;
    private readonly ILotSizeResolver? _lotSizes;
    private readonly TimeProvider _time;

    /// <param name="sessions">For the session's hours and whether it is open; without it, NSE's regular hours and never open.</param>
    /// <param name="lotSizes">For the per-lot premium; without it, none.</param>
    public StraddleService(
        TradingDbContext db,
        IMemoryCache cache,
        IMarketSessionService? sessions = null,
        ILotSizeResolver? lotSizes = null,
        TimeProvider? time = null)
    {
        _db = db;
        _cache = cache;
        _sessions = sessions;
        _lotSizes = lotSizes;
        _time = time ?? TimeProvider.System;
    }

    // ------------------------------------------------------------------
    // The straddle through a session
    // ------------------------------------------------------------------

    /// <summary>
    /// The ATM straddle through one IST session, one point per capture minute.
    /// </summary>
    /// <param name="date">The session; omitted, today, or the newest recorded day while today has nothing yet.</param>
    public async Task<StraddleSeriesResponse> GetSeriesAsync(
        string underlying,
        DateOnly? date,
        CancellationToken cancellationToken = default)
    {
        string key = Normalize(underlying);
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(nowUtc);

        var day = date ?? today;
        var session = day < today
            ? await ClosedSessionAsync(key, day, cancellationToken)
            : await TodaySessionAsync(key, day, cancellationToken);

        // Asked for no day, and today has nothing yet (before the open, a
        // holiday): the newest day that was recorded, which the answer names.
        if (date is null && session.Expiry is null)
        {
            var spans = await ExpirySpansAsync(key, cancellationToken);
            var newest = spans.Count == 0 ? today : IstTime.DateOf(spans.Max(s => s.LastCapturedUtc));
            if (newest < today)
            {
                day = newest;
                session = await ClosedSessionAsync(key, day, cancellationToken);
            }
        }

        var response = new StraddleSeriesResponse
        {
            Underlying = key,
            Date = day,
            IsToday = day == today,
            ServerUtc = nowUtc,
            MarketOpen = day == today && IsOpen(key, nowUtc),
        };

        if (_lotSizes is not null)
        {
            var lot = await _lotSizes.ResolveForUnderlyingAsync(key, cancellationToken);
            response.LotSize = lot.Source == LotSizeInfo.SourceUnknown ? null : lot.LotSize;
        }

        if (session.Expiry is not { } expiry) return response;

        response.ExpiryDate = expiry;
        response.DaysToExpiry = expiry.DayNumber - day.DayNumber;
        response.Points = session.Points.ToList();
        response.Sources = session.Sources.ToList();
        response.FirstCapturedUtc = session.Points.Count > 0 ? session.Points[0].CapturedUtc : null;
        response.LastCapturedUtc = session.Points.Count > 0 ? session.Points[^1].CapturedUtc : null;
        response.MidPricedPoints = session.Points.Count(p =>
            p.CallPriceBasis == AtmStraddle.MidBasis && p.PutPriceBasis == AtmStraddle.MidBasis);
        return response;
    }

    /// <summary>One session's points, the expiry they were read from, and the newest capture read.</summary>
    private sealed record Session(
        DateOnly? Expiry,
        IReadOnlyList<StraddlePointResponse> Points,
        IReadOnlyList<string> Sources,
        DateTime? ThroughUtc);

    private static readonly Session Unrecorded = new(null, Array.Empty<StraddlePointResponse>(), Array.Empty<string>(), null);

    /// <summary>A session that has ended: read once, whole, and kept.</summary>
    private async Task<Session> ClosedSessionAsync(string key, DateOnly day, CancellationToken cancellationToken)
    {
        var cacheKey = ("straddle-closed", key, day);
        if (_cache.TryGetValue(cacheKey, out Session? kept) && kept is not null) return kept;

        var session = Unrecorded;
        var expiry = await ExpiryForDayAsync(key, day, cancellationToken);
        if (expiry is not null)
        {
            var rows = await ReadAsync(key, expiry.Value, IstTime.StartOfDayUtc(day), IstTime.StartOfDayUtc(day.AddDays(1)), cancellationToken);
            session = new Session(expiry, AtmStraddle.Series(rows), SourcesOf(rows, null), null);
        }

        _cache.Set(cacheKey, session, new MemoryCacheEntryOptions { SlidingExpiration = ClosedDayIdle });
        return session;
    }

    /// <summary>
    /// Today, read from a whole minute a little before the newest capture
    /// already read. Starting on a minute boundary keeps the kept minutes and
    /// the re-read ones apart, so no minute is counted twice.
    /// </summary>
    private async Task<Session> TodaySessionAsync(string key, DateOnly day, CancellationToken cancellationToken)
    {
        var expiry = await ExpiryForDayAsync(key, day, cancellationToken);
        if (expiry is null) return Unrecorded;

        var dayStartUtc = IstTime.StartOfDayUtc(day);
        var dayEndUtc = IstTime.StartOfDayUtc(day.AddDays(1));
        var cacheKey = ("straddle-today", key, day, expiry.Value);
        _cache.TryGetValue(cacheKey, out Session? known);

        var fromUtc = known?.ThroughUtc is { } through ? MinuteOf(through - Reread) : dayStartUtc;
        if (fromUtc < dayStartUtc) fromUtc = dayStartUtc;

        var rows = await ReadAsync(key, expiry.Value, fromUtc, dayEndUtc, cancellationToken);
        var fresh = AtmStraddle.Series(rows);
        var points = known is null
            ? fresh
            : known.Points.Where(p => p.CapturedUtc < fromUtc).Concat(fresh).ToList();

        var session = new Session(
            expiry,
            points,
            SourcesOf(rows, known?.Sources),
            rows.Count > 0 ? rows.Max(r => r.CapturedUtc) : known?.ThroughUtc);
        _cache.Set(cacheKey, session, new MemoryCacheEntryOptions { AbsoluteExpiration = dayEndUtc.AddHours(1) });
        return session;
    }

    // ------------------------------------------------------------------
    // ATM IV day by day
    // ------------------------------------------------------------------

    /// <summary>
    /// Every recorded session of the last year with its ATM IV near the open
    /// and near the close, and the IV rank and percentile of the current value.
    /// </summary>
    public async Task<IvHistoryResponse> GetIvHistoryAsync(string underlying, CancellationToken cancellationToken = default)
    {
        string key = Normalize(underlying);
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(nowUtc);
        var response = new IvHistoryResponse { Underlying = key, ServerUtc = nowUtc };

        var spans = await ExpirySpansAsync(key, cancellationToken);
        if (spans.Count == 0) return response;

        var firstDay = IstTime.DateOf(spans.Min(s => s.FirstCapturedUtc));
        var fromDay = firstDay > today.AddDays(-HistoryDays) ? firstDay : today.AddDays(-HistoryDays);

        TodayIv? todayIv = null;
        await HistoryGate.WaitAsync(cancellationToken);
        try
        {
            for (var day = fromDay; day < today; day = day.AddDays(1))
            {
                if (await ClosedDayIvAsync(key, day, cancellationToken) is { } reading) response.Days.Add(reading);
            }

            todayIv = await TodayIvAsync(key, today, nowUtc, cancellationToken);
            if (todayIv?.Day is { } todayReading) response.Days.Add(todayReading);
        }
        finally
        {
            HistoryGate.Release();
        }

        // The value ranked is today's newest while today is recorded; before
        // the open, on a holiday, it is the newest session's close. Either way
        // it is measured against the closes of the sessions before its own.
        DateOnly? currentDay;
        if (todayIv?.Latest is { AtTheMoneyIv: { } latestIv } latest)
        {
            response.CurrentIv = latestIv;
            response.CurrentDate = today;
            response.CurrentCapturedUtc = latest.CapturedUtc;
            response.CurrentBasis = "latest";
            currentDay = today;
        }
        else if (response.Days.LastOrDefault(d => d.CloseIv is not null) is { } newest)
        {
            response.CurrentIv = newest.CloseIv;
            response.CurrentDate = newest.Date;
            response.CurrentCapturedUtc = newest.CloseCapturedUtc;
            response.CurrentBasis = "close";
            currentDay = newest.Date;
        }
        else
        {
            return response;
        }

        var earlier = response.Days
            .Where(d => d.Date < currentDay && d.CloseIv is not null)
            .Select(d => d.CloseIv!.Value)
            .ToList();
        var standing = AtmStraddle.Standing(response.CurrentIv!.Value, earlier);
        response.DaysUsed = standing.Days;
        response.IvRank = Round(standing.Rank);
        response.IvPercentile = Round(standing.Percentile);
        response.RangeLow = Round(standing.Low);
        response.RangeHigh = Round(standing.High);
        return response;
    }

    /// <summary>A kept closed day: its readings, or none when nothing was recorded on it.</summary>
    private sealed record ClosedDay(IvHistoryDayResponse? Reading);

    private async Task<IvHistoryDayResponse?> ClosedDayIvAsync(string key, DateOnly day, CancellationToken cancellationToken)
    {
        var cacheKey = ("iv-closed", key, day);
        if (_cache.TryGetValue(cacheKey, out ClosedDay? kept) && kept is not null) return kept.Reading;

        var reading = await DayIvAsync(key, day, DateTime.MaxValue, cancellationToken);
        _cache.Set(cacheKey, new ClosedDay(reading), new MemoryCacheEntryOptions { SlidingExpiration = ClosedDayIdle });
        return reading;
    }

    /// <summary>Today's readings so far, and today's newest priced capture.</summary>
    private sealed record TodayIv(IvHistoryDayResponse? Day, StraddlePointResponse? Latest);

    private async Task<TodayIv?> TodayIvAsync(string key, DateOnly today, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var cacheKey = ("iv-today", key, today);
        if (_cache.TryGetValue(cacheKey, out TodayIv? kept) && kept is not null) return kept;

        var reading = await DayIvAsync(key, today, nowUtc, cancellationToken);
        StraddlePointResponse? latest = null;
        if (reading is not null)
        {
            var (point, _) = await ReadingAsync(key, reading.ExpiryDate, nowUtc, IstTime.StartOfDayUtc(today), forward: false, cancellationToken);
            latest = point;
        }

        var todayIv = new TodayIv(reading, latest);
        _cache.Set(cacheKey, todayIv, TodayIvFor);
        return todayIv;
    }

    /// <summary>
    /// One day's ATM IV at the first priced capture from five minutes after
    /// the open, and at the last from five minutes before the close. Null when
    /// nothing was recorded that day. A reading whose time has not come by
    /// <paramref name="nowUtc"/> is left empty.
    /// </summary>
    private async Task<IvHistoryDayResponse?> DayIvAsync(string key, DateOnly day, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var expiry = await ExpiryForDayAsync(key, day, cancellationToken);
        if (expiry is null) return null;

        var (openUtc, closeUtc) = SessionHours(key, day);
        var openTarget = openUtc + ReadingInset;
        var closeTarget = closeUtc - ReadingInset;

        var reading = new IvHistoryDayResponse
        {
            Date = day,
            ExpiryDate = expiry.Value,
            DaysToExpiry = expiry.Value.DayNumber - day.DayNumber,
        };

        if (nowUtc >= openTarget)
        {
            var (open, source) = await ReadingAsync(key, expiry.Value, openTarget, Earlier(openTarget + ReadingReach, nowUtc), forward: true, cancellationToken);
            reading.OpenIv = Round(open?.AtTheMoneyIv);
            reading.OpenCapturedUtc = open?.CapturedUtc;
            reading.Source = source;
        }

        if (nowUtc >= closeTarget)
        {
            var (close, source) = await ReadingAsync(key, expiry.Value, closeTarget, closeTarget - ReadingReach, forward: false, cancellationToken);
            reading.CloseIv = Round(close?.AtTheMoneyIv);
            reading.CloseCapturedUtc = close?.CapturedUtc;
            reading.Source = source ?? reading.Source;
        }

        return reading;
    }

    /// <summary>
    /// The first capture at or after <paramref name="fromUtc"/> (or, reading
    /// backwards, the last at or before it) whose ATM straddle has an IV,
    /// stopping at <paramref name="limitUtc"/>. Each step is one index seek
    /// and one capture's strikes near the spot.
    /// </summary>
    private async Task<(StraddlePointResponse? Point, string? Source)> ReadingAsync(
        string key,
        DateOnly expiry,
        DateTime fromUtc,
        DateTime limitUtc,
        bool forward,
        CancellationToken cancellationToken)
    {
        var cursor = fromUtc;
        for (int attempt = 0; attempt < ReadingAttempts; attempt++)
        {
            var captures = _db.OptionChainSnapshots.AsNoTracking()
                .Where(x => x.Underlying == key && x.ExpiryDate == expiry);
            var at = forward
                ? await captures.Where(x => x.CapturedUtc >= cursor && x.CapturedUtc < limitUtc)
                    .OrderBy(x => x.CapturedUtc)
                    .Select(x => (DateTime?)x.CapturedUtc)
                    .FirstOrDefaultAsync(cancellationToken)
                : await captures.Where(x => x.CapturedUtc <= cursor && x.CapturedUtc > limitUtc)
                    .OrderByDescending(x => x.CapturedUtc)
                    .Select(x => (DateTime?)x.CapturedUtc)
                    .FirstOrDefaultAsync(cancellationToken);
            if (at is null) return (null, null);

            // Captures are stamped to the second, so this second is that capture.
            var rows = await ReadAsync(key, expiry, at.Value, at.Value.AddSeconds(1), cancellationToken);
            if (AtmStraddle.Point(rows) is { AtTheMoneyIv: not null } point) return (point, rows[0].SourceKey);

            cursor = forward ? at.Value.AddSeconds(1) : at.Value.AddSeconds(-1);
        }

        return (null, null);
    }

    // ------------------------------------------------------------------
    // Which expiry, and reading it
    // ------------------------------------------------------------------

    /// <summary>
    /// The expiry a day's chain was recorded for: the nearest one not behind
    /// the day with captures on it. One existence seek per candidate; on a
    /// trading day the first candidate is the answer.
    /// </summary>
    private async Task<DateOnly?> ExpiryForDayAsync(string key, DateOnly day, CancellationToken cancellationToken)
    {
        var spans = await ExpirySpansAsync(key, cancellationToken);
        var fromUtc = IstTime.StartOfDayUtc(day);
        var untilUtc = IstTime.StartOfDayUtc(day.AddDays(1));

        foreach (var expiry in AtmStraddle.ExpiryCandidates(spans, day))
        {
            bool recorded = await _db.OptionChainSnapshots.AsNoTracking()
                .AnyAsync(x => x.Underlying == key && x.ExpiryDate == expiry
                               && x.CapturedUtc >= fromUtc && x.CapturedUtc < untilUtc, cancellationToken);
            if (recorded) return expiry;
        }

        return null;
    }

    /// <summary>
    /// Every expiry recorded for the underlying, with its first and last
    /// capture: a loose index scan (the next expiry after the last one found,
    /// then that expiry's two ends), three seeks per expiry. A DISTINCT over
    /// the underlying would read every one of its rows.
    /// </summary>
    private async Task<IReadOnlyList<AtmStraddle.ExpirySpan>> ExpirySpansAsync(string key, CancellationToken cancellationToken)
    {
        var cacheKey = ("straddle-spans", key);
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<AtmStraddle.ExpirySpan>? kept) && kept is not null) return kept;

        var spans = new List<AtmStraddle.ExpirySpan>();
        DateOnly? previous = null;
        while (true)
        {
            var rows = _db.OptionChainSnapshots.AsNoTracking().Where(x => x.Underlying == key);
            if (previous is { } after) rows = rows.Where(x => x.ExpiryDate > after);
            var next = await rows.OrderBy(x => x.ExpiryDate)
                .Select(x => (DateOnly?)x.ExpiryDate)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is not { } expiry) break;

            var contract = _db.OptionChainSnapshots.AsNoTracking().Where(x => x.Underlying == key && x.ExpiryDate == expiry);
            var first = await contract.OrderBy(x => x.CapturedUtc).Select(x => x.CapturedUtc).FirstAsync(cancellationToken);
            var last = await contract.OrderByDescending(x => x.CapturedUtc).Select(x => x.CapturedUtc).FirstAsync(cancellationToken);
            spans.Add(new AtmStraddle.ExpirySpan(expiry, AsUtc(first), AsUtc(last)));
            previous = expiry;
        }

        _cache.Set(cacheKey, (IReadOnlyList<AtmStraddle.ExpirySpan>)spans, SpansFor);
        return spans;
    }

    /// <summary>
    /// One expiry's captures in [from, until), only the strikes near each
    /// capture's spot, IV in percent whichever recorder wrote it.
    /// </summary>
    private async Task<List<AtmStraddle.ChainRow>> ReadAsync(
        string key,
        DateOnly expiry,
        DateTime fromUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken)
    {
        decimal below = 1m - StrikeBand;
        decimal above = 1m + StrikeBand;

        var rows = await _db.OptionChainSnapshots.AsNoTracking()
            .Where(x => x.Underlying == key && x.ExpiryDate == expiry
                        && x.CapturedUtc >= fromUtc && x.CapturedUtc < untilUtc
                        && x.StrikePrice >= x.SpotPrice * below && x.StrikePrice <= x.SpotPrice * above)
            .Select(x => new
            {
                x.CapturedUtc,
                x.SpotPrice,
                x.StrikePrice,
                x.OptionType,
                x.LastTradedPrice,
                x.BidPrice,
                x.AskPrice,
                x.ImpliedVolatility,
                x.SourceKey,
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AtmStraddle.ChainRow(
                AsUtc(r.CapturedUtc),
                r.SpotPrice,
                r.StrikePrice,
                r.OptionType,
                r.LastTradedPrice,
                r.BidPrice,
                r.AskPrice,
                OptionMath.IvPercent(r.ImpliedVolatility, OptionMath.ChainSourceStoresPercent(r.SourceKey)),
                r.SourceKey))
            .ToList();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>The day's session as the exchange ran it; NSE's regular hours when the rules are not known.</summary>
    private (DateTime OpenUtc, DateTime CloseUtc) SessionHours(string key, DateOnly day)
    {
        string exchange = ExchangeOf(key);
        if (_sessions is not null)
        {
            try
            {
                var info = _sessions.GetSessionInfo(IstTime.MiddayUtc(day), exchange, SegmentOf(exchange));
                return (info.SessionOpenUtc, info.SessionCloseUtc);
            }
            catch (NotSupportedException)
            {
                // Fall through to the regular NSE session.
            }
        }

        return (IstTime.FromIst(day.ToDateTime(TimeOnly.FromTimeSpan(IstTime.SessionOpen))),
                IstTime.FromIst(day.ToDateTime(TimeOnly.FromTimeSpan(IstTime.SessionClose))));
    }

    private bool IsOpen(string key, DateTime nowUtc)
    {
        if (_sessions is null) return false;
        string exchange = ExchangeOf(key);
        try
        {
            return _sessions.IsMarketOpen(nowUtc, exchange, SegmentOf(exchange));
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The chain service's rule, from the underlying alone: MCX for a commodity, else its index's exchange.</summary>
    private static string ExchangeOf(string key) => OptionChainService.ExchangeOf(key, new OptionChainResponse());

    private static string SegmentOf(string exchange) => exchange == "MCX" ? "COM" : "FO";

    private static IReadOnlyList<string> SourcesOf(IEnumerable<AtmStraddle.ChainRow> rows, IEnumerable<string>? known)
        => (known ?? Array.Empty<string>())
            .Concat(rows.Select(r => r.SourceKey))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string Normalize(string? underlying) => (underlying ?? string.Empty).Trim().ToUpperInvariant();

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime MinuteOf(DateTime utc) => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    private static DateTime Earlier(DateTime a, DateTime b) => a < b ? a : b;

    private static decimal? Round(decimal? value) => value is { } v ? Math.Round(v, 2) : null;
}
