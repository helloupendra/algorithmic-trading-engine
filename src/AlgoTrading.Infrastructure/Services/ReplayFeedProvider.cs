using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Implementation of <see cref="IReplayFeedProvider"/> that supplies historical bars sequentially to the simulator.
/// For 1-minute bars it prefers live_bars (resolution "1m") and falls back to the candles table ("1");
/// every other resolution is read from candles under its canonical code.
/// </summary>
public class ReplayFeedProvider : IReplayFeedProvider
{
    private readonly TradingDbContext _dbContext;

    public ReplayFeedProvider(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<ReplayBarFrame>> LoadBarsAsync(
        string symbol,
        string resolution,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol is required.", nameof(symbol));

        if (string.IsNullOrWhiteSpace(resolution))
            throw new ArgumentException("Resolution is required.", nameof(resolution));

        if (fromUtc > toUtc)
            throw new ArgumentException("fromUtc cannot be greater than toUtc.");

        var canonical = ResolutionCodes.ToCandle(resolution);

        // First try live_bars for 1m
        if (canonical == "1")
        {
            var liveBars = await _dbContext.LiveBars
                .AsNoTracking()
                .Where(x =>
                    x.Symbol == symbol &&
                    x.Resolution == ResolutionCodes.LiveBarResolution &&
                    x.BarStartUtc >= fromUtc &&
                    x.BarStartUtc <= toUtc)
                .OrderBy(x => x.BarStartUtc)
                .Select(x => new ReplayBarFrame
                {
                    Symbol = x.Symbol,
                    Resolution = canonical,
                    TimestampUtc = x.BarStartUtc,
                    Open = x.Open,
                    High = x.High,
                    Low = x.Low,
                    Close = x.Close,
                    Volume = x.VolumeDelta
                })
                .ToListAsync(cancellationToken);

            if (liveBars.Count > 0)
                return liveBars;
        }

        // Fallback to candles

        var candles = await _dbContext.Candles
            .AsNoTracking()
            .Where(x =>
                x.Symbol == symbol &&
                x.Resolution == canonical &&
                x.TimeStampUtc >= fromUtc &&
                x.TimeStampUtc <= toUtc)
            .OrderBy(x => x.TimeStampUtc)
            .Select(x => new ReplayBarFrame
            {
                Symbol = x.Symbol,
                Resolution = x.Resolution,
                TimestampUtc = x.TimeStampUtc,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = x.Volume
            })
            .ToListAsync(cancellationToken);


        return candles;
    }
}
