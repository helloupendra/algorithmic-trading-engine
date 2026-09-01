// src/AlgoTrading.Worker.MarketData/Processing/TickBatchProcessor.cs
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Worker.MarketData.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Worker.MarketData.Processing;

public class TickBatchProcessor : ITickBatchProcessor
{
    private readonly TradingDbContext _dbContext;
    private readonly ILogger<TickBatchProcessor> _logger;

    public TickBatchProcessor(
        TradingDbContext dbContext,
        ILogger<TickBatchProcessor> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task ProcessAsync(
        IReadOnlyList<MarketTickStreamMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages is null || messages.Count == 0)
            return;

        var normalized = messages
            .Where(x => !string.IsNullOrWhiteSpace(x.Symbol))
            .Select(Normalize)
            .ToList();

        if (normalized.Count == 0)
            return;

        var symbols = normalized
            .Select(x => x.Symbol)
            .Distinct()
            .ToList();

        using var tx = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // 1) Append raw tick archive (Skip replay ticks!)
        var liveTicks = normalized.Where(x => !x.IsReplay).ToList();
        if (liveTicks.Count > 0)
        {
            var tickEntities = liveTicks.Select(x => new MarketTick
            {
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
                PrevClose = x.Close, // rename if your schema uses Close directly
                Volume = x.Volume,
                RawPayload = x.RawPayload,
                ReceivedUtc = x.ReceivedUtc ?? DateTime.UtcNow
            }).ToList();

            await _dbContext.MarketTicks.AddRangeAsync(tickEntities, cancellationToken);
        }

        // 2) Upsert latest quote projection
        var existingLatest = await _dbContext.LiveQuotesLatest
            .Where(x => symbols.Contains(x.Symbol))
            .ToListAsync(cancellationToken);

        var existingBySymbol = existingLatest.ToDictionary(x => x.Symbol, x => x);

        foreach (var msg in normalized)
        {
            if (existingBySymbol.TryGetValue(msg.Symbol, out var latest))
            {
                latest.DataType = string.IsNullOrWhiteSpace(msg.DataType) ? latest.DataType : msg.DataType;
                latest.LastTradedPrice = msg.LastTradedPrice;
                latest.Open = msg.Open;
                latest.High = msg.High;
                latest.Low = msg.Low;
                latest.Close = msg.Close;
                latest.Volume = (long?)msg.Volume;
                latest.UpdatedUtc = msg.ExchangeTimestampUtc ?? msg.ReceivedUtc ?? DateTime.UtcNow;
            }
            else
            {
                _dbContext.LiveQuotesLatest.Add(new LiveQuoteLatest
                {
                    Symbol = msg.Symbol,
                    DataType = string.IsNullOrWhiteSpace(msg.DataType) ? "symbolUpdate" : msg.DataType,
                    LastTradedPrice = msg.LastTradedPrice,
                    Open = msg.Open,
                    High = msg.High,
                    Low = msg.Low,
                    Close = msg.Close,
                    Volume = (long?)msg.Volume,
                    UpdatedUtc = msg.ExchangeTimestampUtc ?? msg.ReceivedUtc ?? DateTime.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        _logger.LogInformation("Processed tick batch of {Count} messages", normalized.Count);
    }

    private static MarketTickStreamMessage Normalize(MarketTickStreamMessage x)
    {
        x.Symbol = x.Symbol.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(x.Exchange))
            x.Exchange = x.Exchange.Trim().ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(x.DataType))
            x.DataType = x.DataType.Trim();

        x.ReceivedUtc = EnsureUtc(x.ReceivedUtc);
        x.ExchangeTimestampUtc = EnsureUtc(x.ExchangeTimestampUtc);

        return x;
    }

    private static DateTime? EnsureUtc(DateTime? dt)
    {
        if (!dt.HasValue) return null;
        if (dt.Value.Kind == DateTimeKind.Utc) return dt;
        
        // If it's local or unspecified, forcibly treat it as UTC for PostgreSQL
        return DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc);
    }
}