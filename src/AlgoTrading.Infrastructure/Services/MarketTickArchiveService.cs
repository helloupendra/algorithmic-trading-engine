// src/AlgoTrading.Infrastructure/Services/MarketTickArchiveService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// A symbol's ticks over a time range (GET /api/LiveData/ticks/history).
/// </summary>
/// <remarks>
/// Read from <c>live_ticks</c>. Until 2026-09-15 every tick was written a second
/// time, through a queue, into <c>market_ticks</c>, and this read that copy.
/// Compared day by day the copy held the same ticks minus the batches its writer
/// had dropped (80 on 2026-09-10, 24 on 2026-09-15) and without the vendor key,
/// at the cost of about half the database's disk. Ticks older than the database
/// keeps are in the verified Google Drive archive (scripts/archive_to_drive.py).
/// </remarks>
public class MarketTickArchiveService : IMarketTickArchiveService
{
    /// <summary>
    /// How far a tick's exchange stamp may sit from the moment it was stored. A
    /// recap replays the morning in the evening, so it is hours, never days.
    /// </summary>
    private static readonly TimeSpan StampWindow = TimeSpan.FromDays(1);

    private readonly TradingDbContext _dbContext;

    public MarketTickArchiveService(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
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
        DateTime storedFrom = fromUtc - StampWindow, storedTo = toUtc + StampWindow;

        var rows = await _dbContext.LiveTicks
            .AsNoTracking()
            // The stored-time bound lets TimescaleDB skip every chunk outside the
            // range; without it this would read the whole hypertable.
            .Where(x => x.ReceivedUtc >= storedFrom && x.ReceivedUtc <= storedTo)
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
