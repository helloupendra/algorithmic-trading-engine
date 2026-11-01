using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The statutory charges of filled paper orders, per run (<see cref="OptionCharges"/>, at each contract's
/// <see cref="ChargeSchedule"/>): the arithmetic behind every "net" the platform shows (the API's
/// <c>RunCharges</c>) and behind the risk gate's daily loss (<see cref="RiskManagementService"/>).
/// </summary>
/// <remarks>
/// One copy, here, because the risk gate lives in this project and <c>RunCharges</c> in the API: a second
/// copy of "net" is how the run page and the history beside it once drifted apart.
/// </remarks>
public static class FillCharges
{
    /// <summary>
    /// Charges per run for the filled orders a query chooses; a run with no filled order is absent (0).
    /// </summary>
    /// <remarks>
    /// Order quantity is in lots, so each fill's turnover is premium × lots × the lot size of the contract it
    /// filled, resolved per symbol with <see cref="ILotSizeResolver.ResolveManyAsync"/> (1 when unknown), the
    /// lookup paper trading books realized P&amp;L with.
    /// </remarks>
    public static async Task<Dictionary<long, decimal>> PerRunAsync(
        IQueryable<PaperOrder> orders, ILotSizeResolver lotSizeResolver, CancellationToken cancellationToken)
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

        var lotSizes = await lotSizeResolver.ResolveManyAsync(
            rows.Select(r => r.Symbol).Distinct(StringComparer.Ordinal), cancellationToken);

        return rows
            .GroupBy(r => r.SimulationRunId)
            .ToDictionary(g => g.Key, g =>
                // Per schedule, then summed: an MCX fill is charged at MCX's
                // rates, and one book can hold both kinds.
                g.GroupBy(r => ChargeSchedule.ForSymbol(r.Symbol)).Sum(bySchedule =>
                {
                    decimal buy = 0m, sell = 0m;
                    int count = 0;
                    foreach (var r in bySchedule)
                    {
                        decimal rupees = r.PremiumLots * LotSizeOf(lotSizes, r.Symbol);
                        if (string.Equals(r.Side, "BUY", StringComparison.OrdinalIgnoreCase)) buy += rupees;
                        else if (string.Equals(r.Side, "SELL", StringComparison.OrdinalIgnoreCase)) sell += rupees;
                        count += r.Orders;
                    }
                    return OptionCharges.For(buy, sell, count, bySchedule.Key).Total;
                }));
    }

    /// <summary>The lot size a fill was booked at: 1 when the symbol is unknown, as in PaperTradingService.</summary>
    public static int LotSizeOf(IReadOnlyDictionary<string, LotSizeInfo> lotSizes, string symbol)
        => lotSizes.TryGetValue(symbol, out var info) && info.LotSize > 0 ? info.LotSize : 1;
}
