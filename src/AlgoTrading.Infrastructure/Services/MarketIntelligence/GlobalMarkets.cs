using System.Text.Json;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>An overseas market the desk records, by its own key and the source's ticker.</summary>
public sealed record GlobalMarket(string Key, string YahooSymbol, string Name);

/// <summary>
/// The overseas markets recorded daily and in the morning snapshots. The keys
/// are the desk's own, so a change of source does not rename a column the
/// models read.
/// </summary>
/// <remarks>
/// Every Yahoo ticker below was checked on 27 Sep 2026: each answered daily
/// bars from January 2020 (^N225 from 6 Jan 2020, after Japan's New Year
/// holidays), so none had to be changed. USDINR is Yahoo's <c>INR=X</c>,
/// rupees per US dollar.
/// </remarks>
public static class GlobalMarketKeys
{
    /// <summary>The snapshot key for GIFT Nifty, read from NSE IX rather than Yahoo.</summary>
    public const string GiftNifty = "GIFTNIFTY";

    public static readonly IReadOnlyList<GlobalMarket> All =
    [
        new("SPX", "^GSPC", "S&P 500"),
        new("NDX", "^NDX", "Nasdaq 100"),
        new("DJI", "^DJI", "Dow Jones Industrial Average"),
        new("VIX", "^VIX", "CBOE Volatility Index"),
        new("N225", "^N225", "Nikkei 225"),
        new("HSI", "^HSI", "Hang Seng"),
        new("KS11", "^KS11", "KOSPI"),
        new("STOXX50E", "^STOXX50E", "Euro Stoxx 50"),
        new("FTSE", "^FTSE", "FTSE 100"),
        new("BRENT", "BZ=F", "Brent crude futures"),
        new("WTI", "CL=F", "WTI crude futures"),
        new("GOLD", "GC=F", "Gold futures"),
        new("DXY", "DX-Y.NYB", "US dollar index"),
        new("US10Y", "^TNX", "US 10-year Treasury yield"),
        new("USDINR", "INR=X", "US dollar / Indian rupee"),
        new("ES", "ES=F", "S&P 500 E-mini futures"),
    ];

    /// <summary>The first date the backfill asks for.</summary>
    public static readonly DateOnly HistoryFrom = new(2020, 1, 1);

    public static string ChartUrl(string yahooSymbol, DateOnly from, DateTime toUtc) =>
        $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}" +
        $"?period1={new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds()}" +
        $"&period2={new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds()}&interval=1d&events=history";

    public static string QuoteUrl(string yahooSymbol) =>
        $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}?range=1d&interval=1d";
}

/// <summary>One daily bar, dated in its market's own time zone.</summary>
public sealed record DailyBar(DateOnly Date, decimal? Open, decimal? High, decimal? Low, decimal Close, decimal? Volume);

/// <summary>Reads Yahoo Finance's v8 chart answer into daily bars.</summary>
public static class YahooDailyBars
{
    /// <summary>
    /// The daily bars in a chart answer, oldest first, one per date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bar's timestamp is its session's start in UTC. The date is read in
    /// the exchange's own zone (<c>meta.exchangeTimezoneName</c>), with its
    /// daylight saving on that date: E-mini futures open at midnight New York
    /// time, so a fixed offset would put every winter bar on the day before.
    /// </para>
    /// <para>
    /// A bar without a close (a holiday Yahoo pads, 63 of VIX's since 2020) is
    /// skipped. When one date comes twice, as Yahoo does for the day in
    /// progress, the later entry wins. Volume is null when the source gives
    /// none or zero, which for an index, a rate or a currency means "none".
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DailyBar> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("chart", out var chart)
            || !chart.TryGetProperty("result", out var results)
            || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
        {
            string error = doc.RootElement.TryGetProperty("chart", out var c) && c.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                ? e.GetRawText()
                : "no result";
            throw new FormatException($"Yahoo chart answer has no data: {error}");
        }

        var result = results[0];
        var meta = result.GetProperty("meta");
        var zone = ZoneOf(meta);

        if (!result.TryGetProperty("timestamp", out var stamps) || stamps.ValueKind != JsonValueKind.Array) return [];
        var quote = result.GetProperty("indicators").GetProperty("quote")[0];
        var open = Series(quote, "open");
        var high = Series(quote, "high");
        var low = Series(quote, "low");
        var close = Series(quote, "close");
        var volume = Series(quote, "volume");

        var byDate = new SortedDictionary<DateOnly, DailyBar>();
        int i = 0;
        foreach (var stamp in stamps.EnumerateArray())
        {
            int at = i++;
            if (at >= close.Count || close[at] is not decimal c) continue;
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(stamp.GetInt64()).UtcDateTime, zone);
            var date = DateOnly.FromDateTime(local);
            decimal? v = at < volume.Count && volume[at] is > 0 ? volume[at] : null;
            byDate[date] = new DailyBar(date, At(open, at), At(high, at), At(low, at), c, v);
        }

        return byDate.Values.ToList();
    }

    private static TimeZoneInfo ZoneOf(JsonElement meta)
    {
        string? name = meta.TryGetProperty("exchangeTimezoneName", out var n) ? n.GetString() : null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(name);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Falls through to the fixed offset below.
            }
        }

        // Only reached on a machine without tz data: today's offset, which is
        // wrong across a daylight-saving change for the midnight-stamped futures.
        int seconds = meta.TryGetProperty("gmtoffset", out var g) && g.TryGetInt32(out var s) ? s : 0;
        return TimeZoneInfo.CreateCustomTimeZone("yahoo-fixed", TimeSpan.FromSeconds(seconds), "fixed", "fixed");
    }

    private static List<decimal?> Series(JsonElement quote, string name)
    {
        var values = new List<decimal?>();
        if (!quote.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return values;
        foreach (var v in array.EnumerateArray())
        {
            values.Add(v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
                ? Math.Round((decimal)d, 6)
                : null);
        }

        return values;
    }

    private static decimal? At(List<decimal?> series, int index) => index < series.Count ? series[index] : null;

    /// <summary>The percent change, rounded to the column's four places; null without a usable previous close.</summary>
    public static decimal? ChangePct(decimal price, decimal? previousClose) =>
        previousClose is > 0 ? Math.Round((price - previousClose.Value) / previousClose.Value * 100m, 4) : null;
}
