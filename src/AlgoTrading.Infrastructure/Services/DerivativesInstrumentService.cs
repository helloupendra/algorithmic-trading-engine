// src/AlgoTrading.Infrastructure/Services/DerivativesInstrumentService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Instruments;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Service for resolving options chains and derivative expiries from the locally synced master database.
/// Helps strategies discover the correct tradable symbols (e.g., ATM, OTM strikes) based on the underlying spot.
/// </summary>
public class DerivativesInstrumentService : IDerivativesInstrumentService
{
    private readonly TradingDbContext _dbContext;

    public DerivativesInstrumentService(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string?> GetNearestFutureSymbolAsync(
        string underlying,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(underlying)) return null;

        var key = underlying.Trim().ToUpperInvariant();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Matched on Underlying rather than a symbol prefix: "CRUDEOIL%" also
        // catches CRUDEOILM, whose contract is a tenth of the size, and picking
        // the mini by accident would size every trade wrong.
        return await _dbContext.Instruments
            .AsNoTracking()
            .Where(x => x.IsEnabled
                        && x.Underlying == key
                        && x.InstrumentType == "FUT"
                        && x.ExpiryDate.HasValue
                        && x.ExpiryDate >= today)
            .OrderBy(x => x.ExpiryDate)
            .Select(x => x.Symbol)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DerivativeExpiryResponse>> GetExpiriesAsync(
        string underlying,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            throw new ArgumentException("Underlying is required.", nameof(underlying));

        // Option expiries only. Every caller of this - the runner picking the
        // expiry to trade, the chain tracker, the launch dialog - is asking
        // "which option expiries exist", and a futures expiry answers a
        // different question. On NSE the two coincide, so including futures
        // never showed; on MCX they do not. CRUDEOIL options expire on the
        // 17th and the future on the 21st, and for those four days the runner
        // picked the 21st, found no CE or PE at any strike, and ran without
        // ever being able to trade.
        var rows = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x =>
                x.IsEnabled &&
                x.Underlying == underlying &&
                x.ExpiryDate.HasValue &&
                (x.InstrumentType == "CE" || x.InstrumentType == "PE"))
            .Select(x => x.ExpiryDate!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new DerivativeExpiryResponse
        {
            Underlying = underlying,
            ExpiryDate = x
        }).ToList();
    }

    public async Task<IReadOnlyList<OptionChainItemResponse>> GetOptionChainAsync(
        string underlying,
        DateOnly expiryDate,
        decimal? fromStrike = null,
        decimal? toStrike = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            throw new ArgumentException("Underlying is required.", nameof(underlying));

        var query = _dbContext.Instruments
            .AsNoTracking()
            .Where(x =>
                x.IsEnabled &&
                x.Underlying == underlying &&
                x.ExpiryDate == expiryDate &&
                (x.OptionType == "CE" || x.OptionType == "PE"));

        if (fromStrike.HasValue)
            query = query.Where(x => x.StrikePrice >= fromStrike.Value);

        if (toStrike.HasValue)
            query = query.Where(x => x.StrikePrice <= toStrike.Value);

        var rows = await query
            .OrderBy(x => x.StrikePrice)
            .ThenBy(x => x.OptionType)
            .Select(x => new OptionChainItemResponse
            {
                Symbol = x.Symbol,
                Underlying = x.Underlying,
                ExpiryDate = x.ExpiryDate,
                StrikePrice = x.StrikePrice,
                OptionType = x.OptionType,
                InstrumentType = x.InstrumentType,
                Description = x.Description
            })
            .ToListAsync(cancellationToken);

        return rows;
    }

    public async Task<OptionChainItemResponse?> GetExactContractAsync(
        string underlying,
        DateOnly expiryDate,
        decimal strike,
        string optionType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            throw new ArgumentException("Underlying is required.", nameof(underlying));

        if (string.IsNullOrWhiteSpace(optionType))
            throw new ArgumentException("OptionType is required.", nameof(optionType));

        string normalizedOptionType = optionType.Trim().ToUpperInvariant();

        var row = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x =>
                x.IsEnabled &&
                x.Underlying == underlying &&
                x.ExpiryDate == expiryDate &&
                x.StrikePrice == strike &&
                x.OptionType == normalizedOptionType)
            .Select(x => new OptionChainItemResponse
            {
                Symbol = x.Symbol,
                Underlying = x.Underlying,
                ExpiryDate = x.ExpiryDate,
                StrikePrice = x.StrikePrice,
                OptionType = x.OptionType,
                InstrumentType = x.InstrumentType,
                Description = x.Description
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row;
    }
}