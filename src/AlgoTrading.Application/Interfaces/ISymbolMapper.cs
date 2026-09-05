namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Translates between the platform's canonical symbol and whatever a vendor
/// calls the same instrument.
/// </summary>
/// <remarks>
/// The canonical symbol is the string already stored everywhere in this platform
/// — <c>NSE:BANKNIFTY26SEP57500CE</c>. It happens to match FYERS grammar because
/// FYERS was the first connector, but it belongs to us now: every other vendor
/// gets a row in <c>instrument_vendor_symbols</c> and is translated at the
/// adapter boundary, so nothing above an adapter ever sees a vendor symbol.
/// A provider whose descriptor sets <c>UsesCanonicalSymbols</c> needs no rows at
/// all — the mapping is the identity and the lookup is skipped.
/// </remarks>
public interface ISymbolMapper
{
    /// <summary>Canonical → vendor. Falls back to the canonical string when no mapping exists.</summary>
    Task<string> ToVendorAsync(
        string canonicalSymbol,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>Vendor → canonical. Falls back to the vendor string when no mapping exists.</summary>
    Task<string> FromVendorAsync(
        string vendorSymbol,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>Batch form of <see cref="ToVendorAsync"/>, keyed by canonical symbol.</summary>
    Task<IReadOnlyDictionary<string, string>> ToVendorManyAsync(
        IReadOnlyCollection<string> canonicalSymbols,
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records (or replaces) one mapping. Used by an adapter's instrument import
    /// when it first learns a vendor's name for an instrument we already track.
    /// </summary>
    Task MapAsync(
        string canonicalSymbol,
        string providerKey,
        string vendorSymbol,
        CancellationToken cancellationToken = default);
}
