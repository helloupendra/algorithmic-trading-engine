using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Canonical ↔ vendor symbol translation, backed by <c>instrument_vendor_symbols</c>.
/// </summary>
/// <remarks>
/// A provider whose descriptor sets <c>UsesCanonicalSymbols</c> — FYERS, whose
/// grammar the canonical form was taken from — short-circuits to the identity
/// mapping without touching the database, so this seam costs nothing until a
/// vendor with a different grammar actually arrives.
/// </remarks>
public class SymbolMapper : ISymbolMapper
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private readonly TradingDbContext _dbContext;
    private readonly IProviderCatalog _catalog;
    private readonly IMemoryCache _cache;

    public SymbolMapper(TradingDbContext dbContext, IProviderCatalog catalog, IMemoryCache cache)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _cache = cache;
    }

    // Metadata only — asking the registry for adapter instances here would make
    // the graph circular, since adapters depend on this mapper.
    private bool SpeaksCanonical(string providerKey)
        => _catalog.Find(providerKey)?.Capabilities.UsesCanonicalSymbols ?? false;

    public async Task<string> ToVendorAsync(
        string canonicalSymbol,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(canonicalSymbol) || SpeaksCanonical(providerKey))
        {
            return canonicalSymbol;
        }

        string cacheKey = $"symbolmap:{providerKey}:to:{canonicalSymbol}";

        if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return cached;
        }

        string? vendorSymbol = await _dbContext.InstrumentVendorSymbols
            .AsNoTracking()
            .Where(x => x.ProviderKey == providerKey && x.CanonicalSymbol == canonicalSymbol)
            .Select(x => x.VendorSymbol)
            .FirstOrDefaultAsync(cancellationToken);

        // No row means the vendor uses the same string for this instrument. That
        // is a fallback, not a guess: an adapter whose grammar differs registers
        // its mappings when it imports its instrument master.
        string resolved = vendorSymbol ?? canonicalSymbol;

        _cache.Set(cacheKey, resolved, CacheLifetime);

        return resolved;
    }

    public async Task<string> FromVendorAsync(
        string vendorSymbol,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(vendorSymbol) || SpeaksCanonical(providerKey))
        {
            return vendorSymbol;
        }

        string cacheKey = $"symbolmap:{providerKey}:from:{vendorSymbol}";

        if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return cached;
        }

        string? canonical = await _dbContext.InstrumentVendorSymbols
            .AsNoTracking()
            .Where(x => x.ProviderKey == providerKey && x.VendorSymbol == vendorSymbol)
            .Select(x => x.CanonicalSymbol)
            .FirstOrDefaultAsync(cancellationToken);

        string resolved = canonical ?? vendorSymbol;

        _cache.Set(cacheKey, resolved, CacheLifetime);

        return resolved;
    }

    public async Task<IReadOnlyDictionary<string, string>> ToVendorManyAsync(
        IReadOnlyCollection<string> canonicalSymbols,
        string providerKey,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        if (canonicalSymbols.Count == 0)
        {
            return result;
        }

        if (SpeaksCanonical(providerKey))
        {
            foreach (var symbol in canonicalSymbols)
            {
                result[symbol] = symbol;
            }

            return result;
        }

        var rows = await _dbContext.InstrumentVendorSymbols
            .AsNoTracking()
            .Where(x => x.ProviderKey == providerKey && canonicalSymbols.Contains(x.CanonicalSymbol))
            .Select(x => new { x.CanonicalSymbol, x.VendorSymbol })
            .ToListAsync(cancellationToken);

        var mapped = rows.ToDictionary(x => x.CanonicalSymbol, x => x.VendorSymbol, StringComparer.Ordinal);

        foreach (var symbol in canonicalSymbols)
        {
            result[symbol] = mapped.TryGetValue(symbol, out var vendor) ? vendor : symbol;
        }

        return result;
    }

    public async Task MapAsync(
        string canonicalSymbol,
        string providerKey,
        string vendorSymbol,
        CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.InstrumentVendorSymbols
            .FirstOrDefaultAsync(
                x => x.ProviderKey == providerKey && x.CanonicalSymbol == canonicalSymbol,
                cancellationToken);

        var now = DateTime.UtcNow;

        if (row is null)
        {
            row = new InstrumentVendorSymbol
            {
                ProviderKey = providerKey,
                CanonicalSymbol = canonicalSymbol,
                CreatedUtc = now,
            };
            _dbContext.InstrumentVendorSymbols.Add(row);
        }

        row.VendorSymbol = vendorSymbol;
        row.UpdatedUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);

        _cache.Remove($"symbolmap:{providerKey}:to:{canonicalSymbol}");
        _cache.Remove($"symbolmap:{providerKey}:from:{vendorSymbol}");
    }
}
