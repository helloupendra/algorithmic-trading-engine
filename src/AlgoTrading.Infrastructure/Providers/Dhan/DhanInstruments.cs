using System.Globalization;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// One Dhan instrument, as its requests need it: the exchange segment name, the
/// numeric security id, and the instrument type the history API asks for.
/// </summary>
public sealed record DhanInstrument(string Segment, long SecurityId, string InstrumentType)
{
    /// <summary>"NSE_FNO:47317:OPTIDX" — how a mapping row stores it.</summary>
    public string ToVendorSymbol() =>
        $"{Segment}:{SecurityId.ToString(CultureInfo.InvariantCulture)}:{InstrumentType}";

    /// <summary>The inverse of <see cref="ToVendorSymbol"/>; null for anything else.</summary>
    public static DhanInstrument? Parse(string? vendorSymbol)
    {
        if (string.IsNullOrWhiteSpace(vendorSymbol)) return null;
        var parts = vendorSymbol.Split(':');
        if (parts.Length != 3 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long id) ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            return null;
        }

        return new DhanInstrument(parts[0], id, parts[2]);
    }

    /// <summary>Futures and options carry open interest; indices and equities do not.</summary>
    public bool HasOpenInterest =>
        InstrumentType.StartsWith("FUT", StringComparison.OrdinalIgnoreCase) ||
        InstrumentType.StartsWith("OPT", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The vocabulary shared by both sides of the translation between the platform's
/// canonical symbols and Dhan's segment + security id.
/// </summary>
public static class DhanInstruments
{
    public const string IndexSegment = "IDX_I";
    public const string NseEquity = "NSE_EQ";
    public const string NseFno = "NSE_FNO";
    public const string BseEquity = "BSE_EQ";
    public const string BseFno = "BSE_FNO";
    public const string McxCommodity = "MCX_COMM";

    /// <summary>
    /// The indices, by canonical symbol. Fixed ids, read from Dhan's instrument
    /// master on 2026-09-14 (segment "I"). Note the trap this table avoids: the
    /// option rows of the master name NIFTY's underlying 26000, but the index
    /// itself, the option chain and the feed all use 13.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, DhanInstrument> Indices =
        new Dictionary<string, DhanInstrument>(StringComparer.OrdinalIgnoreCase)
        {
            ["NSE:NIFTY50-INDEX"] = new(IndexSegment, 13, "INDEX"),
            ["NSE:NIFTYBANK-INDEX"] = new(IndexSegment, 25, "INDEX"),
            ["NSE:FINNIFTY-INDEX"] = new(IndexSegment, 27, "INDEX"),
            ["NSE:MIDCPNIFTY-INDEX"] = new(IndexSegment, 442, "INDEX"),
            ["BSE:SENSEX-INDEX"] = new(IndexSegment, 51, "INDEX"),
            ["BSE:BANKEX-INDEX"] = new(IndexSegment, 69, "INDEX"),
        };

    /// <summary>Option-chain underlyings by the name traders use.</summary>
    public static readonly IReadOnlyDictionary<string, DhanInstrument> IndexUnderlyings =
        new Dictionary<string, DhanInstrument>(StringComparer.OrdinalIgnoreCase)
        {
            ["NIFTY"] = Indices["NSE:NIFTY50-INDEX"],
            ["BANKNIFTY"] = Indices["NSE:NIFTYBANK-INDEX"],
            ["FINNIFTY"] = Indices["NSE:FINNIFTY-INDEX"],
            ["MIDCPNIFTY"] = Indices["NSE:MIDCPNIFTY-INDEX"],
            ["SENSEX"] = Indices["BSE:SENSEX-INDEX"],
            ["BANKEX"] = Indices["BSE:BANKEX-INDEX"],
        };

    /// <summary>
    /// The instrument master's exchange and segment letter → the segment name
    /// requests use. Null for segments this platform does not trade (currency,
    /// NSE's own commodity segment).
    /// </summary>
    public static string? SegmentOf(string exchange, string segmentLetter) =>
        (exchange.Trim().ToUpperInvariant(), segmentLetter.Trim().ToUpperInvariant()) switch
        {
            ("NSE", "I") or ("BSE", "I") => IndexSegment,
            ("NSE", "E") => NseEquity,
            ("BSE", "E") => BseEquity,
            ("NSE", "D") => NseFno,
            ("BSE", "D") => BseFno,
            ("MCX", "M") => McxCommodity,
            _ => null,
        };
}
