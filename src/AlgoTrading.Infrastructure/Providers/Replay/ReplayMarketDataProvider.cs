using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers.Replay;

/// <summary>
/// Serves history from the platform's own <c>candles</c> table.
/// </summary>
/// <remarks>
/// It exists for three reasons. It is a second <see cref="IMarketDataProvider"/>,
/// which is the only real proof that the provider seam works. It needs no vendor
/// account, so a backtest or a coverage check can run when the broker token has
/// expired — the failure that costs the most time on a trading morning. And it
/// costs nothing to keep.
/// </remarks>
public class ReplayMarketDataProvider : IMarketDataProvider
{
    private readonly TradingDbContext _dbContext;
    private readonly ILogger<ReplayMarketDataProvider> _logger;

    public ReplayMarketDataProvider(
        TradingDbContext dbContext,
        ILogger<ReplayMarketDataProvider> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public ProviderDescriptor Descriptor => ReplayProvider.Descriptor;

    public async Task<IReadOnlyList<ProviderHistoryBar>> GetHistoryAsync(
        string canonicalSymbol,
        string resolution,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(canonicalSymbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(canonicalSymbol));
        }

        // No symbol mapping: these rows were written by this platform, so they are
        // canonical by definition.
        string storedResolution = ResolutionCodes.ToCandle(resolution);

        DateTime from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        DateTime to = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);

        var bars = await _dbContext.Candles
            .AsNoTracking()
            .Where(x =>
                x.Symbol == canonicalSymbol &&
                x.Resolution == storedResolution &&
                x.TimeStampUtc >= from &&
                x.TimeStampUtc <= to)
            .OrderBy(x => x.TimeStampUtc)
            .Select(x => new ProviderHistoryBar
            {
                TimestampUtc = x.TimeStampUtc,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = x.Volume,

                // The candles table stores no open interest. Null, not zero: a
                // fabricated zero is what the OI rule used to trade on.
                OpenInterest = null,
            })
            .ToListAsync(cancellationToken);

        if (bars.Count == 0)
        {
            // An empty range is not a rejected symbol — this connector has no
            // opinion about which symbols exist, only about what it has stored.
            _logger.LogDebug(
                "Replay has no stored {Resolution} candles for {Symbol} between {From:o} and {To:o}.",
                storedResolution, canonicalSymbol, from, to);
        }

        return bars;
    }
}
