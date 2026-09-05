using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Persistence
{
    public class HistoricalCandleStore : IHistoricalCandleStore
    {
        private readonly TradingDbContext _dbContext;

        public HistoricalCandleStore(TradingDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<CandleUpsertResult> UpsertAsync(
            string symbol,
            string resolution,
            IReadOnlyList<ProviderHistoryBar> candles,
            string sourceKey,
            CancellationToken cancellationToken = default)
        {
            var result = new CandleUpsertResult();
            if (candles == null || candles.Count == 0)
                return result;

            // Stored under the canonical code so a "1m" option backfill and a "1"
            // index sync land under the same key.
            resolution = ResolutionCodes.ToCandle(resolution);

            // Keep the last copy of any repeated timestamp: two entities with the
            // same key in one AddRange violate the unique index and roll back
            // the whole batch. (FYERS repeats the in-progress bar around a range
            // boundary, so this is not hypothetical.)
            var entitiesToInsert = candles
                .GroupBy(c => c.TimestampUtc)
                .Select(g => g.Last())
                .OrderBy(c => c.TimestampUtc)
                .Select(c => new Candle
                {
                    Symbol = symbol,
                    Resolution = resolution,
                    TimeStampUtc = c.TimestampUtc,
                    Open = c.Open,
                    High = c.High,
                    Low = c.Low,
                    Close = c.Close,
                    Volume = (long)c.Volume,
                    SourceKey = sourceKey,
                }).ToList();

            var timestamps = entitiesToInsert.Select(x => x.TimeStampUtc).ToList();

            var existingCandles = await _dbContext.Candles
                .Where(x =>
                    x.Symbol == symbol &&
                    x.Resolution == resolution &&
                    timestamps.Contains(x.TimeStampUtc))
                .ToListAsync(cancellationToken);

            var existingMap = existingCandles.ToDictionary(x => x.TimeStampUtc);

            var newEntities = new List<Candle>();

            foreach (var incoming in entitiesToInsert)
            {
                if (!existingMap.TryGetValue(incoming.TimeStampUtc, out var existing))
                {
                    newEntities.Add(incoming);
                    result.Inserted++;
                }
                else
                {
                    bool changed =
                        existing.Open != incoming.Open ||
                        existing.High != incoming.High ||
                        existing.Low != incoming.Low ||
                        existing.Close != incoming.Close ||
                        existing.Volume != incoming.Volume ||
                        existing.SourceKey != incoming.SourceKey;

                    if (changed)
                    {
                        existing.Open = incoming.Open;
                        existing.High = incoming.High;
                        existing.Low = incoming.Low;
                        existing.Close = incoming.Close;
                        existing.Volume = incoming.Volume;

                        // Whoever last wrote the row owns it: a value that changed
                        // after a failover must not still name the old source.
                        existing.SourceKey = incoming.SourceKey;

                        result.Updated++;
                    }
                    else
                    {
                        result.Skipped++;
                    }
                }
            }

            if (newEntities.Count > 0)
            {
                await _dbContext.Candles.AddRangeAsync(newEntities, cancellationToken);
            }

            if (newEntities.Count > 0 || result.Updated > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }
}
