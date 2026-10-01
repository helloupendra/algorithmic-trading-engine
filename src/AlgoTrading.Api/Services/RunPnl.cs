// src/AlgoTrading.Api/Services/RunPnl.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// A live run's P&amp;L as the console states it: realized over every
/// position, the open legs marked to market, the statutory charges of its
/// fills (<see cref="RunCharges"/>), and net = realized + unrealized − charges.
/// </summary>
/// <remarks>
/// <para>
/// The marking is the live view's: an open leg at its latest live quote,
/// valued with <see cref="PaperPnl.Unrealized"/> at the lot size the fills
/// were booked at, and at its stored unrealized P&amp;L when no quote is known.
/// It lived inside <see cref="LiveRunHistoryBuilder"/> until the minute
/// recorder (<see cref="RunPnlRecorder"/>) needed the same number; one copy,
/// so the curve the Desk draws ends where the run card and the history row
/// beside it stand.
/// </para>
/// <para>
/// Read-only: nothing is written back to the positions.
/// </para>
/// </remarks>
public sealed class RunPnl
{
    private const string OpenStatus = "Open";

    private readonly TradingDbContext _dbContext;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly RunCharges _charges;

    private readonly IMarketReplayBook? _replayBook;

    public RunPnl(TradingDbContext dbContext, ILotSizeResolver lotSizeResolver, RunCharges charges, IMarketReplayBook? replayBook = null)
    {
        _dbContext = dbContext;
        _lotSizeResolver = lotSizeResolver;
        _charges = charges;
        _replayBook = replayBook;
    }

    /// <summary>What a run's open legs are worth now, and the capital they tie up.</summary>
    public sealed record Mark(decimal Unrealized, decimal CapitalUsed)
    {
        public static readonly Mark None = new(0m, 0m);
    }

    /// <summary>A run's figures: <see cref="Net"/> is what its card shows.</summary>
    public sealed record Figures(decimal Realized, decimal Unrealized, decimal Charges)
    {
        public decimal Net => Realized + Unrealized - Charges;
    }

    /// <summary>One open leg, as much of it as marking needs.</summary>
    public sealed record OpenLeg(long RunId, string Symbol, string Direction, int Quantity, decimal AveragePrice, decimal StoredUnrealized);

    /// <summary>
    /// The open legs of <paramref name="runIds"/> marked to market, per run;
    /// every run asked about has an entry (<see cref="Mark.None"/> with no open leg).
    /// </summary>
    public async Task<Dictionary<long, Mark>> MarkOpenLegsAsync(IReadOnlyCollection<long> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return new Dictionary<long, Mark>();

        var legs = await _dbContext.PaperPositions.AsNoTracking()
            .Where(p => runIds.Contains(p.SimulationRunId) && p.Status == OpenStatus)
            .Select(p => new OpenLeg(p.SimulationRunId, p.Symbol, p.Direction, p.Quantity, p.AveragePrice, p.UnrealizedPnl))
            .ToListAsync(cancellationToken);

        return await MarkAsync(runIds, legs, cancellationToken);
    }

    /// <summary>
    /// Every run's figures, from one read of their positions, one of the
    /// quotes of the open legs, and one of their fills (the charges). Only the
    /// runs in <paramref name="liveRunIds"/> have their open legs counted: a
    /// run that has ended has no open book, as its history row says.
    /// </summary>
    public async Task<Dictionary<long, Figures>> FiguresAsync(
        IReadOnlyCollection<long> runIds,
        IReadOnlySet<long> liveRunIds,
        CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return new Dictionary<long, Figures>();

        var positions = await _dbContext.PaperPositions.AsNoTracking()
            .Where(p => runIds.Contains(p.SimulationRunId))
            .Select(p => new
            {
                p.SimulationRunId,
                p.Symbol,
                p.Direction,
                p.Quantity,
                p.AveragePrice,
                p.RealizedPnl,
                p.UnrealizedPnl,
                p.Status
            })
            .ToListAsync(cancellationToken);

        var realized = positions
            .GroupBy(p => p.SimulationRunId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.RealizedPnl));

        var liveIds = runIds.Where(liveRunIds.Contains).ToList();
        var openLegs = positions
            .Where(p => p.Status == OpenStatus && liveRunIds.Contains(p.SimulationRunId))
            .Select(p => new OpenLeg(p.SimulationRunId, p.Symbol, p.Direction, p.Quantity, p.AveragePrice, p.UnrealizedPnl))
            .ToList();
        var marks = await MarkAsync(liveIds, openLegs, cancellationToken);

        var ids = runIds.ToList();
        var charges = await _charges.ForRunsAsync(ids, cancellationToken);

        return ids.Distinct().ToDictionary(
            id => id,
            id => new Figures(
                realized.GetValueOrDefault(id),
                marks.TryGetValue(id, out var mark) ? mark.Unrealized : 0m,
                charges.GetValueOrDefault(id)));
    }

    /// <summary>
    /// Marks <paramref name="legs"/> at the latest live quote of each symbol,
    /// the stored unrealized P&amp;L standing in where no quote is known.
    /// </summary>
    private async Task<Dictionary<long, Mark>> MarkAsync(
        IReadOnlyCollection<long> runIds,
        IReadOnlyList<OpenLeg> legs,
        CancellationToken cancellationToken)
    {
        var result = runIds.Distinct().ToDictionary(id => id, _ => Mark.None);
        if (legs.Count == 0) return result;

        var symbols = legs.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();

        var quotes = await _dbContext.LiveQuotesLatest.AsNoTracking()
            .Where(q => symbols.Contains(q.Symbol))
            .Select(q => new { q.Symbol, q.LastTradedPrice })
            .ToListAsync(cancellationToken);
        var ltpBySymbol = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var q in quotes)
        {
            if (q.LastTradedPrice.HasValue) ltpBySymbol.TryAdd(q.Symbol, q.LastTradedPrice.Value);
        }

        // The recap runs of the day being replayed are marked at the replay's prices, not the live ones.
        var replayRuns = new HashSet<long>();
        if (_replayBook?.Day is not null)
        {
            var ids = runIds.Distinct().ToList();
            var parameters = await _dbContext.SimulationRuns.AsNoTracking()
                .Where(r => ids.Contains(r.Id))
                .Select(r => new { r.Id, r.ParametersJson })
                .ToListAsync(cancellationToken);
            replayRuns = parameters.Where(r => _replayBook.Prices(r.ParametersJson)).Select(r => r.Id).ToHashSet();
        }

        var lotSizes = await _lotSizeResolver.ResolveManyAsync(symbols, cancellationToken);

        foreach (var leg in legs)
        {
            int lotSize = RunCharges.LotSizeOf(lotSizes, leg.Symbol);
            // The same function the fills use. Written out by hand in the
            // history builder, this was a second copy of the number that says
            // how much a run made.
            decimal? price = replayRuns.Contains(leg.RunId)
                ? _replayBook!.Quote(leg.Symbol)?.LastTradedPrice
                : ltpBySymbol.TryGetValue(leg.Symbol, out var ltp) ? ltp : null;
            decimal unrealized = price is decimal mark
                ? PaperPnl.Unrealized(leg.Direction, leg.AveragePrice, mark, leg.Quantity, lotSize)
                : leg.StoredUnrealized;

            decimal used = PaperTradingService.UsedCapitalOf(leg.Direction, leg.Symbol, leg.AveragePrice, leg.Quantity, lotSize);

            var current = result.GetValueOrDefault(leg.RunId, Mark.None);
            result[leg.RunId] = new Mark(current.Unrealized + unrealized, current.CapitalUsed + used);
        }

        return result;
    }
}
