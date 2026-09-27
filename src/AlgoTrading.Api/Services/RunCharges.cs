using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The statutory charges of live runs' fills (<see cref="OptionCharges"/>, at
/// each contract's <see cref="ChargeSchedule"/>), one
/// figure per run, for every screen that says "net": the run history, the
/// per-account rollup, a strategy's track record and the live run page.
/// </summary>
/// <remarks>
/// <para>
/// Order quantity is in lots, so each fill's turnover is premium × lots × the
/// lot size of the contract it filled, resolved per symbol with the same
/// lookup (<see cref="ILotSizeResolver.ResolveManyAsync"/>, 1 when unknown)
/// that paper trading books realized P&amp;L with. Until 28 Sep the charges used
/// one lot size per run, taken from its underlying: the manual book (symbol
/// "MANUAL") was charged as if every lot were one unit, and a book holding
/// several underlyings at one underlying's size.
/// </para>
/// <para>
/// One place, because the run page showed gross while the history beside it
/// showed net for the same run: two copies of "net" had already drifted apart.
/// </para>
/// </remarks>
public sealed class RunCharges
{
    private readonly TradingDbContext _dbContext;
    private readonly ILotSizeResolver _lotSizeResolver;

    public RunCharges(TradingDbContext dbContext, ILotSizeResolver lotSizeResolver)
    {
        _dbContext = dbContext;
        _lotSizeResolver = lotSizeResolver;
    }

    /// <summary>Charges per run for runs chosen by a database query; a run with no filled order is absent (0).</summary>
    public Task<Dictionary<long, decimal>> ForRunsAsync(IQueryable<long> runIds, CancellationToken cancellationToken)
        => LoadAsync(_dbContext.PaperOrders.AsNoTracking().Where(o => runIds.Contains(o.SimulationRunId)), cancellationToken);

    /// <summary>
    /// Charges per run for a list of run ids. A list rather than
    /// list.AsQueryable(): an in-memory queryable inside an EF query is not
    /// something every provider translates.
    /// </summary>
    public Task<Dictionary<long, decimal>> ForRunsAsync(IReadOnlyCollection<long> runIds, CancellationToken cancellationToken)
        => LoadAsync(_dbContext.PaperOrders.AsNoTracking().Where(o => runIds.Contains(o.SimulationRunId)), cancellationToken);

    /// <summary>One run's charges so far.</summary>
    public async Task<decimal> ForRunAsync(long runId, CancellationToken cancellationToken)
    {
        var charges = await LoadAsync(
            _dbContext.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == runId), cancellationToken);
        return charges.GetValueOrDefault(runId);
    }

    private async Task<Dictionary<long, decimal>> LoadAsync(IQueryable<PaperOrder> orders, CancellationToken cancellationToken)
    {
        var rows = await orders
            .Where(o => o.FillPrice != null)
            .GroupBy(o => new { o.SimulationRunId, o.Symbol, o.Side })
            .Select(g => new
            {
                g.Key.SimulationRunId,
                g.Key.Symbol,
                g.Key.Side,
                PremiumLots = g.Sum(o => o.FillPrice!.Value * o.Quantity),
                Orders = g.Count(),
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return new Dictionary<long, decimal>();

        var lotSizes = await _lotSizeResolver.ResolveManyAsync(
            rows.Select(r => r.Symbol).Distinct(StringComparer.Ordinal), cancellationToken);

        return rows
            .GroupBy(r => r.SimulationRunId)
            .ToDictionary(g => g.Key, g =>
                // Per schedule, then summed: an MCX fill is charged at MCX's
                // rates, and one book can hold both kinds.
                g.GroupBy(r => ChargeSchedule.ForSymbol(r.Symbol)).Sum(bySchedule =>
                {
                    decimal buy = 0m, sell = 0m;
                    int orders = 0;
                    foreach (var r in bySchedule)
                    {
                        decimal rupees = r.PremiumLots * LotSizeOf(lotSizes, r.Symbol);
                        if (string.Equals(r.Side, "BUY", StringComparison.OrdinalIgnoreCase)) buy += rupees;
                        else if (string.Equals(r.Side, "SELL", StringComparison.OrdinalIgnoreCase)) sell += rupees;
                        orders += r.Orders;
                    }
                    return OptionCharges.For(buy, sell, orders, bySchedule.Key).Total;
                }));
    }

    /// <summary>The lot size a fill was booked at: 1 when the symbol is unknown, as in PaperTradingService.</summary>
    internal static int LotSizeOf(IReadOnlyDictionary<string, LotSizeInfo> lotSizes, string symbol)
        => lotSizes.TryGetValue(symbol, out var info) && info.LotSize > 0 ? info.LotSize : 1;
}
