using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
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

    /// <summary>Charges per run for the filled orders a query chooses (a day's, say).</summary>
    public Task<Dictionary<long, decimal>> ForOrdersAsync(IQueryable<PaperOrder> orders, CancellationToken cancellationToken)
        => LoadAsync(orders, cancellationToken);

    /// <summary>One run's charges so far.</summary>
    public async Task<decimal> ForRunAsync(long runId, CancellationToken cancellationToken)
    {
        var charges = await LoadAsync(
            _dbContext.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == runId), cancellationToken);
        return charges.GetValueOrDefault(runId);
    }

    /// <remarks>The arithmetic is <see cref="FillCharges"/>'s, which the risk gate's daily loss shares.</remarks>
    private Task<Dictionary<long, decimal>> LoadAsync(IQueryable<PaperOrder> orders, CancellationToken cancellationToken)
        => FillCharges.PerRunAsync(orders, _lotSizeResolver, cancellationToken);

    /// <summary>The lot size a fill was booked at: 1 when the symbol is unknown, as in PaperTradingService.</summary>
    internal static int LotSizeOf(IReadOnlyDictionary<string, LotSizeInfo> lotSizes, string symbol)
        => FillCharges.LotSizeOf(lotSizes, symbol);
}
