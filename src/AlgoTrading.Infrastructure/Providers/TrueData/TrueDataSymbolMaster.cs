using System.Globalization;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>One row of a TrueData symbol master.</summary>
public sealed record TrueDataMasterRow(
    string VendorSymbol,
    string Series,
    string Exchange,
    int? LotSize,
    decimal? Strike,
    DateOnly? Expiry);

/// <summary>
/// Turns TrueData's symbol master into the mapping rows this platform needs.
/// </summary>
/// <remarks>
/// This is what answers the names the grammar rules cannot: a monthly option,
/// whose canonical form carries a month and no expiry day, and a future, which
/// TrueData names by how far out it is ("CRUDEOIL-I" is whichever contract is
/// nearest today and means something different after every expiry).
///
/// <para>The master gives the expiry and the strike outright, so the canonical
/// name is derived from those rather than from the vendor's spelling. Nothing
/// here guesses.</para>
/// </remarks>
public static class TrueDataSymbolMaster
{
    /// <summary>
    /// The header TrueData sends with <c>csvHeader=true</c>. Parsed by name, so
    /// a column inserted upstream shifts nothing.
    /// </summary>
    public static IReadOnlyList<TrueDataMasterRow> ParseCsv(string csv)
    {
        var rows = new List<TrueDataMasterRow>();
        if (string.IsNullOrWhiteSpace(csv)) return rows;

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return rows;

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var header = lines[0].Trim().Split(',');
        for (int i = 0; i < header.Length; i++)
        {
            // The master repeats "symbolalias" twice; first one wins, and the
            // duplicate is ignored rather than throwing.
            index.TryAdd(header[i].Trim(), i);
        }

        foreach (var line in lines.Skip(1))
        {
            var cells = line.Trim().Split(',');
            string? symbol = Cell(cells, index, "symbol");
            if (string.IsNullOrWhiteSpace(symbol)) continue;

            rows.Add(new TrueDataMasterRow(
                symbol.ToUpperInvariant(),
                (Cell(cells, index, "series") ?? string.Empty).ToUpperInvariant(),
                (Cell(cells, index, "exchange") ?? string.Empty).ToUpperInvariant(),
                Int(cells, index, "lotsize"),
                Decimal(cells, index, "strike"),
                Expiry(cells, index)));
        }

        return rows;
    }

    /// <summary>
    /// The canonical symbol for a master row, or null when this platform has no
    /// name for it (an expired contract, a series it does not trade).
    /// </summary>
    /// <remarks>
    /// Built from the row's own expiry and strike, never from the vendor's
    /// spelling — which is the whole reason the master is imported.
    /// </remarks>
    public static string? ToCanonical(TrueDataMasterRow row)
    {
        if (row.Expiry is null) return null;

        string prefix = row.Exchange switch
        {
            "BSE" => "BSE:",
            "MCX" => "MCX:",
            _ => "NSE:",
        };

        string underlying = UnderlyingOf(row.VendorSymbol);
        if (string.IsNullOrWhiteSpace(underlying)) return null;

        var expiry = row.Expiry.Value;

        if (row.Series is "CE" or "PE")
        {
            if (row.Strike is not { } strike) return null;
            // The canonical dated-option grammar: yy, one character of month
            // (October to December are O, N and D), dd, strike, type.
            return $"{prefix}{underlying}{expiry:yy}{MonthChar(expiry.Month)}{expiry.Day:D2}" +
                   $"{decimal.Truncate(strike).ToString(CultureInfo.InvariantCulture)}{row.Series}";
        }

        // "XX" is TrueData's series for a future. The canonical grammar names it
        // by its expiry month, which is exactly what the master carries.
        if (row.Series == "XX")
        {
            return $"{prefix}{underlying}{expiry:yy}{expiry.ToString("MMM", CultureInfo.InvariantCulture).ToUpperInvariant()}FUT";
        }

        return null;
    }

    /// <summary>
    /// The underlying in a TrueData derivative name: the letters before the
    /// digits ("NIFTY26091523400CE") or before the "-I" of a future.
    /// </summary>
    private static string UnderlyingOf(string vendorSymbol)
    {
        int dash = vendorSymbol.IndexOf('-');
        string body = dash > 0 ? vendorSymbol[..dash] : vendorSymbol;

        int digit = body.IndexOfAny("0123456789".ToCharArray());
        if (digit > 0) body = body[..digit];

        return body.Trim().ToUpperInvariant();
    }

    private static string MonthChar(int month) => month switch
    {
        10 => "O",
        11 => "N",
        12 => "D",
        _ => month.ToString(CultureInfo.InvariantCulture),
    };

    private static string? Cell(string[] cells, Dictionary<string, int> index, string name) =>
        index.TryGetValue(name, out int at) && at < cells.Length && !string.IsNullOrWhiteSpace(cells[at])
            ? cells[at].Trim()
            : null;

    private static int? Int(string[] cells, Dictionary<string, int> index, string name) =>
        int.TryParse(Cell(cells, index, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static decimal? Decimal(string[] cells, Dictionary<string, int> index, string name) =>
        decimal.TryParse(Cell(cells, index, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The master writes an expiry "15-09-2026", and blank for cash.</summary>
    private static DateOnly? Expiry(string[] cells, Dictionary<string, int> index)
    {
        var raw = Cell(cells, index, "expiry");
        if (raw is null) return null;
        foreach (var format in new[] { "dd-MM-yyyy", "yyyy-MM-dd", "dd-MM-yyyy HH:mm:ss", "yyyy-MM-ddTHH:mm:ss" })
        {
            if (DateOnly.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        }
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? DateOnly.FromDateTime(dt)
            : null;
    }
}
