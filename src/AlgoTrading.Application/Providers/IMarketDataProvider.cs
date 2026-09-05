namespace AlgoTrading.Application.Providers;

/// <summary>
/// One historical bar as a data provider returned it. Vendor-neutral: whoever
/// implements <see cref="IMarketDataProvider"/> is responsible for converting
/// its own payload into this shape, in UTC, before it crosses the seam.
/// </summary>
public sealed class ProviderHistoryBar
{
    public DateTime TimestampUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }

    /// <summary>Null when the provider does not serve open interest for this instrument.</summary>
    public long? OpenInterest { get; set; }
}

/// <summary>
/// Raised when a provider refused one specific symbol — an expired contract, a
/// symbol the vendor does not list — as opposed to a transport or auth failure.
/// The caller skips that symbol and keeps going; anything else aborts the run.
/// </summary>
/// <remarks>
/// Derives from <see cref="InvalidOperationException"/> so the existing callers
/// that answer 400 for a broker-side refusal keep behaving exactly as they do
/// today; a caller that wants to skip just this symbol catches the narrower type.
/// </remarks>
public class ProviderSymbolRejectedException : InvalidOperationException
{
    public ProviderSymbolRejectedException(string providerKey, string symbol, string reason)
        : base($"{providerKey} rejected '{symbol}': {reason}")
    {
        ProviderKey = providerKey;
        Symbol = symbol;
        Reason = reason;
    }

    public string ProviderKey { get; }
    public string Symbol { get; }
    public string Reason { get; }
}

/// <summary>
/// A source of market data. Implementations talk to exactly one vendor and know
/// nothing about the database: fetching and persisting are separate jobs, so a
/// second vendor never has to re-implement the upsert logic.
/// </summary>
/// <remarks>
/// Symbols crossing this interface are always the platform's <em>canonical</em>
/// symbol. The implementation translates to and from its own grammar through
/// <see cref="AlgoTrading.Application.Interfaces.ISymbolMapper"/> at its own boundary.
/// </remarks>
public interface IMarketDataProvider
{
    ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Historical bars for one canonical symbol. Returns an empty list when the
    /// range simply holds no data; throws <see cref="ProviderSymbolRejectedException"/>
    /// when the vendor rejects the symbol itself.
    /// </summary>
    Task<IReadOnlyList<ProviderHistoryBar>> GetHistoryAsync(
        string canonicalSymbol,
        string resolution,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default);
}
