// src/AlgoTrading.Api/Services/RunPnlSeriesBuilder.cs
using AlgoTrading.Api.Controllers;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Reads one IST day of <see cref="Domain.Entities.RunPnlMinute"/> rows into
/// per-run series and per-account totals (GET /api/Strategy/runs/pnl-series).
/// </summary>
public sealed class RunPnlSeriesBuilder
{
    private readonly TradingDbContext _dbContext;
    private readonly StrategyProcessRegistry _registry;

    public RunPnlSeriesBuilder(TradingDbContext dbContext, StrategyProcessRegistry registry)
    {
        _dbContext = dbContext;
        _registry = registry;
    }

    /// <summary>
    /// The day's series. <paramref name="userId"/> is already resolved by the
    /// controller, as for the run list: a trader always gets their own id; null
    /// is every account.
    /// </summary>
    public async Task<RunPnlSeriesResponse> BuildAsync(DateOnly date, long? userId, CancellationToken cancellationToken)
    {
        var dayStartUtc = IstTime.StartOfDayUtc(date);
        var dayEndUtc = IstTime.StartOfDayUtc(date.AddDays(1));

        var runsInScope = _dbContext.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == StrategyRunControl.LivePaperMode);
        if (userId.HasValue)
        {
            long scopeUserId = userId.Value;
            runsInScope = runsInScope.Where(r => r.UserId == scopeUserId);
        }

        var points = await (
                from p in _dbContext.RunPnlMinutes.AsNoTracking()
                join r in runsInScope on p.SimulationRunId equals r.Id
                where p.AtUtc >= dayStartUtc && p.AtUtc < dayEndUtc
                orderby p.SimulationRunId, p.AtUtc
                select new { p.SimulationRunId, p.AtUtc, p.Realized, p.Unrealized, p.Charges, p.Net })
            .ToListAsync(cancellationToken);

        var response = new RunPnlSeriesResponse
        {
            Date = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DayStartUtc = dayStartUtc
        };
        if (points.Count == 0) return response;

        var runIds = points.Select(p => p.SimulationRunId).Distinct().ToList();
        var runs = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.UserId, r.StrategyName, r.Symbol, r.ParametersJson, r.Status, r.StartedUtc, r.CreatedUtc })
            .ToListAsync(cancellationToken);

        var userIds = runs.Select(r => r.UserId).Distinct().ToList();
        var userNames = await _dbContext.AppUsers.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);

        var pointsByRun = points.GroupBy(p => p.SimulationRunId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var run in runs.OrderBy(r => r.StartedUtc ?? r.CreatedUtc).ThenBy(r => r.Id))
        {
            var p = LiveRunParameters.Parse(run.ParametersJson);
            var running = _registry.Get(run.Id);
            var exit = running is null ? _registry.GetExitByRun(run.Id) : null;
            var startedUtc = run.StartedUtc ?? run.CreatedUtc;
            bool isBook = run.StrategyName == ManualOrdersController.BookStrategyName;
            bool isAlerter = string.Equals(LiveRunParameters.ReadRole(run.ParametersJson), AlertsSupervisor.RoleAlerts, StringComparison.OrdinalIgnoreCase);

            var series = new RunPnlSeries
            {
                RunId = run.Id,
                UserId = run.UserId,
                UserName = userNames.GetValueOrDefault(run.UserId),
                StrategyName = run.StrategyName,
                Underlying = LiveRunHistoryBuilder.DeriveUnderlying(running, exit, p, run.Symbol),
                IsManualBook = isBook,
                Status = run.Status,
                StartedUtc = startedUtc,
                // The same "the day's runs" as GET /api/Strategy/runs?fromDate=…&toDate=…
                // (StartedUtc ?? CreatedUtc on the IST day), less the alerters.
                InAccountTotals = !isAlerter && startedUtc >= dayStartUtc && startedUtc < dayEndUtc
            };

            foreach (var point in pointsByRun[run.Id])
            {
                series.Minutes.Add((int)(point.AtUtc - dayStartUtc).TotalMinutes);
                series.Realized.Add(point.Realized);
                series.Unrealized.Add(point.Unrealized);
                series.Charges.Add(point.Charges);
                series.Net.Add(point.Net);
            }

            response.Runs.Add(series);
        }

        response.Accounts = response.Runs
            .Where(r => r.InAccountTotals)
            .GroupBy(r => r.UserId)
            .Select(g => SumAccount(g.Key, g.First().UserName, g.ToList()))
            .OrderBy(a => a.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.UserId)
            .ToList();

        return response;
    }

    /// <summary>
    /// An account's minutes: at every minute any of its runs has a point, each
    /// run's latest point at or before it (nothing before its first). So a run
    /// that ended keeps its final figures in the total for the rest of the day,
    /// and one that started later adds nothing until it does.
    /// </summary>
    public static AccountPnlSeries SumAccount(long userId, string? userName, IReadOnlyList<RunPnlSeries> runs)
    {
        var account = new AccountPnlSeries { UserId = userId, UserName = userName, Runs = runs.Count };
        var minutes = runs.SelectMany(r => r.Minutes).Distinct().Order().ToList();
        var next = new int[runs.Count];

        foreach (int minute in minutes)
        {
            decimal realized = 0m, unrealized = 0m, charges = 0m, net = 0m;
            for (int i = 0; i < runs.Count; i++)
            {
                var run = runs[i];
                while (next[i] < run.Minutes.Count && run.Minutes[next[i]] <= minute) next[i]++;
                int at = next[i] - 1;
                if (at < 0) continue;

                realized += run.Realized[at];
                unrealized += run.Unrealized[at];
                charges += run.Charges[at];
                net += run.Net[at];
            }

            account.Minutes.Add(minute);
            account.Realized.Add(realized);
            account.Unrealized.Add(unrealized);
            account.Charges.Add(charges);
            account.Net.Add(net);
        }

        return account;
    }
}
