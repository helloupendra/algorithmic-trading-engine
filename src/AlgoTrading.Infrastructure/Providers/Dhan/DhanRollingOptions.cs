using System.Globalization;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>One option series of the expired options endpoint, as the importer asks for it.</summary>
public sealed record DhanRollingSeries(
    string Underlying,
    string ExpiryFlag,
    int ExpiryCode,
    int StrikeOffset,
    string OptionType,
    string Interval)
{
    /// <summary>"1m", "5m"…: the Resolution column.</summary>
    public string Resolution => DhanRollingOptions.ResolutionFor(Interval);

    public override string ToString() =>
        $"{Underlying} {ExpiryFlag}{ExpiryCode} {DhanRollingOptions.StrikeArgument(StrikeOffset)} {OptionType} {Resolution}";
}

/// <summary>
/// The request and response rules of Dhan's expired options endpoint
/// (<c>POST /charts/rollingoption</c>), kept free of HTTP and the database so
/// each is pinned by a test.
/// </summary>
/// <remarks>
/// Verified against the live API on 2026-09-15; where the answer differs from
/// Dhan's documentation, the answer wins and the difference is noted:
/// <list type="bullet">
/// <item>Timestamps are epoch seconds marking the <em>start</em> of each bar
/// (the first bar of a day is 09:15 IST). The 1-minute spot equals the NIFTY
/// index's 1-minute close for the same stamp.</item>
/// <item><c>toDate</c> is <em>inclusive</em> (documented as exclusive): from
/// 2025-09-01 to 2025-09-01 returned that day's 75 five-minute bars. Requests
/// ask one day past the window and the parser trims, which is right under
/// either reading.</item>
/// <item>A CALL request answers <c>ce</c> with <c>pe</c> null, and a PUT the reverse.</item>
/// <item><c>expiryCode</c> 1 is the nearest expiry (0 is refused as "expiryCode
/// is required", although the annexure lists 0 as current). On expiry day,
/// code 1 is still the contract expiring that afternoon.</item>
/// <item>Strikes ATM−10 to ATM+10 answer for index options, weekly and monthly.
/// ATM+11 answers 200 with empty arrays, not an error, and so does a strike
/// written "+1" (read as ATM). Offsets are validated here for that reason.</item>
/// <item>Intervals 1, 5, 15 and 60 answer; 25 is refused (DH-905) despite the
/// documentation.</item>
/// <item>Data begins in August 2020 (2020-08-03 answers, July 2020 is empty).</item>
/// <item>BSE underlyings (SENSEX 51, BANKEX 69) need <c>BSE_FNO</c>; asked under
/// <c>NSE_FNO</c> or <c>IDX_I</c> they answer 200 with empty arrays.</item>
/// <item>Bars coarser than a minute are built from minutes that can belong to
/// different strikes: when ATM moves inside a 5-minute bar, its open and high
/// can come from the old strike and its close from the new one, and
/// <c>strike</c> is the strike at the bar's end.</item>
/// </list>
/// </remarks>
public static class DhanRollingOptions
{
    public const string Path = "/charts/rollingoption";

    /// <summary>Dhan's documented cap per request is 30 days.</summary>
    public const int WindowDays = 30;

    /// <summary>Index options answer up to ten strikes either side of the money.</summary>
    public const int MaxStrikeOffset = 10;

    public static readonly IReadOnlyList<string> Intervals = new[] { "1", "5", "15", "60" };

    public static readonly IReadOnlyList<string> ExpiryFlags = new[] { "WEEK", "MONTH" };

    private static readonly string[] RequiredData = { "open", "high", "low", "close", "iv", "volume", "strike", "oi", "spot" };

    /// <summary>"ATM", "ATM+3", "ATM-2".</summary>
    /// <exception cref="ArgumentOutOfRangeException">Beyond ±<see cref="MaxStrikeOffset"/>, which Dhan answers with silence.</exception>
    public static string StrikeArgument(int offset)
    {
        if (offset is < -MaxStrikeOffset or > MaxStrikeOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), offset,
                $"Dhan serves strikes ATM-{MaxStrikeOffset} to ATM+{MaxStrikeOffset}; beyond that it answers with no bars rather than an error.");
        }

        return offset switch
        {
            0 => "ATM",
            > 0 => "ATM+" + offset.ToString(CultureInfo.InvariantCulture),
            _ => "ATM-" + (-offset).ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>"5" → "5m".</summary>
    /// <exception cref="NotSupportedException">An interval the endpoint refuses.</exception>
    public static string ResolutionFor(string interval) =>
        Intervals.Contains(interval, StringComparer.Ordinal)
            ? interval + "m"
            : throw new NotSupportedException($"The expired options endpoint serves 1, 5, 15 and 60-minute bars; '{interval}' is none of them.");

    /// <summary>"5", "5m", "1" → the interval string Dhan takes.</summary>
    public static string NormalizeInterval(string? interval)
    {
        string value = (interval ?? string.Empty).Trim().ToLowerInvariant();
        if (value.EndsWith('m')) value = value[..^1];
        _ = ResolutionFor(value);
        return value;
    }

    /// <summary>
    /// The inclusive IST date range [from, to] cut into inclusive windows of at
    /// most <see cref="WindowDays"/> days.
    /// </summary>
    public static IReadOnlyList<(DateOnly From, DateOnly To)> Windows(DateOnly from, DateOnly to)
    {
        var windows = new List<(DateOnly, DateOnly)>();
        for (var start = from; start <= to; start = start.AddDays(WindowDays))
        {
            var end = start.AddDays(WindowDays - 1);
            windows.Add((start, end < to ? end : to));
        }

        return windows;
    }

    /// <summary>The underlying's F&amp;O segment and security id: indices only.</summary>
    /// <exception cref="NotSupportedException">Anything but the six index underlyings.</exception>
    public static DhanInstrument OptionUnderlying(string underlying)
    {
        string key = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        if (!DhanInstruments.IndexUnderlyings.TryGetValue(key, out var index))
        {
            throw new NotSupportedException(
                $"Expired options history is imported for {string.Join(", ", DhanInstruments.IndexUnderlyings.Keys)}; '{underlying}' is not one of them.");
        }

        // The underlying's index id, under its exchange's F&O segment: BSE's
        // indices answer only under BSE_FNO.
        string exchange = DhanInstruments.Indices.First(kv => kv.Value == index).Key.Split(':')[0];
        return new DhanInstrument(exchange == "BSE" ? DhanInstruments.BseFno : DhanInstruments.NseFno, index.SecurityId, "OPTIDX");
    }

    public static object Request(DhanRollingSeries series, DateOnly from, DateOnly to)
    {
        var underlying = OptionUnderlying(series.Underlying);
        return new
        {
            exchangeSegment = underlying.Segment,
            interval = series.Interval,
            securityId = underlying.SecurityId,
            instrument = underlying.InstrumentType,
            expiryFlag = series.ExpiryFlag,
            expiryCode = series.ExpiryCode,
            strike = StrikeArgument(series.StrikeOffset),
            drvOptionType = series.OptionType == "PE" ? "PUT" : "CALL",
            requiredData = RequiredData,
            fromDate = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            // A day past the window, trimmed by the parser: toDate answered
            // inclusive on 2026-09-15 but is documented exclusive, and a lost
            // last day is the worse error.
            toDate = to.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// {"data":{"ce":{"timestamp":[…],"open":[…],…},"pe":null}} into rows for the
    /// series, keeping bars whose IST date lies in [from, to].
    /// </summary>
    /// <remarks>
    /// A repeated timestamp keeps its last copy. A bar without all four prices is
    /// dropped rather than stored with zeros. Open interest, IV and spot of zero
    /// or less are stored as unknown.
    /// </remarks>
    public static List<OptionHistoryBar> Parse(JsonElement root, DhanRollingSeries series, DateOnly from, DateOnly to)
    {
        var rows = new List<OptionHistoryBar>();
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty(series.OptionType == "PE" ? "pe" : "ce", out var side) || side.ValueKind != JsonValueKind.Object ||
            !side.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        var open = Column(side, "open");
        var high = Column(side, "high");
        var low = Column(side, "low");
        var close = Column(side, "close");
        var volume = Column(side, "volume");
        var oi = Column(side, "oi");
        var iv = Column(side, "iv");
        var strike = Column(side, "strike");
        var spot = Column(side, "spot");
        string resolution = series.Resolution;

        var byStamp = new Dictionary<DateTime, OptionHistoryBar>();
        int count = timestamps.GetArrayLength();
        for (int i = 0; i < count; i++)
        {
            if (Number(timestamps, i) is not { } epoch) continue;
            var stamp = DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(epoch)).UtcDateTime;
            var day = IstTime.DateOf(stamp);
            if (day < from || day > to) continue;

            if (Number(open, i) is not { } o || Number(high, i) is not { } h ||
                Number(low, i) is not { } l || Number(close, i) is not { } c ||
                Number(strike, i) is not { } k)
            {
                continue;
            }

            byStamp[stamp] = new OptionHistoryBar
            {
                Underlying = series.Underlying,
                ExpiryFlag = series.ExpiryFlag,
                ExpiryCode = series.ExpiryCode,
                ExpiryDate = null,
                StrikeOffset = series.StrikeOffset,
                Strike = decimal.Round(k, 2),
                OptionType = series.OptionType,
                Resolution = resolution,
                BarStartUtc = stamp,
                Open = decimal.Round(o, 4),
                High = decimal.Round(h, 4),
                Low = decimal.Round(l, 4),
                Close = decimal.Round(c, 4),
                Volume = Number(volume, i) is { } v ? (long)v : null,
                OpenInterest = Number(oi, i) is { } q && q > 0 ? (long)q : null,
                ImpliedVolatility = Number(iv, i) is { } x && x > 0 ? decimal.Round(x, 4) : null,
                SpotPrice = Number(spot, i) is { } s && s > 0 ? decimal.Round(s, 4) : null,
                SourceKey = DhanProvider.Key,
            };
        }

        rows.AddRange(byStamp.Values.OrderBy(r => r.BarStartUtc));
        return rows;
    }

    /// <summary>
    /// The trading days of a window that a series has no bars for. A window is
    /// complete when this is empty, and a window with no trading days at all
    /// (a holiday stretch) is complete without asking.
    /// </summary>
    public static IReadOnlyList<DateOnly> MissingDays(
        IReadOnlyCollection<DateOnly> tradingDays, IReadOnlySet<DateOnly> presentDays, DateOnly from, DateOnly to) =>
        tradingDays.Where(d => d >= from && d <= to && !presentDays.Contains(d)).Order().ToList();

    /// <summary>
    /// Offsets from a request: a JSON array [-3,-2,…], a range string "-3..3",
    /// a single number, or nothing (ATM alone). Sorted and distinct.
    /// </summary>
    /// <exception cref="ArgumentException">Unreadable, or beyond ±<see cref="MaxStrikeOffset"/>.</exception>
    public static IReadOnlyList<int> ParseOffsets(JsonElement? value)
    {
        var offsets = new List<int>();
        if (value is not { } v || v.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            offsets.Add(0);
        }
        else if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int n))
                    throw new ArgumentException("strikeOffsets must be whole numbers, e.g. [-3,-2,-1,0,1,2,3] or \"-3..3\".");
                offsets.Add(n);
            }
        }
        else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int single))
        {
            offsets.Add(single);
        }
        else if (v.ValueKind == JsonValueKind.String)
        {
            string text = v.GetString()!.Trim();
            int dots = text.IndexOf("..", StringComparison.Ordinal);
            if (dots > 0 &&
                int.TryParse(text[..dots], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int a) &&
                int.TryParse(text[(dots + 2)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int b) &&
                a <= b)
            {
                for (int n = a; n <= b; n++) offsets.Add(n);
            }
            else if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n))
            {
                offsets.Add(n);
            }
            else
            {
                throw new ArgumentException($"strikeOffsets '{text}' is not a range like \"-3..3\".");
            }
        }
        else
        {
            throw new ArgumentException("strikeOffsets must be an array of whole numbers or a range like \"-3..3\".");
        }

        if (offsets.Count == 0) throw new ArgumentException("strikeOffsets is empty.");
        if (offsets.Any(n => n is < -MaxStrikeOffset or > MaxStrikeOffset))
            throw new ArgumentException($"strikeOffsets must lie within -{MaxStrikeOffset}..{MaxStrikeOffset}: Dhan answers anything beyond with no bars.");

        return offsets.Distinct().Order().ToList();
    }

    /// <summary>
    /// Days as runs of consecutive weekdays: ["2025-08-01","2025-08-14"],
    /// ["2025-08-18","2025-08-29"]. A weekend never breaks a run; any missing
    /// weekday does, holidays included, so a gap is always visible.
    /// </summary>
    public static IReadOnlyList<(DateOnly From, DateOnly To)> Runs(IEnumerable<DateOnly> days)
    {
        var runs = new List<(DateOnly, DateOnly)>();
        DateOnly? start = null, last = null;
        foreach (var day in days.Distinct().Order())
        {
            if (last is { } prev && NextWeekday(prev) == day)
            {
                last = day;
                continue;
            }

            if (start is { } s) runs.Add((s, last!.Value));
            start = day;
            last = day;
        }

        if (start is { } first) runs.Add((first, last!.Value));
        return runs;
    }

    private static DateOnly NextWeekday(DateOnly day)
    {
        var next = day.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return next;
    }

    private static JsonElement? Column(JsonElement side, string name) =>
        side.TryGetProperty(name, out var column) && column.ValueKind == JsonValueKind.Array ? column : null;

    private static decimal? Number(JsonElement? column, int index)
    {
        if (column is not { } c || index >= c.GetArrayLength()) return null;
        var e = c[index];
        if (e.ValueKind != JsonValueKind.Number) return null;
        if (e.TryGetDecimal(out var d)) return d;
        return e.TryGetDouble(out var f) && double.IsFinite(f) ? (decimal)f : null;
    }
}
