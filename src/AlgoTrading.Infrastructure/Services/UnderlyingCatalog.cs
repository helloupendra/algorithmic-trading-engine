// src/AlgoTrading.Infrastructure/Services/UnderlyingCatalog.cs
using System.Text.RegularExpressions;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Static knowledge about F&amp;O underlyings: the spot symbol each one is quoted
/// under, the reverse mapping, the fallback strike step, and a parser for FYERS
/// option symbols. Shared by the lot-size resolver, the live view and the
/// underlyings endpoint so all three agree.
/// </summary>
public static class UnderlyingCatalog
{
    /// <summary>
    /// Index underlyings in display order (indices first, then stocks alphabetically).
    /// </summary>
    public static readonly IReadOnlyList<string> IndexUnderlyings = new[]
    {
        "NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "NIFTYNXT50", "SENSEX", "BANKEX"
    };

    private static readonly Dictionary<string, string> SpotByUnderlying = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NIFTY"] = "NSE:NIFTY50-INDEX",
        ["BANKNIFTY"] = "NSE:NIFTYBANK-INDEX",
        ["FINNIFTY"] = "NSE:FINNIFTY-INDEX",
        ["MIDCPNIFTY"] = "NSE:MIDCPNIFTY-INDEX",
        ["NIFTYNXT50"] = "NSE:NIFTYNXT50-INDEX",
        ["SENSEX"] = "BSE:SENSEX-INDEX",
        ["BANKEX"] = "BSE:BANKEX-INDEX"
    };

    private static readonly Dictionary<string, string> UnderlyingBySpot =
        SpotByUnderlying.ToDictionary(x => x.Value, x => x.Key.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, decimal> FallbackStrikeSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NIFTY"] = 50m,
        ["BANKNIFTY"] = 100m,
        ["FINNIFTY"] = 50m,
        ["MIDCPNIFTY"] = 25m,
        ["NIFTYNXT50"] = 100m,
        ["SENSEX"] = 100m,
        ["BANKEX"] = 100m
    };

    // Longest names first so BANKNIFTY is not mistaken for NIFTY.
    private static readonly string[] IndexNamesByLength =
        IndexUnderlyings.OrderByDescending(x => x.Length).ToArray();

    // Monthly: NSE:BANKNIFTY26SEP57600CE  -> underlying yy MON strike CE/PE
    private static readonly Regex MonthlyOption = new(
        @"^(?:[A-Z]+:)?(?<u>[A-Z][A-Z0-9&\-]*?)(?<yy>\d{2})(?<mon>JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)(?<strike>\d+(?:\.\d+)?)(?<type>CE|PE)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Weekly: NSE:NIFTY2690129550CE -> underlying yy m(1-9/O/N/D) dd strike CE/PE
    private static readonly Regex WeeklyOption = new(
        @"^(?:[A-Z]+:)?(?<u>[A-Z][A-Z0-9&\-]*?)(?<yy>\d{2})(?<m>[1-9OND])(?<dd>0[1-9]|[12]\d|3[01])(?<strike>\d+(?:\.\d+)?)(?<type>CE|PE)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Futures: NSE:BANKNIFTY26SEPFUT
    private static readonly Regex Future = new(
        @"^(?:[A-Z]+:)?(?<u>[A-Z][A-Z0-9&\-]*?)(?<yy>\d{2})(?<mon>JAN|FEB|MAR|APR|MAY|JUN|JUL|AUG|SEP|OCT|NOV|DEC)FUT$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// "BANKNIFTY" -> "NSE:NIFTYBANK-INDEX"; anything unknown -> "NSE:{UNDERLYING}-EQ".
    /// </summary>
    public static string SpotSymbolFor(string underlying)
    {
        var key = Normalize(underlying);
        return SpotByUnderlying.TryGetValue(key, out var spot) ? spot : $"NSE:{key}-EQ";
    }

    /// <summary>
    /// "NSE:NIFTYBANK-INDEX" -> "BANKNIFTY"; "NSE:RELIANCE-EQ" -> "RELIANCE"; null when unrecognised.
    /// </summary>
    public static string? UnderlyingForSpot(string? spotSymbol)
    {
        if (string.IsNullOrWhiteSpace(spotSymbol)) return null;
        var s = spotSymbol.Trim();
        if (UnderlyingBySpot.TryGetValue(s, out var u)) return u;

        var body = s.Contains(':') ? s[(s.IndexOf(':') + 1)..] : s;
        if (body.EndsWith("-EQ", StringComparison.OrdinalIgnoreCase))
            return body[..^3].ToUpperInvariant();
        if (body.EndsWith("-INDEX", StringComparison.OrdinalIgnoreCase))
            return body[..^6].ToUpperInvariant();
        return null;
    }

    public static bool IsIndex(string underlying) => SpotByUnderlying.ContainsKey(Normalize(underlying));

    /// <summary>
    /// Sort key: index underlyings in catalog order first, then everything else.
    /// </summary>
    public static int SortRank(string underlying)
    {
        var key = Normalize(underlying);
        for (int i = 0; i < IndexUnderlyings.Count; i++)
        {
            if (string.Equals(IndexUnderlyings[i], key, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return IndexUnderlyings.Count;
    }

    /// <summary>
    /// Strike step used only when the instrument master has no chain to derive it from.
    /// </summary>
    public static decimal FallbackStrikeStep(string underlying)
        => FallbackStrikeSteps.TryGetValue(Normalize(underlying), out var step) ? step : 50m;

    /// <summary>
    /// Best-effort underlying for any symbol: spot/equity symbols via the reverse
    /// map, derivatives via the symbol grammar, else the longest known index name
    /// contained in it, else the leading alphabetic run.
    /// </summary>
    public static string InferUnderlying(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return string.Empty;

        var fromSpot = UnderlyingForSpot(symbol);
        if (fromSpot is not null) return fromSpot;

        var parsed = ParseOptionSymbol(symbol);
        if (parsed is not null) return parsed.Underlying;

        var body = symbol.Contains(':') ? symbol[(symbol.IndexOf(':') + 1)..] : symbol;
        var fut = Future.Match(body);
        if (fut.Success) return fut.Groups["u"].Value.ToUpperInvariant();

        var upper = body.ToUpperInvariant();
        foreach (var name in IndexNamesByLength)
        {
            if (upper.Contains(name)) return name;
        }

        var lead = Regex.Match(upper, @"^[A-Z&\-]+");
        return lead.Success ? lead.Value : upper;
    }

    /// <summary>
    /// Parses a FYERS option symbol (monthly or weekly grammar). Returns null for
    /// anything that is not an option.
    /// </summary>
    public static ParsedOptionSymbol? ParseOptionSymbol(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        var s = symbol.Trim();

        var m = MonthlyOption.Match(s);
        if (m.Success)
        {
            int year = 2000 + int.Parse(m.Groups["yy"].Value);
            int month = MonthFromAbbreviation(m.Groups["mon"].Value);
            // Monthly contracts expire on the last trading day of the month; the exact
            // day comes from the Instruments row when it exists. Here we report the
            // last calendar day so the label still shows the month.
            DateOnly? expiry = month > 0 ? new DateOnly(year, month, DateTime.DaysInMonth(year, month)) : null;
            return new ParsedOptionSymbol(
                m.Groups["u"].Value.ToUpperInvariant(),
                decimal.Parse(m.Groups["strike"].Value, System.Globalization.CultureInfo.InvariantCulture),
                m.Groups["type"].Value.ToUpperInvariant(),
                expiry,
                IsWeekly: false);
        }

        var w = WeeklyOption.Match(s);
        if (w.Success)
        {
            int year = 2000 + int.Parse(w.Groups["yy"].Value);
            int month = w.Groups["m"].Value.ToUpperInvariant() switch
            {
                "O" => 10,
                "N" => 11,
                "D" => 12,
                var d => int.Parse(d)
            };
            int day = int.Parse(w.Groups["dd"].Value);
            DateOnly? expiry = null;
            if (day <= DateTime.DaysInMonth(year, month))
                expiry = new DateOnly(year, month, day);

            return new ParsedOptionSymbol(
                w.Groups["u"].Value.ToUpperInvariant(),
                decimal.Parse(w.Groups["strike"].Value, System.Globalization.CultureInfo.InvariantCulture),
                w.Groups["type"].Value.ToUpperInvariant(),
                expiry,
                IsWeekly: true);
        }

        return null;
    }

    /// <summary>
    /// "BANKNIFTY 57600 CE · 29 Sep" (expiry part omitted when unknown).
    /// </summary>
    public static string ContractLabel(string underlying, decimal? strike, string? optionType, DateOnly? expiry)
    {
        var strikeText = strike.HasValue
            ? strike.Value.ToString(strike.Value == decimal.Truncate(strike.Value) ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        var head = string.Join(' ', new[] { Normalize(underlying), strikeText, (optionType ?? string.Empty).ToUpperInvariant() }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return expiry.HasValue
            ? $"{head} · {expiry.Value.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)}"
            : head;
    }

    private static int MonthFromAbbreviation(string mon) => mon.ToUpperInvariant() switch
    {
        "JAN" => 1, "FEB" => 2, "MAR" => 3, "APR" => 4, "MAY" => 5, "JUN" => 6,
        "JUL" => 7, "AUG" => 8, "SEP" => 9, "OCT" => 10, "NOV" => 11, "DEC" => 12,
        _ => 0
    };

    private static string Normalize(string? underlying) => (underlying ?? string.Empty).Trim().ToUpperInvariant();
}

/// <summary>
/// Result of <see cref="UnderlyingCatalog.ParseOptionSymbol"/>.
/// </summary>
public sealed record ParsedOptionSymbol(string Underlying, decimal Strike, string OptionType, DateOnly? Expiry, bool IsWeekly);
