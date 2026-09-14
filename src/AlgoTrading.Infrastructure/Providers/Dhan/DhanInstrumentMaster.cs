using System.Globalization;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>One row of Dhan's detailed instrument master, as much of it as mapping needs.</summary>
public sealed record DhanMasterRow(
    string Exchange,
    string SegmentLetter,
    long SecurityId,
    string Instrument,
    string UnderlyingSymbol,
    string Series,
    decimal LotSize,
    DateOnly? Expiry,
    decimal? Strike,
    string OptionType,
    string ExpiryFlag);

/// <summary>A platform instrument, as much of it as matching needs.</summary>
public sealed record PlatformInstrument(
    long Id,
    string Symbol,
    string Exchange,
    string Segment,
    string InstrumentType,
    string Underlying,
    DateOnly? Expiry,
    decimal? Strike,
    string OptionType);

/// <summary>The outcome of matching the master against the platform's instruments.</summary>
public sealed record DhanMatchResult(
    IReadOnlyDictionary<string, (DhanInstrument Instrument, long InstrumentId)> ByCanonical,
    IReadOnlyList<string> Unmatched);

/// <summary>
/// Reads Dhan's detailed instrument master and matches it to the platform's
/// canonical instruments. Pure: no HTTP, no database, so every rule is testable.
/// </summary>
/// <remarks>
/// Matching is by contract attributes, never by building a name: exchange,
/// underlying, expiry date, strike and CE/PE for options; exchange, underlying
/// and expiry for futures; the symbol for NSE equities. Traps found in the file on
/// 2026-09-14, each handled here:
/// <list type="bullet">
/// <item>Columns are read by header name; the header ends with a trailing comma.</item>
/// <item>A future's STRIKE_PRICE is "-0.01000", not zero.</item>
/// <item>INSTRUMENT_TYPE says "OP" on NSE but "OPTIDX" on BSE; INSTRUMENT is
/// consistent, so that is the column used.</item>
/// <item>UNDERLYING_SECURITY_ID on derivatives (26000 for NIFTY) is not the index's
/// own id (13); the underlying is matched by symbol.</item>
/// <item>Expired rows are still present; anything that expired before today is skipped.</item>
/// </list>
/// </remarks>
public static class DhanInstrumentMaster
{
    private static readonly string[] Required =
    {
        "EXCH_ID", "SEGMENT", "SECURITY_ID", "INSTRUMENT", "UNDERLYING_SYMBOL",
        "SERIES", "LOT_SIZE", "SM_EXPIRY_DATE", "STRIKE_PRICE", "OPTION_TYPE", "EXPIRY_FLAG",
    };

    /// <summary>
    /// Rows from the CSV lines, header first. Rows for segments the platform does
    /// not trade, and contracts that expired before <paramref name="today"/>, are
    /// left out.
    /// </summary>
    /// <exception cref="InvalidOperationException">The header lacks a column mapping needs.</exception>
    public static IEnumerable<DhanMasterRow> Parse(IEnumerable<string> lines, DateOnly today)
    {
        using var e = lines.GetEnumerator();
        if (!e.MoveNext()) yield break;

        var header = e.Current.Split(',');
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(header[i])) index[header[i].Trim()] = i;
        }

        var missing = Required.Where(c => !index.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Dhan instrument master is missing column(s): {string.Join(", ", missing)}.");

        while (e.MoveNext())
        {
            var f = e.Current.Split(',');
            string Get(string column) => index[column] < f.Length ? f[index[column]].Trim() : string.Empty;

            string exchange = Get("EXCH_ID").ToUpperInvariant();
            string segment = Get("SEGMENT").ToUpperInvariant();
            if (DhanInstruments.SegmentOf(exchange, segment) is null) continue;
            if (!long.TryParse(Get("SECURITY_ID"), NumberStyles.None, CultureInfo.InvariantCulture, out long securityId)) continue;

            DateOnly? expiry = DateOnly.TryParseExact(Get("SM_EXPIRY_DATE"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                               && d.Year > 1
                ? d
                : null;
            if (expiry is { } x && x < today) continue;

            yield return new DhanMasterRow(
                exchange,
                segment,
                securityId,
                Get("INSTRUMENT").ToUpperInvariant(),
                Get("UNDERLYING_SYMBOL").ToUpperInvariant(),
                Get("SERIES").ToUpperInvariant(),
                decimal.TryParse(Get("LOT_SIZE"), NumberStyles.Number, CultureInfo.InvariantCulture, out var lot) ? lot : 0m,
                expiry,
                decimal.TryParse(Get("STRIKE_PRICE"), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var strike) ? strike : null,
                Get("OPTION_TYPE").ToUpperInvariant(),
                Get("EXPIRY_FLAG").ToUpperInvariant());
        }
    }

    /// <summary>
    /// Pairs every platform instrument with its Dhan row. An instrument with no
    /// match is reported, never guessed.
    /// </summary>
    public static DhanMatchResult Match(IEnumerable<DhanMasterRow> rows, IEnumerable<PlatformInstrument> instruments)
    {
        var dhan = new Dictionary<string, DhanInstrument>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string? key = KeyOf(row);
            if (key is null) continue;
            var segment = DhanInstruments.SegmentOf(row.Exchange, row.SegmentLetter)!;
            // First row wins; a duplicate key in the master is not something to overwrite silently.
            dhan.TryAdd(key, new DhanInstrument(segment, row.SecurityId, row.Instrument));
        }

        var matched = new Dictionary<string, (DhanInstrument, long)>(StringComparer.Ordinal);
        var unmatched = new List<string>();
        foreach (var instrument in instruments)
        {
            string? key = KeyOf(instrument);
            if (key is null) continue;

            if (dhan.TryGetValue(key, out var found)) matched[instrument.Symbol] = (found, instrument.Id);
            else unmatched.Add(instrument.Symbol);
        }

        return new DhanMatchResult(matched, unmatched);
    }

    /// <summary>The attributes a Dhan row is matched by; null for rows mapping does not use.</summary>
    internal static string? KeyOf(DhanMasterRow row)
    {
        if (row.Instrument.StartsWith("OPT", StringComparison.Ordinal))
        {
            if (row.Expiry is null || row.Strike is null || row.OptionType is not ("CE" or "PE")) return null;
            return $"{row.Exchange}|{row.UnderlyingSymbol}|{row.Expiry:yyyy-MM-dd}|{row.OptionType}|{Strike(row.Strike.Value)}";
        }

        if (row.Instrument.StartsWith("FUT", StringComparison.Ordinal))
        {
            return row.Expiry is null ? null : $"{row.Exchange}|{row.UnderlyingSymbol}|{row.Expiry:yyyy-MM-dd}|FUT|";
        }

        if (row.Instrument == "EQUITY" && row.Exchange == "NSE" && row.Series == "EQ")
        {
            return $"NSE|EQ|{row.UnderlyingSymbol}";
        }

        return null;
    }

    internal static string? KeyOf(PlatformInstrument instrument)
    {
        string exchange = instrument.Exchange.Trim().ToUpperInvariant();
        string underlying = instrument.Underlying.Trim().ToUpperInvariant();
        string type = instrument.InstrumentType.Trim().ToUpperInvariant();
        string option = instrument.OptionType.Trim().ToUpperInvariant();

        if (option is "CE" or "PE")
        {
            if (instrument.Expiry is null || instrument.Strike is null || underlying.Length == 0) return null;
            return $"{exchange}|{underlying}|{instrument.Expiry:yyyy-MM-dd}|{option}|{Strike(instrument.Strike.Value)}";
        }

        if (type == "FUT")
        {
            return instrument.Expiry is null || underlying.Length == 0 ? null : $"{exchange}|{underlying}|{instrument.Expiry:yyyy-MM-dd}|FUT|";
        }

        // NSE:RELIANCE-EQ → NSE|EQ|RELIANCE
        if (exchange == "NSE" && type == "EQ" && instrument.Symbol.StartsWith("NSE:", StringComparison.OrdinalIgnoreCase) &&
            instrument.Symbol.EndsWith("-EQ", StringComparison.OrdinalIgnoreCase))
        {
            return $"NSE|EQ|{instrument.Symbol[4..^3].ToUpperInvariant()}";
        }

        return null;
    }

    /// <summary>"23950.00000" and "23950.00" are one strike.</summary>
    private static string Strike(decimal strike) =>
        decimal.Round(strike, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
