// src/AlgoTrading.Infrastructure/Services/LotSizeResolver.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Resolves lot sizes in priority order:
/// 1. Instruments.LotSize for the exact symbol (from the broker master),
/// 2. the configured LotSizes fallback for the symbol's underlying,
/// 3. 1 (source "unknown").
/// </summary>
public sealed class LotSizeResolver : ILotSizeResolver
{
    private readonly TradingDbContext _dbContext;
    private readonly LotSizeOptions _options;

    public LotSizeResolver(TradingDbContext dbContext, LotSizeOptions options)
    {
        _dbContext = dbContext;
        _options = options;
    }

    public async Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return new LotSizeInfo(1, LotSizeInfo.SourceUnknown, string.Empty);

        var many = await ResolveManyAsync(new[] { symbol }, cancellationToken);
        return many.TryGetValue(symbol, out var info)
            ? info
            : new LotSizeInfo(1, LotSizeInfo.SourceUnknown, UnderlyingCatalog.InferUnderlying(symbol));
    }

    public async Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(
        IEnumerable<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var wanted = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var result = new Dictionary<string, LotSizeInfo>(StringComparer.Ordinal);
        if (wanted.Count == 0) return result;

        var rows = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x => wanted.Contains(x.Symbol))
            .Select(x => new { x.Symbol, x.LotSize, x.Underlying })
            .ToListAsync(cancellationToken);

        var bySymbol = rows
            .GroupBy(x => x.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var symbol in wanted)
        {
            bySymbol.TryGetValue(symbol, out var row);

            var underlying = !string.IsNullOrWhiteSpace(row?.Underlying)
                ? row!.Underlying.Trim().ToUpperInvariant()
                : UnderlyingCatalog.InferUnderlying(symbol);

            if (row?.LotSize is > 0)
            {
                result[symbol] = new LotSizeInfo(row.LotSize.Value, LotSizeInfo.SourceMaster, underlying);
                continue;
            }

            result[symbol] = FromConfiguration(underlying);
        }

        return result;
    }

    public async Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default)
    {
        var key = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        if (key.Length == 0) return new LotSizeInfo(1, LotSizeInfo.SourceUnknown, key);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Nearest live contract wins: lot sizes change at expiry boundaries and the
        // master carries the new value on the new contracts first. Within that
        // expiry take the largest lot, which is deterministic and matches the
        // underlyings endpoint (Max(LotSize) of the nearest expiry).
        var masterLot = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x => x.Underlying == key && x.LotSize > 0 && x.ExpiryDate.HasValue && x.ExpiryDate >= today)
            .OrderBy(x => x.ExpiryDate)
            .ThenByDescending(x => x.LotSize)
            .Select(x => x.LotSize)
            .FirstOrDefaultAsync(cancellationToken);

        if (masterLot is > 0)
            return new LotSizeInfo(masterLot.Value, LotSizeInfo.SourceMaster, key);

        return FromConfiguration(key);
    }

    private LotSizeInfo FromConfiguration(string underlying)
    {
        if (!string.IsNullOrWhiteSpace(underlying) && _options.TryGet(underlying, out var configured) && configured > 0)
            return new LotSizeInfo(configured, LotSizeInfo.SourceConfigured, underlying);

        return new LotSizeInfo(1, LotSizeInfo.SourceUnknown, underlying);
    }
}
