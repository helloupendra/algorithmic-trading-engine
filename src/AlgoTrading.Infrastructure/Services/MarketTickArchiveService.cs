// src/AlgoTrading.Infrastructure/Services/MarketTickArchiveService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

public class MarketTickArchiveService : IMarketTickArchiveService
{
    private readonly TradingDbContext _dbContext;

    public MarketTickArchiveService(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ArchiveAsync(
        MarketTickArchiveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Symbol))
            throw new InvalidOperationException("Symbol is required.");

        var entity = new MarketTick
        {
            Symbol = request.Symbol.Trim().ToUpperInvariant(),
            DataType = string.IsNullOrWhiteSpace(request.DataType)
                ? "symbolUpdate"
                : request.DataType.Trim(),

            ExchangeTimestampUtc = request.ExchangeTimestampUtc,

            LastTradedPrice = request.LastTradedPrice,
            BidPrice = request.BidPrice,
            AskPrice = request.AskPrice,

            BidSize = request.BidSize,
            AskSize = request.AskSize,

            Open = request.Open,
            High = request.High,
            Low = request.Low,
            PrevClose = request.PrevClose,

            Volume = request.Volume,
            RawPayload = request.RawPayload ?? string.Empty,

            ReceivedUtc = DateTime.UtcNow
        };

        await _dbContext.MarketTicks.AddAsync(entity, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MarketTickDto>> GetRangeAsync(
        string symbol,
        DateTime fromUtc,
        DateTime toUtc,
        int take,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new InvalidOperationException("Symbol is required.");

        if (fromUtc >= toUtc)
            throw new InvalidOperationException("fromUtc must be earlier than toUtc.");

        if (take <= 0)
            take = 10000;

        string normalized = symbol.Trim().ToUpperInvariant();

        var rows = await _dbContext.MarketTicks
            .AsNoTracking()
            .Where(x =>
                x.Symbol == normalized &&
                (x.ExchangeTimestampUtc ?? x.ReceivedUtc) >= fromUtc &&
                (x.ExchangeTimestampUtc ?? x.ReceivedUtc) <= toUtc)
            .OrderBy(x => x.ExchangeTimestampUtc ?? x.ReceivedUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new MarketTickDto
        {
            Id = x.Id,
            Symbol = x.Symbol,
            DataType = x.DataType,
            ExchangeTimestampUtc = x.ExchangeTimestampUtc,
            LastTradedPrice = x.LastTradedPrice,
            BidPrice = x.BidPrice,
            AskPrice = x.AskPrice,
            BidSize = x.BidSize,
            AskSize = x.AskSize,
            Open = x.Open,
            High = x.High,
            Low = x.Low,
            PrevClose = x.PrevClose,
            Volume = x.Volume,
            ReceivedUtc = x.ReceivedUtc
        }).ToList();
    }
}
