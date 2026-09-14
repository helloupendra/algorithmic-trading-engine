using System.Globalization;
using System.Text.Json;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// The request and response rules of Dhan's history endpoints, kept free of
/// HTTP so every rule below can be tested on its own.
/// </summary>
/// <remarks>
/// Verified against the live API on 2026-09-14:
/// <list type="bullet">
/// <item>Timestamps are epoch seconds marking the <em>start</em> of each bar
/// (a 1-minute day runs 09:15 to 15:29, 375 bars).</item>
/// <item>fromDate is exclusive of a bar that starts exactly on it: asked from
/// 09:15 the 5-minute answer began at 09:20; asked from 09:00 it began at 09:15.
/// Requests therefore start one minute early and the parser trims to the range
/// the caller asked for.</item>
/// <item>Daily bars are stamped at 00:00 IST of their date.</item>
/// <item>Derivatives and MCX return an open_interest array when "oi" is true.</item>
/// </list>
/// </remarks>
public static class DhanHistory
{
    /// <summary>Intraday: 90 days per request.</summary>
    public static readonly TimeSpan IntradayWindow = TimeSpan.FromDays(90);

    /// <summary>Daily has no published cap; a year per request keeps answers small.</summary>
    public static readonly TimeSpan DailyWindow = TimeSpan.FromDays(365);

    private static readonly HashSet<string> IntradayIntervals = new(StringComparer.Ordinal) { "1", "5", "15", "25", "60" };

    /// <summary>
    /// The interval Dhan's intraday endpoint takes for a platform resolution, or
    /// null for daily bars.
    /// </summary>
    /// <exception cref="NotSupportedException">A resolution Dhan does not serve.</exception>
    public static string? IntervalFor(string? resolution)
    {
        string r = (resolution ?? string.Empty).Trim().ToLowerInvariant();
        if (r is "d" or "1d" or "day" or "daily" or "eod") return null;

        r = r switch
        {
            "1h" or "60m" => "60",
            _ => r.EndsWith('m') ? r[..^1] : r,
        };

        return IntradayIntervals.Contains(r)
            ? r
            : throw new NotSupportedException($"Dhan serves 1, 5, 15, 25 and 60-minute bars and daily bars; '{resolution}' is none of them.");
    }

    /// <summary>[fromUtc, toUtc) cut into windows a single request may cover.</summary>
    public static IEnumerable<(DateTime FromUtc, DateTime ToUtc)> Windows(DateTime fromUtc, DateTime toUtc, bool intraday)
    {
        var span = intraday ? IntradayWindow : DailyWindow;
        for (var start = fromUtc; start < toUtc; start += span)
        {
            var end = start + span < toUtc ? start + span : toUtc;
            yield return (start, end);
        }
    }

    public static object IntradayRequest(DhanInstrument instrument, string interval, DateTime fromUtc, DateTime toUtc) => new
    {
        securityId = instrument.SecurityId.ToString(CultureInfo.InvariantCulture),
        exchangeSegment = instrument.Segment,
        instrument = instrument.InstrumentType,
        interval,
        oi = instrument.HasOpenInterest,
        // One minute early: Dhan excludes a bar that starts exactly on fromDate.
        fromDate = IstStamp(fromUtc.AddMinutes(-1)),
        toDate = IstStamp(toUtc),
    };

    public static object DailyRequest(DhanInstrument instrument, DateTime fromUtc, DateTime toUtc) => new
    {
        securityId = instrument.SecurityId.ToString(CultureInfo.InvariantCulture),
        exchangeSegment = instrument.Segment,
        instrument = instrument.InstrumentType,
        expiryCode = 0,
        oi = instrument.HasOpenInterest,
        fromDate = IstTime.DateOf(fromUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        // A day past the end, and the parser trims: whether Dhan's toDate is
        // inclusive is not documented, and a missing last day is the worse error.
        toDate = IstTime.DateOf(toUtc).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// {"open":[…],"high":[…],"low":[…],"close":[…],"volume":[…],"timestamp":[…],"open_interest":[…]}
    /// into bars inside the caller's range: [fromUtc, toUtc) for intraday, and the
    /// IST dates of fromUtc through toUtc for daily bars.
    /// </summary>
    public static IReadOnlyList<ProviderHistoryBar> Parse(JsonElement root, DateTime fromUtc, DateTime toUtc, bool intraday)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("timestamp", out var timestamps) ||
            timestamps.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProviderHistoryBar>();
        }

        var open = Column(root, "open");
        var high = Column(root, "high");
        var low = Column(root, "low");
        var close = Column(root, "close");
        var volume = Column(root, "volume");
        var oi = Column(root, "open_interest");

        var firstDay = IstTime.DateOf(fromUtc);
        var lastDay = IstTime.DateOf(toUtc);

        int count = timestamps.GetArrayLength();
        var bars = new List<ProviderHistoryBar>(count);
        for (int i = 0; i < count; i++)
        {
            var stamp = DateTimeOffset.FromUnixTimeSeconds((long)Math.Round(timestamps[i].GetDouble())).UtcDateTime;

            bool inRange = intraday
                ? stamp >= fromUtc && stamp < toUtc
                : IstTime.DateOf(stamp) is var day && day >= firstDay && day <= lastDay;
            if (!inRange) continue;

            decimal openInterest = At(oi, i);
            bars.Add(new ProviderHistoryBar
            {
                TimestampUtc = stamp,
                Open = At(open, i),
                High = At(high, i),
                Low = At(low, i),
                Close = At(close, i),
                Volume = At(volume, i),
                // Zero is indistinguishable from "every position closed", which is
                // a claim worth not making: no open interest reads as unknown.
                OpenInterest = openInterest > 0 ? (long)openInterest : null,
            });
        }

        return bars;
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss" on the exchange's clock, which is how Dhan reads both stamps.</summary>
    private static string IstStamp(DateTime utc) =>
        IstTime.ToIst(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static JsonElement? Column(JsonElement root, string name) =>
        root.TryGetProperty(name, out var column) && column.ValueKind == JsonValueKind.Array ? column : null;

    private static decimal At(JsonElement? column, int index)
    {
        if (column is not { } c || index >= c.GetArrayLength()) return 0m;
        var e = c[index];
        return e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var d) ? d : 0m;
    }
}
