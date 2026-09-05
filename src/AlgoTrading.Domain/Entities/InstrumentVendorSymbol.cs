namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One vendor's name for an instrument the platform already knows by its
/// canonical symbol. This is the whole of the multi-vendor identity story: the
/// price tables keep their canonical string key, and each adapter translates
/// through this table at its own boundary.
/// </summary>
/// <remarks>
/// A provider that speaks canonical symbols (FYERS, whose grammar the canonical
/// form was taken from) needs no rows here at all.
/// </remarks>
public class InstrumentVendorSymbol
{
    public long Id { get; set; }

    /// <summary>Provider key, e.g. "dhan". Lowercase, matches the descriptor.</summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>The platform symbol, e.g. "NSE:BANKNIFTY26SEP57500CE".</summary>
    public string CanonicalSymbol { get; set; } = string.Empty;

    /// <summary>Whatever that vendor calls it — a different string, or a numeric token.</summary>
    public string VendorSymbol { get; set; } = string.Empty;

    /// <summary>
    /// Optional link to the instrument master. Nullable on purpose: indices and
    /// synthetic symbols are tracked and priced without ever being in the master.
    /// </summary>
    public long? InstrumentId { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
