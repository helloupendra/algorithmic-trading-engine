using System.Globalization;
using AlgoTrading.Contracts.Instruments;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services.OptionHistory;

/// <summary>
/// Expired index option contracts, and their premiums, served from
/// <c>option_history_bars</c> so a backtest can trade years the instrument master
/// and the broker no longer know.
/// </summary>
/// <remarks>
/// <para>
/// <c>option_history_bars</c> holds Dhan's expired-options history: for every minute,
/// the nearest-expiry contract at each strike offset from ATM, with its actual strike
/// but no symbol and no expiry date. The <see cref="IndexOptionExpiryCalendar"/>
/// supplies the missing expiry. A bar on a day after expiry P and on or before
/// expiry E belongs to the contract expiring on E, because on those days E was the
/// nearest expiry.
/// </para>
/// <para>
/// A contract is only as complete as that history. Its bars exist while its strike
/// was within the imported offsets of ATM (ATM−5…ATM+5 at the time of writing).
/// Once the market moves further away, the contract has no fresh bars, and the
/// backtest's fill falls back to the last close of the same day.
/// </para>
/// </remarks>
public sealed class IndexOptionHistory
{
    public const string MinuteResolution = "1m";
    private const string NearestWeekFlag = "WEEK";
    private const int NearestCode = 1;
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);
    private const int SessionOpenMinute = 9 * 60 + 15;

    private readonly TradingDbContext _db;
    private readonly IndexOptionExpiryCalendar _calendar;
    private readonly Dictionary<string, IReadOnlyList<DateOnly>> _expiries = new(StringComparer.OrdinalIgnoreCase);

    public IndexOptionHistory(TradingDbContext db, IndexOptionExpiryCalendar calendar)
    {
        _db = db;
        _calendar = calendar;
    }

    /// <summary>
    /// Every known option expiry of an index: the exchange calendar plus whatever the
    /// instrument master holds (listed or expired), ascending.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> ExpiriesAsync(string underlying, CancellationToken cancellationToken)
    {
        string key = underlying.Trim().ToUpperInvariant();
        if (_expiries.TryGetValue(key, out var cached)) return cached;
        if (!UnderlyingCatalog.IsIndex(key)) return _expiries[key] = Array.Empty<DateOnly>();

        var master = await _db.Instruments.AsNoTracking()
            .Where(x => x.Underlying == key && x.ExpiryDate.HasValue && (x.InstrumentType == "CE" || x.InstrumentType == "PE"))
            .Select(x => x.ExpiryDate!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return _expiries[key] = _calendar.For(key).Concat(master).Distinct().Order().ToList();
    }

    /// <summary>
    /// The UTC span of a contract's bars: the days after the previous expiry, through
    /// the expiry day itself (IST days). Without a previous expiry, the seven days before.
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtcExclusive) Window(DateOnly? previousExpiry, DateOnly expiry)
    {
        var firstDay = previousExpiry?.AddDays(1) ?? expiry.AddDays(-7);
        return (IstMidnightUtc(firstDay), IstMidnightUtc(expiry.AddDays(1)));
    }

    /// <summary>
    /// An expired contract the history can price: the calendar knows the expiry and at
    /// least one bar exists at that strike and side. Null otherwise.
    /// </summary>
    public async Task<OptionChainItemResponse?> FindContractAsync(
        string underlying, DateOnly expiry, decimal strike, string optionType, CancellationToken cancellationToken)
    {
        string key = underlying.Trim().ToUpperInvariant();
        string side = optionType.Trim().ToUpperInvariant();
        if (side is not ("CE" or "PE")) return null;

        var expiries = await ExpiriesAsync(key, cancellationToken);
        int index = IndexOf(expiries, expiry);
        if (index < 0) return null;

        var (from, to) = Window(index > 0 ? expiries[index - 1] : null, expiry);
        bool priced = await Bars(key, side, strike, from, to).AnyAsync(cancellationToken);
        if (!priced) return null;

        return new OptionChainItemResponse
        {
            Symbol = IndexOptionSymbols.Format(key, expiry, strike, side, IsMonthly(expiries, expiry)),
            Underlying = key,
            ExpiryDate = expiry,
            StrikePrice = strike,
            OptionType = side,
            InstrumentType = side,
            Description = $"{key} {expiry:dd MMM yyyy} {strike.ToString("0.##", CultureInfo.InvariantCulture)} {side} (expired; stored history)"
        };
    }

    /// <summary>
    /// Candles for an expired index option symbol from the stored history, rolled up
    /// from 1 minute to <paramref name="resolution"/> ("1", "5", "15", "D"). Empty when
    /// the symbol is not an index option the calendar knows, or nothing is stored.
    /// </summary>
    /// <remarks>
    /// The date bounds work like the candles query: from 00:00 UTC of
    /// <paramref name="fromDate"/> up to 00:00 UTC of the day after <paramref name="toDate"/>.
    /// </remarks>
    public async Task<IReadOnlyList<CandleResponse>> CandlesAsync(
        string symbol, string resolution, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken)
    {
        var parsed = UnderlyingCatalog.ParseOptionSymbol(symbol);
        if (parsed is null || parsed.Expiry is null || !UnderlyingCatalog.IsIndex(parsed.Underlying))
            return Array.Empty<CandleResponse>();

        string code = ResolutionCodes.ToCandle(resolution);
        if (code != ResolutionCodes.Daily && !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return Array.Empty<CandleResponse>();

        var expiries = await ExpiriesAsync(parsed.Underlying, cancellationToken);
        var expiry = parsed.IsWeekly ? parsed.Expiry.Value : LastExpiryOfMonth(expiries, parsed.Expiry.Value);
        int index = expiry is null ? -1 : IndexOf(expiries, expiry.Value);
        if (index < 0) return Array.Empty<CandleResponse>();

        var (from, to) = Window(index > 0 ? expiries[index - 1] : null, expiries[index]);
        if (fromDate.HasValue) from = Max(from, fromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        if (toDate.HasValue) to = Min(to, toDate.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        if (from >= to) return Array.Empty<CandleResponse>();

        var bars = await Bars(parsed.Underlying, parsed.OptionType, parsed.Strike, from, to)
            .OrderBy(x => x.BarStartUtc)
            .ThenBy(x => Math.Abs(x.StrikeOffset))
            .Select(x => new MinuteBar(x.BarStartUtc, x.Open, x.High, x.Low, x.Close, x.Volume ?? 0))
            .ToListAsync(cancellationToken);

        return RollUp(symbol.Trim(), bars, code);
    }

    /// <summary>First and last stored bar of an index's nearest-expiry history, and the calendar's span.</summary>
    public async Task<OptionHistorySpan?> SpanAsync(string underlying, CancellationToken cancellationToken)
    {
        string key = underlying.Trim().ToUpperInvariant();
        if (!UnderlyingCatalog.IsIndex(key)) return null;

        // The ATM call stands for the whole history: with the offset and side fixed, the
        // series-time index answers first/last in about 0.2 s on the compressed table,
        // where the same question over every offset took 1–15 s.
        var series = _db.OptionHistoryBars.AsNoTracking()
            .Where(x => x.Underlying == key && x.ExpiryFlag == NearestWeekFlag && x.ExpiryCode == NearestCode
                        && x.StrikeOffset == 0 && x.OptionType == "CE" && x.Resolution == MinuteResolution);
        var first = await series.OrderBy(x => x.BarStartUtc).Select(x => (DateTime?)x.BarStartUtc).FirstOrDefaultAsync(cancellationToken);
        if (first is null) return null;
        var last = await series.OrderByDescending(x => x.BarStartUtc).Select(x => (DateTime?)x.BarStartUtc).FirstOrDefaultAsync(cancellationToken);

        var calendar = _calendar.For(key);
        return new OptionHistorySpan(first.Value, last!.Value, calendar.Count,
            calendar.Count > 0 ? calendar[0] : null, calendar.Count > 0 ? calendar[^1] : null);
    }

    /// <summary>
    /// 1-minute bars rolled up the platform's way: intraday buckets start at 09:15 IST;
    /// a daily bar is stamped 00:00 UTC of its IST date. A minute that appears twice
    /// keeps its first row.
    /// </summary>
    public static IReadOnlyList<CandleResponse> RollUp(string symbol, IReadOnlyList<MinuteBar> bars, string resolution)
    {
        string code = ResolutionCodes.ToCandle(resolution);
        int minutes = code == ResolutionCodes.Daily ? 0 : int.Parse(code, CultureInfo.InvariantCulture);
        var result = new List<CandleResponse>();
        CandleResponse? current = null;
        DateTime lastMinute = DateTime.MinValue;

        foreach (var bar in bars)
        {
            if (bar.StartUtc == lastMinute) continue;
            lastMinute = bar.StartUtc;

            var bucket = BucketStart(bar.StartUtc, minutes);
            if (current is null || current.TimestampUtc != bucket)
            {
                current = new CandleResponse
                {
                    Symbol = symbol,
                    Resolution = code,
                    TimestampUtc = bucket,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume
                };
                result.Add(current);
                continue;
            }
            current.High = Math.Max(current.High, bar.High);
            current.Low = Math.Min(current.Low, bar.Low);
            current.Close = bar.Close;
            current.Volume += bar.Volume;
        }
        return result;
    }

    public readonly record struct MinuteBar(DateTime StartUtc, decimal Open, decimal High, decimal Low, decimal Close, long Volume);

    public sealed record OptionHistorySpan(DateTime FirstBarUtc, DateTime LastBarUtc, int CalendarExpiries,
        DateOnly? FirstCalendarExpiry, DateOnly? LastCalendarExpiry);

    private IQueryable<Domain.Entities.OptionHistoryBar> Bars(string underlying, string side, decimal strike, DateTime from, DateTime to) =>
        _db.OptionHistoryBars.AsNoTracking().Where(x =>
            x.Underlying == underlying && x.ExpiryFlag == NearestWeekFlag && x.ExpiryCode == NearestCode
            && x.Resolution == MinuteResolution && x.OptionType == side && x.Strike == strike
            && x.BarStartUtc >= from && x.BarStartUtc < to);

    private static DateTime BucketStart(DateTime startUtc, int minutes)
    {
        var ist = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc) + Ist;
        if (minutes == 0) return DateTime.SpecifyKind(ist.Date, DateTimeKind.Utc);
        if (minutes == 1) return DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
        int minuteOfDay = ist.Hour * 60 + ist.Minute;
        int floor = SessionOpenMinute + (int)Math.Floor((minuteOfDay - SessionOpenMinute) / (double)minutes) * minutes;
        return DateTime.SpecifyKind(ist.Date.AddMinutes(floor) - Ist, DateTimeKind.Utc);
    }

    private static DateTime IstMidnightUtc(DateOnly day) =>
        DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue) - Ist, DateTimeKind.Utc);

    private static bool IsMonthly(IReadOnlyList<DateOnly> expiries, DateOnly expiry) =>
        !expiries.Any(x => x > expiry && x.Year == expiry.Year && x.Month == expiry.Month);

    // A monthly symbol names only the month; its contract expires on the month's last expiry.
    private static DateOnly? LastExpiryOfMonth(IReadOnlyList<DateOnly> expiries, DateOnly anyDayOfMonth)
    {
        DateOnly? last = null;
        foreach (var x in expiries)
            if (x.Year == anyDayOfMonth.Year && x.Month == anyDayOfMonth.Month) last = x;
        return last;
    }

    private static int IndexOf(IReadOnlyList<DateOnly> sorted, DateOnly value)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int cmp = sorted[mid].CompareTo(value);
            if (cmp == 0) return mid;
            if (cmp < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
