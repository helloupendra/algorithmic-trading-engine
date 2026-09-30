using System.Text.Json.Nodes;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

/// <summary>
/// <c>get_strategy_history</c>: how a strategy (or an account, or an
/// underlying) did over a period: the money by day, the runs, and what its
/// closed trades looked like.
/// </summary>
/// <remarks>
/// <para>
/// Written after the owner asked "how did GhostTangentCrossings run this
/// month?" on 30 Sep and the Assistant could only read one day at a time
/// (<c>get_runs</c>), with a month needing thirty calls against a limit of
/// eight. This one reads the period in one call.
/// </para>
/// <para>
/// The runs come from <see cref="LiveRunHistoryBuilder"/>, the Trade →
/// History page's source, so a day's net is the page's (realized − charges,
/// plus open legs while a run is active). The trade figures are over the
/// runs' closed legs, gross of charges, which are counted per run: a leg's
/// share of a run's charges is not something the desk records.
/// </para>
/// </remarks>
public sealed class StrategyHistoryTool(LiveRunHistoryBuilder history, TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int MaxRuns = 3000;
    private const int ListedRuns = 30;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.StrategyHistory;

    public string Description =>
        "How runs did over a period, in one call: pick a strategy (name contains), an underlying, an account, and a " +
        "period ('this_month', 'last_month', 'last_7_days', 'last_30_days') or from/to dates. Returns totals (net " +
        "after charges, gross, charges, runs, winning and losing days), net by day, by underlying and by strategy, " +
        "the best and worst runs with their ids (read one with get_run), and stats of the closed trades: count, win " +
        "rate, average win and loss, profit factor, largest win and loss, average holding minutes, and gross P&L by " +
        "hour of entry. Use it for any question about a strategy's or an account's week or month.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("strategy", AiToolSchema.Text("Strategy name, or part of it, as get_runs shows it."), false),
        ("underlying", AiToolSchema.Text("NIFTY, BANKNIFTY, SENSEX, CRUDEOIL..."), false),
        ("account", AiToolSchema.Text("Only this account's runs (user name)."), false),
        ("period", AiToolSchema.OneOf("A named period. Default this_month.", "this_month", "last_month", "last_7_days", "last_30_days"), false),
        ("from", AiToolSchema.Text("First IST day, yyyy-MM-dd (overrides period)."), false),
        ("to", AiToolSchema.Text("Last IST day, yyyy-MM-dd. Default today."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        string? strategy = args.String("strategy", 80);
        string? underlying = args.String("underlying", 20)?.ToUpperInvariant();
        string? account = args.String("account");
        var (from, to) = Period(args, today);
        if (from > to) throw new AiToolArgumentException("from is after to.");
        if (to.DayNumber - from.DayNumber > 366) throw new AiToolArgumentException("The period is longer than a year; ask for less.");

        // The history builder pages at 500 runs; a month of the desk is about that many.
        var runs = new List<LiveRunSummaryResponse>();
        for (int skip = 0; skip < MaxRuns; skip += LiveRunHistoryFilter.MaxTake)
        {
            var page = await history.ListAsync(new LiveRunHistoryFilter(null, null, underlying, null, from, to, LiveRunHistoryFilter.MaxTake, skip), cancellationToken);
            runs.AddRange(page);
            if (page.Count < LiveRunHistoryFilter.MaxTake) break;
        }

        var rows = runs
            .Where(r => r.Role != "alerts")
            .Where(r => strategy is null || r.StrategyName.Contains(strategy, StringComparison.OrdinalIgnoreCase))
            .Where(r => account is null || string.Equals(r.UserName, account, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var scope = new { strategy, underlying, account, from = from.ToString("yyyy-MM-dd"), to = to.ToString("yyyy-MM-dd") };
        if (rows.Count == 0)
        {
            return new AiToolOutput(new { scope, runs = 0, note = "No runs match in this period." }, now, 0, "no runs");
        }

        decimal Net(LiveRunSummaryResponse r) => r.GrossPnl - r.Charges + (r.IsActive ? r.UnrealizedPnl : 0m);
        DateOnly Day(LiveRunSummaryResponse r) => IstTime.DateOf(r.StartedUtc);

        var byDay = rows.GroupBy(Day).OrderBy(g => g.Key).Select(g => new
        {
            date = g.Key.ToString("yyyy-MM-dd"),
            runs = g.Count(),
            net = Rs(g.Sum(Net)),
            charges = Rs(g.Sum(r => r.Charges)),
            trades = g.Sum(r => r.Trades),
        }).ToList();

        var trades = await TradeStatsAsync(rows.Select(r => r.RunId).ToList(), cancellationToken);

        var data = new
        {
            scope,
            totals = new
            {
                runs = rows.Count,
                daysTraded = byDay.Count,
                net = Rs(rows.Sum(Net)),
                gross = Rs(rows.Sum(r => r.GrossPnl)),
                charges = Rs(rows.Sum(r => r.Charges)),
                openNow = rows.Count(r => r.IsActive),
                winningDays = byDay.Count(d => d.net > 0),
                losingDays = byDay.Count(d => d.net < 0),
                winningRuns = rows.Count(r => Net(r) > 0),
                losingRuns = rows.Count(r => Net(r) < 0),
                bestDay = byDay.MaxBy(d => d.net),
                worstDay = byDay.MinBy(d => d.net),
            },
            byDay,
            byUnderlying = rows.GroupBy(r => r.Underlying).Select(g => new { underlying = g.Key, runs = g.Count(), net = Rs(g.Sum(Net)) }).OrderBy(g => g.net).ToList(),
            byStrategy = rows.GroupBy(r => r.StrategyName).Select(g => new { strategy = g.Key, runs = g.Count(), net = Rs(g.Sum(Net)) }).OrderBy(g => g.net).ToList(),
            byAccount = rows.GroupBy(r => r.UserName ?? "?").Select(g => new { account = g.Key, runs = g.Count(), net = Rs(g.Sum(Net)) }).ToList(),
            trades,
            runs = rows.OrderByDescending(r => Math.Abs(Net(r))).Take(ListedRuns).OrderBy(r => r.StartedUtc).Select(r => new
            {
                runId = r.RunId,
                date = Day(r).ToString("yyyy-MM-dd"),
                strategy = r.StrategyName,
                underlying = r.Underlying,
                account = r.UserName,
                net = Rs(Net(r)),
                charges = Rs(r.Charges),
                trades = r.Trades,
            }).ToList(),
            runsNote = rows.Count > ListedRuns ? $"The {ListedRuns} runs with the largest net are listed; totals cover all {rows.Count}." : null,
        };

        return new AiToolOutput(data, now, rows.Count,
            $"{rows.Count} runs, {from:dd MMM}–{to:dd MMM}, net ₹{Rs(rows.Sum(Net)):N2}");
    }

    /// <summary>The period asked for: from/to when given, else a named period, else this month.</summary>
    private static (DateOnly From, DateOnly To) Period(AiToolArgs args, DateOnly today)
    {
        var to = args.Date("to", today) ?? today;
        if (args.Date("from", today) is DateOnly from) return (from, to);
        return (args.String("period", 20)?.ToLowerInvariant() ?? "this_month") switch
        {
            "this_month" => (new DateOnly(today.Year, today.Month, 1), to),
            "last_month" => (new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1)),
            "last_7_days" => (today.AddDays(-6), to),
            "last_30_days" => (today.AddDays(-29), to),
            var other => throw new AiToolArgumentException($"period is this_month, last_month, last_7_days or last_30_days, not {other}."),
        };
    }

    /// <summary>What the runs' closed legs looked like, gross of charges.</summary>
    private async Task<object?> TradeStatsAsync(List<long> runIds, CancellationToken cancellationToken)
    {
        var legs = await db.PaperPositions.AsNoTracking()
            .Where(p => runIds.Contains(p.SimulationRunId) && p.Status != "Open" && p.ClosedUtc != null)
            .Select(p => new { p.RealizedPnl, p.OpenedUtc, p.ClosedUtc })
            .ToListAsync(cancellationToken);
        if (legs.Count == 0) return null;

        var wins = legs.Where(l => l.RealizedPnl > 0).Select(l => l.RealizedPnl).ToList();
        var losses = legs.Where(l => l.RealizedPnl < 0).Select(l => l.RealizedPnl).ToList();
        decimal lost = -losses.Sum();
        return new
        {
            closedLegs = legs.Count,
            winRate = Math.Round(100.0 * wins.Count / legs.Count, 1),
            averageWin = wins.Count == 0 ? (decimal?)null : Rs(wins.Average()),
            averageLoss = losses.Count == 0 ? (decimal?)null : Rs(losses.Average()),
            largestWin = wins.Count == 0 ? (decimal?)null : Rs(wins.Max()),
            largestLoss = losses.Count == 0 ? (decimal?)null : Rs(losses.Min()),
            profitFactor = lost == 0 ? (decimal?)null : Math.Round(wins.Sum() / lost, 2),
            averageHoldMinutes = Math.Round(legs.Average(l => (l.ClosedUtc!.Value - l.OpenedUtc).TotalMinutes), 1),
            grossByEntryHour = legs
                .GroupBy(l => IstTime.ToIst(DateTime.SpecifyKind(l.OpenedUtc, DateTimeKind.Utc)).Hour)
                .OrderBy(g => g.Key)
                .Select(g => new { hour = $"{g.Key:00}:00", legs = g.Count(), gross = Rs(g.Sum(l => l.RealizedPnl)) })
                .ToList(),
            note = "Leg figures are gross; charges are per run, in the totals.",
        };
    }
}
