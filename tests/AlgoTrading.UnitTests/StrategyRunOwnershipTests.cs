using System.Diagnostics;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Whose run is whose, once every trading account runs the same plan.
/// </summary>
/// <remarks>
/// Until 2026-09-23 a run was refused whenever anyone was already running that
/// strategy on that underlying, so the second account's morning deploy was
/// turned away as a duplicate. Two traders running the same strategy on the
/// same underlying is not a duplicate: it is the same signal in two books. The
/// duplicate worth refusing is the same strategy, same underlying, same
/// account — that one doubles a position by accident.
/// </remarks>
public class StrategyRunOwnershipTests : IDisposable
{
    private readonly List<Process> _processes = new();

    /// <summary>Kills the stand-in processes the entries were built around.</summary>
    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone; nothing to kill.
            }
            process.Dispose();
        }
    }

    [Fact]
    public void Two_accounts_may_run_the_same_strategy_on_the_same_underlying()
    {
        var registry = NewRegistry();
        Assert.True(registry.TryAdd(Run(runId: 1, strategyId: 7, underlying: "NIFTY", userId: 100)));
        Assert.True(registry.TryAdd(Run(runId: 2, strategyId: 7, underlying: "NIFTY", userId: 200)));

        Assert.Equal(1, registry.Find(7, "NIFTY", 100)!.RunId);
        Assert.Equal(2, registry.Find(7, "NIFTY", 200)!.RunId);
    }

    [Fact]
    public void The_same_account_running_it_twice_is_what_gets_refused()
    {
        var registry = NewRegistry();
        registry.TryAdd(Run(runId: 1, strategyId: 7, underlying: "NIFTY", userId: 100));

        // This is the check the start endpoint makes before it creates a run.
        Assert.NotNull(registry.Find(7, "NIFTY", 100));

        // A different underlying in the same account is fine, and so is the
        // same underlying in another account.
        Assert.Null(registry.Find(7, "BANKNIFTY", 100));
        Assert.Null(registry.Find(7, "NIFTY", 999));
    }

    [Fact]
    public void Asking_without_an_account_still_answers_is_anyone_running_it()
    {
        var registry = NewRegistry();
        registry.TryAdd(Run(runId: 1, strategyId: 7, underlying: "NIFTY", userId: 100));

        // Stopping a strategy by name has no account in hand, and must still
        // find it.
        Assert.NotNull(registry.Find(7, "NIFTY"));
        Assert.Null(registry.Find(8, "NIFTY"));
    }

    [Fact]
    public void The_run_limit_counts_one_traders_runs_not_the_whole_desk()
    {
        var registry = NewRegistry();
        registry.TryAdd(Run(runId: 1, strategyId: 7, underlying: "NIFTY", userId: 100));
        registry.TryAdd(Run(runId: 2, strategyId: 7, underlying: "BANKNIFTY", userId: 100));
        registry.TryAdd(Run(runId: 3, strategyId: 7, underlying: "NIFTY", userId: 200));

        Assert.Equal(2, registry.CountFor(100));
        Assert.Equal(1, registry.CountFor(200));
        Assert.Equal(0, registry.CountFor(300));
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void Exits_remember_the_whole_close_of_two_accounts_and_whose_run_it_was()
    {
        // 28 Sep: three indices in two accounts end six runs of one strategy at
        // the close; with five remembered, one fell off the Stopped list.
        var registry = NewRegistry();
        for (long i = 1; i <= 6; i++)
        {
            var run = Run(runId: i, strategyId: 7, underlying: i % 3 == 0 ? "SENSEX" : i % 2 == 0 ? "NIFTY" : "BANKNIFTY", userId: i <= 3 ? 100 : 200);
            registry.TryAdd(run);
            registry.RecordExit(run, "Market closed (15:30 IST)");
        }

        var exits = registry.GetLastExits(7);
        Assert.Equal(6, exits.Count);
        Assert.Equal(3, exits.Count(x => x.UserId == 100));
        Assert.Equal(3, exits.Count(x => x.UserId == 200));
    }

    [Fact]
    public void SixExits_AllKept()
    {
        // 25 Sep: six runs of one strategy a day — three indices in two
        // accounts — against five exits kept per strategy. Admin's 249, 252,
        // 255 and 258 fell off, and "Realized today" was short by ₹1,176.
        var registry = NewRegistry();
        long runId = 249;
        foreach (long owner in new long[] { 1, 7 })
        {
            foreach (var underlying in new[] { "NIFTY", "BANKNIFTY", "SENSEX" })
            {
                var run = Run(runId++, strategyId: 7, underlying, owner);
                registry.TryAdd(run);
                registry.RecordExit(run, "Market closed (15:30 IST)");
            }
        }

        var exits = registry.GetLastExits(7);
        Assert.Equal(6, exits.Count);
        Assert.Equal(new long[] { 249, 250, 251, 252, 253, 254 }, exits.Select(x => x.RunId).Order());
        Assert.Equal(3, registry.GetLastExits(7, ownerUserId: 1).Count);
        Assert.Equal(3, registry.GetLastExits(7, ownerUserId: 7).Count);
    }

    [Fact]
    public void One_accounts_restarts_never_push_out_another_accounts_exits()
    {
        var registry = NewRegistry();
        var admins = Run(runId: 249, strategyId: 7, underlying: "NIFTY", userId: 1);
        registry.TryAdd(admins);
        registry.RecordExit(admins, "Stopped by admin");

        // The other account stops and restarts the same strategy all day.
        for (long i = 0; i < 3 * StrategyProcessRegistry.ExitsPerAccountAndUnderlying; i++)
        {
            var churn = Run(runId: 1000 + i, strategyId: 7, underlying: "NIFTY", userId: 7);
            registry.TryAdd(churn);
            registry.RecordExit(churn, "Stopped by coderforchange");
        }

        Assert.Equal(249, Assert.Single(registry.GetLastExits(7, ownerUserId: 1)).RunId);
        Assert.Equal(StrategyProcessRegistry.ExitsPerAccountAndUnderlying, registry.GetLastExits(7, ownerUserId: 7).Count);
        Assert.NotNull(registry.GetExitByRun(249));
    }

    [Fact]
    public async Task TraderList_HasNoOtherOwnersExits()
    {
        using var desk = new RunnerDesk();
        var runs = new Dictionary<long, long>();
        foreach (var owner in new[] { RunnerDesk.AdminId, RunnerDesk.TraderId, RunnerDesk.OtherTraderId })
        {
            // What the stop pipeline does at the close: close the row, remember
            // the exit, drop the entry.
            long runId = desk.SeedRun(owner, "NIFTY", "Stopped");
            var run = Run(runId, RunnerDesk.GhostId, "NIFTY", owner);
            desk.Registry.TryAdd(run);
            desk.Registry.RecordExit(run, "Market closed (15:30 IST)");
            desk.Registry.Remove(runId);
            runs[owner] = runId;
        }

        await using var db = desk.Db();

        var trader = await Ghost(desk.Controller(db, RunnerDesk.TraderId));
        Assert.Equal(runs[RunnerDesk.TraderId], Assert.Single(trader.RecentExits).RunId);
        Assert.Equal(runs[RunnerDesk.TraderId], trader.LastExit!.RunId);

        var admin = await Ghost(desk.Controller(db, RunnerDesk.AdminId));
        Assert.Equal(runs.Values.Order(), admin.RecentExits.Select(x => x.RunId).Order());

        // The legacy strategy-scoped logs route resolves to the trader's own
        // newest exit, not to whichever account's run ended last (which it
        // then refused to show them).
        var logs = await desk.Controller(db, RunnerDesk.TraderId).GetLogs(RunnerDesk.GhostId);
        Assert.IsType<OkObjectResult>(logs);
    }

    [Fact]
    public async Task Adopted_KeepsStarter()
    {
        using var desk = new RunnerDesk();

        // The morning plan: admin starts it in coderforchange's account.
        long run = desk.SeedRun(RunnerDesk.TraderId, "NIFTY", "Running", startedBy: (RunnerDesk.AdminId, "admin"));
        await desk.Pids.SetPidAsync(SystemSettingKeys.StrategyRunPid(run), desk.RunnerFor(run).Id);

        // A row from before the starter was recorded: the owner is all there is.
        long older = desk.SeedRun(RunnerDesk.TraderId, "BANKNIFTY", "Running");
        await desk.Pids.SetPidAsync(SystemSettingKeys.StrategyRunPid(older), desk.RunnerFor(older).Id);

        await using (var db = desk.Db())
        {
            var result = await desk.RunControl(db).ReconcileOrphanedRunsAsync();
            Assert.Equal(2, result.Adopted);
        }

        var adopted = desk.Registry.Get(run)!;
        Assert.True(adopted.Adopted);
        Assert.Equal("admin", adopted.StartedBy);
        Assert.Equal(RunnerDesk.TraderId, adopted.UserId);
        Assert.Equal("coderforchange", desk.Registry.Get(older)!.StartedBy);

        // And the exit it leaves says the same.
        desk.Registry.RecordExit(adopted, "Market closed (15:30 IST)");
        Assert.Equal("admin", desk.Registry.GetExitByRun(run)!.StartedBy);
    }

    private static async Task<StrategyListItemResponse> Ghost(StrategyController controller)
    {
        var result = await controller.GetAll(CancellationToken.None);
        var list = Assert.IsType<List<StrategyListItemResponse>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        return list.Single(x => x.Id == RunnerDesk.GhostId);
    }

    private StrategyProcessRegistry NewRegistry()
        => new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StrategyProcessRegistry>.Instance);

    /// <summary>
    /// A registry entry, built around a process that is alive and does nothing.
    /// </summary>
    /// <remarks>
    /// It has to be a real, living process: registering an entry starts an exit
    /// monitor, and a process that was never started counts as exited at once —
    /// the entry would be swept out from under the assertions. A sleep is the
    /// cheapest thing that stays alive; a real runner would need the Python
    /// engine, a database and a feed.
    /// </remarks>
    private RunningStrategy Run(long runId, int strategyId, string underlying, long userId)
        => new(
            strategyId,
            $"Strategy{strategyId}",
            Sleeper(),
            StartedBy: "admin",
            UserId: userId,
            StartedUtc: DateTime.UtcNow,
            RunId: runId,
            Underlying: underlying,
            SpotSymbol: $"NSE:{underlying}-INDEX",
            Lots: 2,
            Risk: new RiskRulesDto());

    private Process Sleeper()
    {
        var info = TestSleeper.StartInfo();
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;

        var process = Process.Start(info)!;
        _processes.Add(process);
        return process;
    }
}
