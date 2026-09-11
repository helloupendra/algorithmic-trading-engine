using System.Globalization;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>The greeks of one option, as TrueData priced them.</summary>
public sealed record TrueDataGreeks(
    decimal? Delta,
    decimal? Theta,
    decimal? Vega,
    decimal? Gamma,
    decimal? Rho,
    decimal? ImpliedVolatility);

/// <summary>One side of a strike: a call or a put.</summary>
public sealed record TrueDataChainSide(
    decimal? LastPrice,
    decimal? PreviousClose,
    decimal? Bid,
    long? BidQuantity,
    decimal? Ask,
    long? AskQuantity,
    long? OpenInterest,
    long? PreviousOpenInterest,
    long? Volume,
    TrueDataGreeks? Greeks,
    DateTime? TimestampUtc)
{
    /// <summary>
    /// Change in open interest since the previous close, when both are known.
    /// The number the OI rules actually read: a level says little, the move says
    /// whether writers came in or walked away.
    /// </summary>
    public long? OpenInterestChange =>
        OpenInterest is { } now && PreviousOpenInterest is { } before ? now - before : null;
}

/// <summary>One strike, both sides.</summary>
public sealed record TrueDataChainRow(decimal Strike, TrueDataChainSide Call, TrueDataChainSide Put);

/// <summary>A whole chain for one underlying and expiry.</summary>
public sealed record TrueDataOptionChain(
    string Underlying,
    DateOnly Expiry,
    IReadOnlyList<TrueDataChainRow> Rows)
{
    /// <summary>The strike carrying the most call open interest, or null when none does.</summary>
    public decimal? PeakCallOiStrike => Rows
        .Where(r => r.Call.OpenInterest > 0)
        .OrderByDescending(r => r.Call.OpenInterest)
        .Select(r => (decimal?)r.Strike)
        .FirstOrDefault();

    /// <summary>The strike carrying the most put open interest, or null when none does.</summary>
    public decimal? PeakPutOiStrike => Rows
        .Where(r => r.Put.OpenInterest > 0)
        .OrderByDescending(r => r.Put.OpenInterest)
        .Select(r => (decimal?)r.Strike)
        .FirstOrDefault();

    /// <summary>
    /// Put open interest over call open interest across the chain. Null when
    /// there is no call OI to divide by, rather than a misleading zero.
    /// </summary>
    public decimal? PutCallOiRatio
    {
        get
        {
            long calls = Rows.Sum(r => r.Call.OpenInterest ?? 0);
            long puts = Rows.Sum(r => r.Put.OpenInterest ?? 0);
            return calls > 0 ? Math.Round((decimal)puts / calls, 4) : null;
        }
    }
}

/// <summary>
/// Reads TrueData's greeks chain, which is a header row and one line per strike.
/// </summary>
/// <remarks>
/// CSV rather than the JSON form on purpose. The JSON answer is an array of
/// bare positional arrays with nulls in it, so a field TrueData inserts one day
/// silently shifts every value after it into the wrong property. The CSV names
/// its columns, so the same change is absorbed instead of misread.
/// </remarks>
public static class TrueDataChainCsv
{
    public static TrueDataOptionChain Parse(string csv, string underlying, DateOnly expiry)
    {
        var rows = new List<TrueDataChainRow>();
        if (string.IsNullOrWhiteSpace(csv)) return new TrueDataOptionChain(underlying, expiry, rows);

        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return new TrueDataOptionChain(underlying, expiry, rows);

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var header = lines[0].Trim().Split(',');
        for (int i = 0; i < header.Length; i++) index[header[i].Trim()] = i;

        foreach (var line in lines.Skip(1))
        {
            var cells = line.Trim().Split(',');
            if (cells.Length < header.Length) continue;

            decimal? strike = Decimal(cells, index, "strike");
            if (strike is null) continue;

            rows.Add(new TrueDataChainRow(
                strike.Value,
                Side(cells, index, "call", "c"),
                Side(cells, index, "put", "p")));
        }

        return new TrueDataOptionChain(underlying, expiry, rows);
    }

    private static TrueDataChainSide Side(string[] cells, Dictionary<string, int> index, string prefix, string greekPrefix)
    {
        var greeks = new TrueDataGreeks(
            Decimal(cells, index, greekPrefix + "delta"),
            Decimal(cells, index, greekPrefix + "theta"),
            Decimal(cells, index, greekPrefix + "vega"),
            Decimal(cells, index, greekPrefix + "gamma"),
            Decimal(cells, index, greekPrefix + "rho"),
            Decimal(cells, index, greekPrefix + "iv"));

        // All six empty means this side was not priced — an untraded far strike,
        // usually. Reported as no greeks rather than as six zeroes, which would
        // read as a real delta of nothing.
        bool anyGreek = greeks.Delta.HasValue || greeks.Theta.HasValue || greeks.Vega.HasValue
                        || greeks.Gamma.HasValue || greeks.Rho.HasValue || greeks.ImpliedVolatility.HasValue;

        return new TrueDataChainSide(
            LastPrice: Decimal(cells, index, prefix + "ltp"),
            PreviousClose: Decimal(cells, index, prefix + "PClose"),
            Bid: Decimal(cells, index, prefix + "bid"),
            BidQuantity: Long(cells, index, prefix + "bidqty"),
            Ask: Decimal(cells, index, prefix + "ask"),
            AskQuantity: Long(cells, index, prefix + "askqty"),
            OpenInterest: Long(cells, index, prefix + "OI"),
            PreviousOpenInterest: Long(cells, index, prefix + "pOI") ?? Long(cells, index, prefix + "POI"),
            Volume: Long(cells, index, prefix + "Vol"),
            Greeks: anyGreek ? greeks : null,
            TimestampUtc: Timestamp(cells, index, prefix + "timestamp"));
    }

    private static string? Cell(string[] cells, Dictionary<string, int> index, string name) =>
        index.TryGetValue(name, out int at) && at < cells.Length && !string.IsNullOrWhiteSpace(cells[at])
            ? cells[at].Trim()
            : null;

    private static decimal? Decimal(string[] cells, Dictionary<string, int> index, string name)
    {
        var raw = Cell(cells, index, name);
        return raw is not null && decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static long? Long(string[] cells, Dictionary<string, int> index, string name)
    {
        var raw = Cell(cells, index, name);
        if (raw is null) return null;
        // Volumes come through as decimals often enough to matter.
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? (long)value
            : null;
    }

    /// <summary>"11-09-2026 15:40:00" in IST, stored as UTC.</summary>
    private static DateTime? Timestamp(string[] cells, Dictionary<string, int> index, string name)
    {
        var raw = Cell(cells, index, name);
        if (raw is null) return null;
        return DateTime.TryParseExact(raw, "dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var ist)
            ? AlgoTrading.Infrastructure.Services.IstTime.FromIst(ist)
            : null;
    }
}
