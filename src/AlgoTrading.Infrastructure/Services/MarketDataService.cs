using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// History sync and stored-history reads, with no idea which vendor is behind
/// them: it asks <see cref="IProviderRouter"/> who serves history, hands the bars
/// to <see cref="IHistoricalCandleStore"/>, and stamps every row with the source.
/// </summary>
public class MarketDataService : IMarketDataService
{
    private readonly IProviderRouter _router;
    private readonly IHistoricalCandleStore _candleStore;
    private readonly TradingDbContext _dbContext;
    private readonly ILogger<MarketDataService> _logger;

    public MarketDataService(
        IProviderRouter router,
        IHistoricalCandleStore candleStore,
        TradingDbContext dbContext,
        ILogger<MarketDataService> logger)
    {
        _router = router;
        _candleStore = candleStore;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CandleResponse>> SyncHistoryAsync(
        SyncHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(request));
        }

        string resolution = ResolutionCodes.ToCandle(request.Resolution);

        // The request speaks in dates; providers speak in instants. The to-date is
        // taken as the whole day so a same-day sync still gets that session — the
        // inclusive behaviour every caller already relies on.
        DateTime fromUtc = request.FromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        DateTime toUtc = request.ToDate.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc);

        var provider = await _router.ResolveDataAsync(
            ProviderCapability.History,
            cancellationToken: cancellationToken);

        var bars = await provider.GetHistoryAsync(
            request.Symbol,
            resolution,
            fromUtc,
            toUtc,
            cancellationToken);

        var upsert = await _candleStore.UpsertAsync(
            request.Symbol,
            resolution,
            bars,
            provider.Descriptor.Key,
            cancellationToken);

        _logger.LogInformation(
            "History sync for {Symbol} ({Resolution}) from {Source} -> fetched {Fetched}, inserted {Inserted}, updated {Updated}, skipped {Skipped}.",
            request.Symbol,
            resolution,
            provider.Descriptor.Key,
            bars.Count,
            upsert.Inserted,
            upsert.Updated,
            upsert.Skipped);

        return bars
            .Select(x => new CandleResponse
            {
                Symbol = request.Symbol,
                Resolution = resolution,
                TimestampUtc = x.TimestampUtc,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = (long)x.Volume,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<CandleResponse>> GetStoredHistoryAsync(
        GetStoredCandlesRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(request.Symbol));
        }

        string resolution = ResolutionCodes.ToCandle(request.Resolution);

        var query = _dbContext.Candles
            .AsNoTracking()
            .Where(x => x.Symbol == request.Symbol && x.Resolution == resolution);

        if (request.FromDate.HasValue)
        {
            DateTime fromUtc = request.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.TimeStampUtc >= fromUtc);
        }

        if (request.ToDate.HasValue)
        {
            DateTime toUtcExclusive = request.ToDate.Value
                .AddDays(1)
                .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            query = query.Where(x => x.TimeStampUtc < toUtcExclusive);
        }

        return await query
            .OrderBy(x => x.TimeStampUtc)
            .Select(x => new CandleResponse
            {
                Symbol = x.Symbol,
                Resolution = x.Resolution,
                TimestampUtc = x.TimeStampUtc,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = x.Volume,
            })
            .ToListAsync(cancellationToken);
    }
}
