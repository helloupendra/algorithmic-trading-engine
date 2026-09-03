// src/AlgoTrading.Application/Interfaces/ILotSizeResolver.cs
namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// The lot size that applies to a symbol, and where the number came from:
/// "master" (Instruments.LotSize from the broker master), "configured" (the
/// LotSizes appsettings section keyed by underlying) or "unknown" (1).
/// </summary>
public sealed record LotSizeInfo(int LotSize, string Source, string Underlying)
{
    public const string SourceMaster = "master";
    public const string SourceConfigured = "configured";
    public const string SourceUnknown = "unknown";
}

/// <summary>
/// Resolves contract lot sizes so that every tier agrees on
/// units = lots x lotSize and P&amp;L = priceDiff x lots x lotSize.
/// </summary>
public interface ILotSizeResolver
{
    /// <summary>
    /// Lot size for one exact symbol (option, future, index or equity).
    /// </summary>
    Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lot sizes for many symbols in one round trip. Every requested symbol is
    /// present in the result.
    /// </summary>
    Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lot size for an underlying (e.g. "BANKNIFTY"): the master value of its
    /// nearest live contract when available, else the configured fallback.
    /// </summary>
    Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default);
}
