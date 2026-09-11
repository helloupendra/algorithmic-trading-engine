using System.Globalization;
using System.Text.RegularExpressions;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>
/// Translates between this platform's canonical symbol and TrueData's grammar.
/// </summary>
/// <remarks>
/// Pure and static on purpose: symbol grammar is a fact about two vendors, not a
/// service, and it is the one part of a connector worth testing against strings
/// copied straight out of the vendor's own master file.
///
/// <para>What is lexical, and what is not, matters more here than the rules:</para>
/// <list type="bullet">
/// <item><b>Indices and equities</b> translate on their own: "NSE:NIFTY50-INDEX"
/// is TrueData's "NIFTY 50", "NSE:RELIANCE-EQ" is "RELIANCE".</item>
/// <item><b>Weekly options</b> translate on their own. Both grammars carry the
/// expiry date; they only spell the month differently, one character against
/// two digits ("NSE:NIFTY2691523500CE" is "NIFTY26091523500CE").</item>
/// <item><b>Monthly options and futures do not.</b> TrueData names every option
/// by its exact expiry day and every future by how far out it is ("CRUDEOIL-I"
/// is the near month), while the canonical monthly form carries only a month
/// ("BANKNIFTY26SEP57500CE"). The day cannot be invented, so these return null
/// and the caller falls back to the mapping rows an import wrote from TrueData's
/// own master. Guessing the last Thursday here would be a bug that pays out in
/// wrong prices on the one day a month it is wrong.</item>
/// </list>
/// </remarks>
public static class TrueDataSymbols
{
    /// <summary>Canonical spot symbol → TrueData index name, both ways.</summary>
    private static readonly IReadOnlyDictionary<string, string> IndexByCanonical =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NSE:NIFTY50-INDEX"] = "NIFTY 50",
            ["NSE:NIFTYBANK-INDEX"] = "NIFTY BANK",
            ["NSE:FINNIFTY-INDEX"] = "NIFTY FIN SERVICE",
            ["NSE:MIDCPNIFTY-INDEX"] = "NIFTY MID SELECT",
            ["NSE:NIFTYNXT50-INDEX"] = "NIFTY NEXT 50",
            ["BSE:SENSEX-INDEX"] = "SENSEX",
            ["BSE:BANKEX-INDEX"] = "BANKEX",
        };

    private static readonly IReadOnlyDictionary<string, string> CanonicalByIndex =
        IndexByCanonical.ToDictionary(x => x.Value, x => x.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>"NSE:RELIANCE-EQ" — cash equity, the only suffix TrueData drops.</summary>
    private static readonly Regex Equity =
        new(@"^(?<ex>NSE|BSE):(?<name>[A-Z0-9&\-]+?)-EQ$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>TrueData weekly/monthly option: underlying + yy + MM + dd + strike + CE/PE.</summary>
    private static readonly Regex VendorOption =
        new(@"^(?<u>[A-Z&]+)(?<yy>\d{2})(?<mm>\d{2})(?<dd>\d{2})(?<strike>\d+)(?<type>CE|PE)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Canonical → TrueData, or null when the canonical form does not carry
    /// enough to build the vendor's name and a mapping row must answer instead.
    /// </summary>
    public static string? ToVendor(string? canonicalSymbol)
    {
        if (string.IsNullOrWhiteSpace(canonicalSymbol)) return null;
        var s = canonicalSymbol.Trim();

        if (IndexByCanonical.TryGetValue(s, out var index)) return index;

        var eq = Equity.Match(s);
        if (eq.Success) return eq.Groups["name"].Value.ToUpperInvariant();

        // Options: the platform's own parser already knows both canonical
        // grammars and the O/N/D month characters, so it is reused rather than
        // re-derived. Only the weekly form carries the day TrueData needs.
        var parsed = UnderlyingCatalog.ParseOptionSymbol(s);
        if (parsed is { IsWeekly: true, Expiry: { } expiry })
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{parsed.Underlying}{expiry:yyMMdd}{FormatStrike(parsed.Strike)}{parsed.OptionType}");
        }

        return null;
    }

    /// <summary>
    /// Canonical form for a symbol TrueData used, or null when it cannot be
    /// rebuilt without knowing the contract (its futures, mainly).
    /// </summary>
    public static string? FromVendor(string? vendorSymbol)
    {
        if (string.IsNullOrWhiteSpace(vendorSymbol)) return null;
        var s = vendorSymbol.Trim();

        if (CanonicalByIndex.TryGetValue(s, out var canonicalIndex)) return canonicalIndex;

        var opt = VendorOption.Match(s);
        if (opt.Success)
        {
            int yy = int.Parse(opt.Groups["yy"].Value, CultureInfo.InvariantCulture);
            int mm = int.Parse(opt.Groups["mm"].Value, CultureInfo.InvariantCulture);
            int dd = int.Parse(opt.Groups["dd"].Value, CultureInfo.InvariantCulture);
            if (mm is < 1 or > 12) return null;
            if (dd < 1 || dd > DateTime.DaysInMonth(2000 + yy, mm)) return null;

            string underlying = opt.Groups["u"].Value.ToUpperInvariant();
            string exchange = UnderlyingCatalog.SpotSymbolFor(underlying).StartsWith("BSE:", StringComparison.OrdinalIgnoreCase)
                ? "BSE"
                : "NSE";

            // The canonical weekly grammar: one character for the month, with
            // October, November and December as O, N and D.
            string month = mm switch { 10 => "O", 11 => "N", 12 => "D", _ => mm.ToString(CultureInfo.InvariantCulture) };
            return string.Create(CultureInfo.InvariantCulture,
                $"{exchange}:{underlying}{yy:D2}{month}{dd:D2}{opt.Groups["strike"].Value}{opt.Groups["type"].Value.ToUpperInvariant()}");
        }

        // A plain name with no decoration is cash equity. "-I"/"-II" futures and
        // anything else need the master, so they are declined here rather than
        // guessed at.
        if (Regex.IsMatch(s, @"^[A-Z0-9&]+$", RegexOptions.IgnoreCase) && !s.Contains('-'))
            return $"NSE:{s.ToUpperInvariant()}-EQ";

        return null;
    }

    /// <summary>
    /// Platform resolution ("5", "5m", "D") → TrueData interval ("5min", "eod").
    /// </summary>
    public static string ToInterval(string? resolution)
    {
        var canonical = ResolutionCodes.ToCandle(resolution);
        return canonical == ResolutionCodes.Daily
            ? "eod"
            : canonical + "min";
    }

    /// <summary>
    /// TrueData's from/to stamps are "yyMMddTHH:mm:ss" in IST — the exchange's
    /// clock, not UTC, which is what every timestamp it returns is in too.
    /// </summary>
    public static string ToRequestStamp(DateTime utc) =>
        IstTime.ToIst(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("yyMMdd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Strikes are whole numbers in both grammars; a ".0" tail would not match.</summary>
    private static string FormatStrike(decimal strike) =>
        strike == decimal.Truncate(strike)
            ? decimal.Truncate(strike).ToString(CultureInfo.InvariantCulture)
            : strike.ToString(CultureInfo.InvariantCulture);
}
