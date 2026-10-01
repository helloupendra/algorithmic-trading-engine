using System.Globalization;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The AI Trader's own account: the <c>ai-trader</c> user, its manual book, and its strategy runs. Created on
/// first use, a Trader with no password, so nobody can sign in as it.
/// </summary>
public static class AiTraderAccount
{
    public const string UserName = "ai-trader";

    /// <summary>The owner's capital for it (1 Oct 2026); <see cref="AiTraderRules.Capital"/> is what enforces it.</summary>
    public const decimal Capital = 500_000m;

    /// <summary>Its account, or null before its first use.</summary>
    public static Task<AppUser?> FindAsync(TradingDbContext db, CancellationToken cancellationToken) =>
        db.AppUsers.FirstOrDefaultAsync(u => u.UserName.ToLower() == UserName, cancellationToken);

    public static async Task<AppUser> EnsureAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var user = await FindAsync(db, cancellationToken);
        if (user is not null) return user;

        user = new AppUser
        {
            UserName = UserName,
            Email = $"{UserName}@localhost",
            Role = UserRoles.Trader,
            TotalCapital = Capital,
            // Three strategy runs and the manual book.
            MaxConcurrentRuns = 4,
            IsActive = true,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }
}

/// <summary>
/// The facts the rules judge on, read from the AI Trader's own account at a moment: its open positions,
/// today's net after charges, the trades it has opened today, its running strategies, and the desk's kill
/// switch.
/// </summary>
public sealed class AiTraderBookReader(
    TradingDbContext db,
    ILotSizeResolver lotSizes,
    RunCharges charges,
    RunPnl pnl,
    StrategyProcessRegistry registry,
    IRiskManagementService risk,
    IMarketSessionService sessions) : IAiTraderBooks
{
    /// <summary>
    /// Its live account as of <paramref name="clockUtc"/>. In a replay only the trading day is read: the account
    /// is today's, and a replayed day never traded in it (the replay's shadow book is
    /// <see cref="AiTraderShadowBook"/>). Nor does the live desk's kill switch reach a replay: it halts what the
    /// desk places, and a replay places nothing. Carried in, it refused every buy of a replay played while the
    /// desk was halted.
    /// </summary>
    public async Task<AiTraderBook> ReadAsync(DateTime clockUtc, bool replay, CancellationToken cancellationToken)
    {
        bool tradingDay = sessions.GetSessionInfo(clockUtc, "NSE", "FO").IsTradingDay;
        if (replay) return new AiTraderBook(clockUtc, tradingDay, false, 0m, 0, [], []);
        bool kill = await risk.IsKillSwitchActiveAsync(cancellationToken);

        var user = await AiTraderAccount.EnsureAsync(db, cancellationToken);
        var day = await DayAsync(user.Id, clockUtc, cancellationToken);
        var open = day.Positions.Where(p => p.Open)
            .Select(p => new AiTraderOpenPosition(p.Id, p.Symbol, p.Lots, p.Entry, p.Entry * p.Lots * p.LotSize, p.Mark, p.StopLoss, p.Target, p.Pnl))
            .ToList();

        var running = registry.List().Where(r => r.UserId == user.Id).ToList();
        var runFigures = await pnl.FiguresAsync(running.Select(r => r.RunId).ToList(), running.Select(r => r.RunId).ToHashSet(), cancellationToken);
        var runs = running
            .Select(r => new AiTraderRun(r.RunId, r.Name, r.Underlying,
                runFigures.TryGetValue(r.RunId, out var f) ? f.Realized + f.Unrealized - f.Charges : 0m))
            .ToList();

        // A run's figures are its whole life (RunPnl has no "today"): only a run started today counts in today's net,
        // all of it being today's. Live runs stop at the close, so one started earlier and still running is rare; its
        // net is still shown with it, and left out of the day's.
        var dayStart = IstTime.StartOfDayUtc(IstTime.DateOf(clockUtc));
        var startedToday = running.Where(r => r.StartedUtc >= dayStart).Select(r => r.RunId).ToHashSet();
        decimal runsToday = runs.Where(r => startedToday.Contains(r.RunId)).Sum(r => r.NetPnl);
        return new AiTraderBook(clockUtc, tradingDay, kill, Math.Round(day.Net + runsToday, 2), day.OpenedToday, open, runs);
    }

    /// <summary>
    /// Its manual book on the day of <paramref name="clockUtc"/> (IST), for the daily digest: empty when it has no
    /// account yet (none is created for it here).
    /// </summary>
    public async Task<AiTraderLiveDay> DayAsync(DateTime clockUtc, CancellationToken cancellationToken) =>
        await AiTraderAccount.FindAsync(db, cancellationToken) is { } user
            ? await DayAsync(user.Id, clockUtc, cancellationToken)
            : AiTraderLiveDay.Empty;

    /// <summary>
    /// Its manual book on the day of <paramref name="clockUtc"/>: the positions open now or closed that day, and the
    /// day's net after charges, today's part only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Net today = the realized P&amp;L of the positions closed today + today's change in the open ones − the charges
    /// of today's fills. An open position bought today counts its P&amp;L at its mark. One carried from an earlier day
    /// counts its mark less the previous session's close, which the feed sends with today's quotes
    /// (<see cref="LiveQuoteLatest.Close"/>). Until 1 Oct its whole P&amp;L since entry counted, and every running run's
    /// whole net, against today's charges only: a winner carried in hid today's losses from the daily-loss rule.
    /// </para>
    /// <para>
    /// Approximations, both rare in an intraday book: with no quote of today for a carried position (so no previous
    /// close), its P&amp;L since entry counts if it is a loss and nothing if it is a gain, so the daily-loss rule is
    /// never loosened by a gain it cannot date; and a carried position closed today counts its whole realized P&amp;L.
    /// </para>
    /// </remarks>
    private async Task<AiTraderLiveDay> DayAsync(long userId, DateTime clockUtc, CancellationToken cancellationToken)
    {
        var book = await ManualBook.FindAsync(db, userId, cancellationToken);
        if (book is null) return AiTraderLiveDay.Empty;
        var dayStart = IstTime.StartOfDayUtc(IstTime.DateOf(clockUtc));

        var positions = await db.PaperPositions.AsNoTracking()
            .Where(p => p.SimulationRunId == book.Id && (p.Status == "Open" || p.ClosedUtc >= dayStart))
            .OrderBy(p => p.OpenedUtc).ThenBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var symbols = positions.Select(p => p.Symbol).Distinct().ToList();
        var sizes = await lotSizes.ResolveManyAsync(symbols, cancellationToken);
        var carried = positions.Where(p => p.Status == "Open" && p.OpenedUtc < dayStart).Select(p => p.Symbol).Distinct().ToList();
        var closes = (await db.LiveQuotesLatest.AsNoTracking()
                .Where(q => carried.Contains(q.Symbol) && q.UpdatedUtc >= dayStart && q.Close > 0)
                .Select(q => new { q.Symbol, q.Close })
                .ToListAsync(cancellationToken))
            .GroupBy(q => q.Symbol)
            .ToDictionary(g => g.Key, g => g.First().Close!.Value);

        // A closed position's lots have left it (its quantity is 0): the lots bought into it, from its fills.
        var since = positions.Count == 0 ? dayStart : positions.Min(p => p.OpenedUtc);
        var buys = await db.PaperOrders.AsNoTracking()
            .Where(o => o.SimulationRunId == book.Id && o.Side == "BUY" && o.FillPrice != null && o.FilledUtc >= since && symbols.Contains(o.Symbol))
            .Select(o => new { o.Symbol, o.Quantity, o.FilledUtc })
            .ToListAsync(cancellationToken);

        var rows = positions.Select(p =>
        {
            int lot = RunCharges.LotSizeOf(sizes, p.Symbol);
            bool open = p.Status == "Open";
            int lots = open ? p.Quantity : buys.Where(o => o.Symbol == p.Symbol && o.FilledUtc >= p.OpenedUtc && o.FilledUtc <= p.ClosedUtc).Sum(o => o.Quantity);
            decimal pnl = open ? p.UnrealizedPnl : p.RealizedPnl;
            decimal today = !open || p.OpenedUtc >= dayStart ? pnl
                : closes.TryGetValue(p.Symbol, out var close) && p.LastMarkPrice is decimal mark ? PaperPnl.Unrealized(p.Direction, close, mark, p.Quantity, lot)
                : Math.Min(0m, pnl);
            return new AiTraderLivePosition(p.Id, p.Symbol, lots, lot, p.OpenedUtc, p.AveragePrice, p.LastMarkPrice, open ? p.UpdatedUtc : p.ClosedUtc,
                open ? null : p.ClosedUtc, p.StopLossPrice, p.TargetPrice, pnl, today);
        }).ToList();

        var todaysOrders = db.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == book.Id && o.FilledUtc >= dayStart);
        decimal chargesToday = (await charges.ForOrdersAsync(todaysOrders, cancellationToken)).GetValueOrDefault(book.Id);
        int openedToday = await todaysOrders.CountAsync(o => o.Side == "BUY", cancellationToken);
        return new AiTraderLiveDay(rows, Math.Round(rows.Sum(r => r.PnlToday) - chargesToday, 2), chargesToday, openedToday);
    }

    /// <summary>
    /// The book as the brief tells it to the model. In shadow and replay it is the shadow book
    /// (<see cref="AiTraderShadowBook"/>), and the brief says how it is kept.
    /// </summary>
    public static string Describe(AiTraderBook book, AiTraderRules rules, string mode)
    {
        var text = new StringBuilder();
        text.Append(mode switch
        {
            AiTraderModes.Replay => "YOUR BOOK (this replay's shadow book, fresh at its start: code keeps your allowed buys as if placed, bought at the ask, checked every minute at the bid against your stop and target, squared off at 15:30; nothing reaches a broker)\n",
            AiTraderModes.Shadow => "YOUR BOOK (shadow: code keeps your allowed buys as if placed, bought at the ask, checked every minute at the bid against your stop and target, squared off at 15:30; nothing reaches a broker)\n",
            _ => "YOUR BOOK\n",
        });

        decimal budget = rules.DailyLossLimit + Math.Min(0m, book.NetToday);
        text.Append(CultureInfo.InvariantCulture,
            $"Net today {AiTraderGuard.Rupees(book.NetToday)} after charges; loss budget left {AiTraderGuard.Rupees(Math.Max(0m, budget))}; trades opened {book.OpenedToday} of {rules.MaxTradesPerDay}; positions {book.Open.Count} of {rules.MaxOpenPositions}.\n");
        foreach (var p in book.Open)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"- position {p.PositionId}: {p.Symbol} × {p.Lots} lot(s), entry {p.Entry:0.##}, mark {(p.Mark is decimal m ? m.ToString("0.##", CultureInfo.InvariantCulture) : "—")}, P&L {AiTraderGuard.Rupees(p.UnrealizedPnl)}, stop {(p.StopLoss?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none")}, target {(p.Target?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none")}\n");
        }

        text.Append(mode == AiTraderModes.Live
            ? book.Runs.Count == 0
                ? "Your strategies running: none."
                : "Your strategies running: " + string.Join("; ", book.Runs.Select(r => $"run {r.RunId} {r.Strategy} on {r.Underlying}, net {AiTraderGuard.Rupees(r.NetPnl)}")) + "."
            : "Strategy runs are not simulated in shadow: a start or stop is recorded only.");
        text.Append('\n');
        text.Append(CultureInfo.InvariantCulture, $"Strategies you may start: {string.Join(", ", rules.Strategies)} (at most {rules.MaxStrategyRuns} running).\n");
        if (book.KillSwitch) text.Append("The desk's kill switch is ON: nothing new will be placed.\n");
        return text.ToString();
    }
}

/// <summary>Reads the AI Trader's account at a moment (<see cref="AiTraderBookReader"/>; a fake in tests).</summary>
public interface IAiTraderBooks
{
    Task<AiTraderBook> ReadAsync(DateTime clockUtc, bool replay, CancellationToken cancellationToken);

    /// <summary>Its manual book (the live book) on the day of <paramref name="clockUtc"/>, position by position.</summary>
    Task<AiTraderLiveDay> DayAsync(DateTime clockUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Its manual book on one day: the positions open then or closed that day, oldest first. <c>Net</c> is the day's part
/// after <c>Charges</c> (today's fills'; <see cref="AiTraderBookReader"/>), and <c>OpenedToday</c> counts today's buys.
/// </summary>
public sealed record AiTraderLiveDay(IReadOnlyList<AiTraderLivePosition> Positions, decimal Net, decimal Charges, int OpenedToday)
{
    public static readonly AiTraderLiveDay Empty = new([], 0m, 0m, 0);
}

/// <summary>
/// One position of its manual book. <c>Pnl</c> is the whole trade's before charges, as closed or at its mark;
/// <c>PnlToday</c> the part of it that counts today. A closed one's <c>Mark</c> is its exit fill.
/// </summary>
public sealed record AiTraderLivePosition(long Id, string Symbol, int Lots, int LotSize, DateTime OpenedUtc, decimal Entry, decimal? Mark,
    DateTime? MarkUtc, DateTime? ClosedUtc, decimal? StopLoss, decimal? Target, decimal Pnl, decimal PnlToday)
{
    public bool Open => ClosedUtc is null;
}
