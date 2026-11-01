using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// What the risk gate's "max daily loss" measures: one run's net P&amp;L for the current IST day, after
/// charges (<see cref="RiskManagementService"/>).
/// </summary>
/// <remarks>
/// <para>
/// Until 2 Nov 2026 the gate summed every position the run had ever had, before charges. A strategy run lives
/// one session, so for it that was the day, gross. A manual book (one per user, never stopped) and the AI
/// Trader's book live for weeks, so their old losses piled up until every new entry was refused on every later
/// day, with a message that said "daily"; and a book up in total could lose more than the limit in one day
/// before the gate noticed. Measured gross, a high-churn run whose charges drained the account was never
/// stopped: the owner decided on 27 Sep that the risk guard works on net.
/// </para>
/// <para>
/// The day is the IST calendar day of the clock: every session the desk trades (NSE, BSE, MCX until 23:55)
/// lies inside one. Its net = the sum of each position's part of today (<see cref="TodaysPart"/>) − the charges
/// of the run's fills filled today (<see cref="FillCharges"/>). The per-position rule is the AI Trader's own
/// (its book reader in the API), so the platform gate and the AI Trader's daily-loss rule agree on what
/// "today" is.
/// </para>
/// <para>
/// A recap run replays one recorded session in an evening, and its rows carry that session's times, not
/// today's: it is one day by construction, so all of it counts.
/// </para>
/// </remarks>
public static class DailyLoss
{
    /// <summary>
    /// The part of one position's P&amp;L (before charges) that belongs to the day starting at
    /// <paramref name="dayStartUtc"/>.
    /// </summary>
    /// <param name="previousClose">
    /// The contract's previous session close, sent by the feed with today's quotes; null when no quote of
    /// today carries one.
    /// </param>
    /// <param name="reducedToday">
    /// True when a fill today closed part of a position opened on an earlier day that is still open.
    /// </param>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Opened today: all of it, realized (partial closes included) plus unrealized at its mark.</item>
    /// <item>Opened earlier and still open: its mark less the previous session's close, so only today's move
    /// counts. With no previous close known, its unrealized P&amp;L since entry counts when it is a loss and
    /// nothing when it is a gain: a gain the rule cannot date never loosens the limit, a loss it cannot date
    /// can only tighten it.</item>
    /// <item>Opened earlier, closed (or partly closed) today: its whole realized P&amp;L. The earlier days' move
    /// of a carried position lands on the day it is closed; it is rare in a book squared off at every close,
    /// and it is the AI Trader reader's rule too.</item>
    /// <item>Opened and closed on earlier days: nothing.</item>
    /// </list>
    /// </remarks>
    public static decimal TodaysPart(Position p, DateTime dayStartUtc, decimal? previousClose, int lotSize, bool reducedToday)
    {
        bool open = string.Equals(p.Status, "Open", StringComparison.Ordinal);
        if (p.OpenedUtc >= dayStartUtc) return p.RealizedPnl + (open ? p.UnrealizedPnl : 0m);

        // A row with no close stamp is dated by its last update: closed (or carried) no later than that.
        if (!open) return (p.ClosedUtc ?? p.UpdatedUtc) >= dayStartUtc ? p.RealizedPnl : 0m;

        decimal unrealized = previousClose is decimal close && close > 0m && p.LastMarkPrice is decimal mark
            ? PaperPnl.Unrealized(p.Direction, close, mark, p.Quantity, lotSize)
            : Math.Min(0m, p.UnrealizedPnl);
        return unrealized + (reducedToday ? p.RealizedPnl : 0m);
    }

    /// <summary>
    /// The run's figures for the IST day of <paramref name="nowUtc"/>: today's part of its positions, the
    /// charges of today's fills, and the net between them. A recap run counts whole (<see cref="Figures.WholeRun"/>).
    /// </summary>
    public static async Task<Figures> ReadAsync(
        TradingDbContext db, ILotSizeResolver lotSizes, long runId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var parameters = await db.SimulationRuns.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.ParametersJson)
            .FirstOrDefaultAsync(cancellationToken);
        bool wholeRun = RecapClock.IsRecap(parameters);
        var dayStart = IstTime.StartOfDayUtc(IstTime.DateOf(nowUtc));

        var positionsQuery = db.PaperPositions.AsNoTracking().Where(p => p.SimulationRunId == runId);
        var ordersQuery = db.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == runId);
        if (!wholeRun)
        {
            positionsQuery = positionsQuery.Where(p =>
                p.Status == "Open" || p.OpenedUtc >= dayStart || (p.ClosedUtc ?? p.UpdatedUtc) >= dayStart);
            ordersQuery = ordersQuery.Where(o => o.FilledUtc >= dayStart);
        }

        var positions = await positionsQuery
            .Select(p => new Position(p.Id, p.GroupId, p.Symbol, p.Direction, p.Status, p.Quantity, p.OpenedUtc,
                p.ClosedUtc, p.UpdatedUtc, p.RealizedPnl, p.UnrealizedPnl, p.LastMarkPrice))
            .ToListAsync(cancellationToken);

        // Only a position carried in from an earlier day needs more than its own row.
        var carried = wholeRun
            ? new List<Position>()
            : positions.Where(p => p.Status == "Open" && p.OpenedUtc < dayStart).ToList();
        var closes = new Dictionary<string, decimal>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, LotSizeInfo> sizes = new Dictionary<string, LotSizeInfo>();
        var reduced = new HashSet<long>();
        if (carried.Count > 0)
        {
            var symbols = carried.Select(p => p.Symbol).Distinct(StringComparer.Ordinal).ToList();
            closes = (await db.LiveQuotesLatest.AsNoTracking()
                    .Where(q => symbols.Contains(q.Symbol) && q.UpdatedUtc >= dayStart && q.Close > 0)
                    .Select(q => new { q.Symbol, q.Close })
                    .ToListAsync(cancellationToken))
                .GroupBy(q => q.Symbol, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Close!.Value, StringComparer.Ordinal);
            sizes = await lotSizes.ResolveManyAsync(symbols, cancellationToken);

            var todaysFills = await db.PaperOrders.AsNoTracking()
                .Where(o => o.SimulationRunId == runId && o.FillPrice != null && o.FilledUtc >= dayStart && symbols.Contains(o.Symbol))
                .Select(o => new { o.GroupId, o.Symbol, o.Side })
                .ToListAsync(cancellationToken);
            // Only one position is open per (group, contract), so a closing-side fill today in its group and
            // contract reduced this one.
            foreach (var p in carried)
            {
                if (todaysFills.Any(o => o.GroupId == p.GroupId && o.Symbol == p.Symbol && Closes(p.Direction, o.Side)))
                    reduced.Add(p.Id);
            }
        }

        decimal gross = positions.Sum(p => TodaysPart(p, wholeRun ? DateTime.MinValue : dayStart,
            closes.TryGetValue(p.Symbol, out var close) ? close : null,
            FillCharges.LotSizeOf(sizes, p.Symbol),
            reduced.Contains(p.Id)));
        decimal charges = (await FillCharges.PerRunAsync(ordersQuery, lotSizes, cancellationToken)).GetValueOrDefault(runId);
        return new Figures(Math.Round(gross, 2), Math.Round(charges, 2), wholeRun);
    }

    private static bool Closes(string direction, string side)
        => PaperPnl.IsLong(direction)
            ? string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase)
            : string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase);

    /// <summary>One position as the rule reads it.</summary>
    public sealed record Position(
        long Id, string GroupId, string Symbol, string Direction, string Status, int Quantity, DateTime OpenedUtc,
        DateTime? ClosedUtc, DateTime UpdatedUtc, decimal RealizedPnl, decimal UnrealizedPnl, decimal? LastMarkPrice);

    /// <summary>
    /// A run's day: <see cref="Gross"/> before charges, the <see cref="Charges"/> of the day's fills, and
    /// <see cref="Net"/> after them. <see cref="WholeRun"/> for a recap, which counts all of itself.
    /// </summary>
    public sealed record Figures(decimal Gross, decimal Charges, bool WholeRun)
    {
        public decimal Net => Gross - Charges;
    }
}
