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

    public async Task<IReadOnlyList<DerivativeExpiryResponse>> GetExpiriesAsync(
        string underlying,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            throw new ArgumentException("Underlying is required.", nameof(underlying));

        var rows = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x =>
                x.IsEnabled &&
                x.Underlying == underlying &&
                x.ExpiryDate.HasValue)
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