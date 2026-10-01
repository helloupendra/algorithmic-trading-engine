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

    public static async Task<AppUser> EnsureAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.UserName.ToLower() == UserName, cancellationToken);
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
        var book = await ManualBook.FindAsync(db, user.Id, cancellationToken);
        var dayStart = IstTime.StartOfDayUtc(IstTime.DateOf(clockUtc));

        var open = new List<AiTraderOpenPosition>();
        decimal realizedToday = 0m, unrealized = 0m, chargesToday = 0m;
        int openedToday = 0;
        if (book is not null)
        {
            var positions = await db.PaperPositions.AsNoTracking()
                .Where(p => p.SimulationRunId == book.Id && (p.Status == "Open" || p.ClosedUtc >= dayStart))
                .ToListAsync(cancellationToken);
            var sizes = await lotSizes.ResolveManyAsync(positions.Select(p => p.Symbol).Distinct(), cancellationToken);
            foreach (var p in positions.Where(p => p.Status == "Open"))
            {
                int lot = RunCharges.LotSizeOf(sizes, p.Symbol);
                open.Add(new AiTraderOpenPosition(p.Id, p.Symbol, p.Quantity, p.AveragePrice, p.AveragePrice * p.Quantity * lot,
                    p.LastMarkPrice, p.StopLossPrice, p.TargetPrice, p.UnrealizedPnl));
                unrealized += p.UnrealizedPnl;
            }

            realizedToday = positions.Where(p => p.Status != "Open" && p.ClosedUtc >= dayStart).Sum(p => p.RealizedPnl);
            var todaysOrders = db.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == book.Id && o.FilledUtc >= dayStart);
            chargesToday = (await charges.ForOrdersAsync(todaysOrders, cancellationToken)).GetValueOrDefault(book.Id);
            openedToday = await todaysOrders.CountAsync(o => o.Side == "BUY", cancellationToken);
        }

        var running = registry.List().Where(r => r.UserId == user.Id).ToList();
        var runFigures = await pnl.FiguresAsync(running.Select(r => r.RunId).ToList(), running.Select(r => r.RunId).ToHashSet(), cancellationToken);
        var runs = running
            .Select(r => new AiTraderRun(r.RunId, r.Name, r.Underlying,
                runFigures.TryGetValue(r.RunId, out var f) ? f.Realized + f.Unrealized - f.Charges : 0m))
            .ToList();

        // Strategy runs started today count towards the day's net too.
        decimal runsToday = runs.Sum(r => r.NetPnl);
        decimal net = realizedToday + unrealized - chargesToday + runsToday;
        return new AiTraderBook(clockUtc, tradingDay, kill, Math.Round(net, 2), openedToday, open, runs);
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
}
