using System.Diagnostics;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
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
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak")
            : new ProcessStartInfo("/bin/sleep", "30");
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;

        var process = Process.Start(info)!;
        _processes.Add(process);
        return process;
    }
}
