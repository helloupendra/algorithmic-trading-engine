using System.Diagnostics;
using System.Text;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.Api.Services.ProcessProbe;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What a restarted API does with the live runs the previous one left open.
/// </summary>
/// <remarks>
/// A stored pid is one of three things: the run's runner (adopt it), gone or
/// recycled (close the run), or alive but impossible to verify — its command
/// line could not be read. Until 28 Sep the third was folded into the second
/// (ProcessProbe.TryGetAlive), so a runner still trading whose command line
/// could not be read at that moment had its run closed and its positions
/// flattened under it.
/// </remarks>
public class StrategyRunControlTests
{
    [Fact]
    public async Task ProbeUnknown_NeitherAdoptsNorCloses()
    {
        using var desk = new RunnerDesk();
        long run = desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Running");
        var pidKey = SystemSettingKeys.StrategyRunPid(run);
        await desk.Pids.SetPidAsync(pidKey, 424242);
        var probe = new ScriptedProbe(() => new ProbeResult(Outcome.Unknown, null));
        desk.Probe = probe;

        StrategyRunControl.ReconcileResult result;
        await using (var db = desk.Db())
        {
            result = await desk.RunControl(db).ReconcileOrphanedRunsAsync();
        }

        Assert.Equal(new StrategyRunControl.ReconcileResult(Adopted: 0, Closed: 0, Unverified: 1), result);
        Assert.Equal(4, probe.Calls);                                  // once, then after 2, 4 and 8 s
        Assert.Equal("Running", desk.Runs().Single().Status);          // not closed, nothing flattened
        Assert.True(desk.Pids.Has(pidKey));                            // the pid is kept for the next attempt
        Assert.False(desk.Registry.Contains(run));                     // and nothing was adopted
        Assert.Contains(desk.Alerts, x => x.Contains($"#{run}"));
    }

    [Fact]
    public async Task A_runner_verified_on_a_retry_is_adopted()
    {
        using var desk = new RunnerDesk();
        long run = desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Running");
        var runner = desk.RunnerFor(run);
        await desk.Pids.SetPidAsync(SystemSettingKeys.StrategyRunPid(run), runner.Id);
        desk.Probe = new ScriptedProbe(
            () => new ProbeResult(Outcome.Unknown, null),                 // ps timed out under load
            () => new ProbeResult(Outcome.Alive, Process.GetProcessById(runner.Id)));

        await using (var db = desk.Db())
        {
            Assert.Equal(new StrategyRunControl.ReconcileResult(1, 0, 0), await desk.RunControl(db).ReconcileOrphanedRunsAsync());
        }

        Assert.True(desk.Registry.Get(run)!.Adopted);
        Assert.Empty(desk.Alerts);
    }

    [Fact]
    public async Task A_runner_found_gone_on_a_retry_is_closed()
    {
        using var desk = new RunnerDesk();
        long run = desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Running");
        await desk.Pids.SetPidAsync(SystemSettingKeys.StrategyRunPid(run), 424242);
        desk.Probe = new ScriptedProbe(
            () => new ProbeResult(Outcome.Unknown, null),
            () => new ProbeResult(Outcome.Dead, null));

        await using (var db = desk.Db())
        {
            Assert.Equal(new StrategyRunControl.ReconcileResult(0, 1, 0), await desk.RunControl(db).ReconcileOrphanedRunsAsync());
        }

        Assert.Equal("Stopped", desk.Runs().Single().Status);
        Assert.False(desk.Pids.Has(SystemSettingKeys.StrategyRunPid(run)));
    }

    [Fact]
    public async Task An_adopted_runner_gets_longer_than_five_seconds_to_leave_after_SIGTERM()
    {
        // 24 Sep, 15:30: 10 of 13 adopted runners were killed after "ignoring"
        // SIGTERM for 5 s. No SIGTERM on Windows, so nothing to wait for there.
        if (OperatingSystem.IsWindows()) return;

        // Not our child (its shell exits and it is handed to init), and slow to
        // leave: it finishes its shutdown six seconds after SIGTERM, as a runner
        // releasing its lock and joining its threads can. It prints its own pid
        // only once its trap is set: printed from outside with $!, the pid could
        // be read and signalled before the trap existed, and the runner died at
        // once (macOS CI, 28 Sep: "it left after 0.0s").
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("sh -c 'trap \"sleep 6; exit 0\" TERM; echo $$; exec >/dev/null 2>&1; while :; do sleep 1; done' &");
        var shell = Process.Start(start)!;
        int pid = int.Parse(shell.StandardOutput.ReadLine()!.Trim());
        shell.WaitForExit();

        var steps = new List<string>();
        var clock = Stopwatch.StartNew();
        try
        {
            using var adopted = Process.GetProcessById(pid);
            Assert.True(await ProcessTerminator.StopAsync(adopted, pid, steps.Add, NullLogger.Instance, "adopted test runner", adopted: true));
            Assert.True(clock.Elapsed > ProcessTerminator.GracefulExitTimeout, $"it left after {clock.Elapsed.TotalSeconds:0.0}s");
            Assert.DoesNotContain(steps, x => x.Contains("killing"));
        }
        finally
        {
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch (ArgumentException) { /* gone, as it should be */ }
        }
    }

    [Fact]
    public void A_proc_command_line_reads_like_ps()
    {
        var raw = Encoding.UTF8.GetBytes("/srv/.venv/bin/python\0/srv/strategies/execution_runner.py\0--strategy\0Fulcrum\0--run-id\0215\0");
        var commandLine = ParseProcCommandLine(raw);

        Assert.Equal("/srv/.venv/bin/python /srv/strategies/execution_runner.py --strategy Fulcrum --run-id 215", commandLine);
        Assert.True(NamesAnyMarker(commandLine!, new[] { StrategyRunnerMarker }));
        Assert.True(HasRunIdArgument(commandLine!, 215));

        // A zombie or a kernel thread has no arguments: that is "cannot tell", not a name.
        Assert.Null(ParseProcCommandLine(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void On_linux_the_command_line_comes_from_proc()
    {
        if (!OperatingSystem.IsLinux()) return;

        // This test host's own: readable without spawning ps.
        var commandLine = ReadCommandLine(Environment.ProcessId, NullLogger.Instance);
        Assert.False(string.IsNullOrWhiteSpace(commandLine));
    }

    // ------------------------------------------------------- desk events --

    [Fact]
    public async Task A_start_and_a_stop_tell_the_runs_owner()
    {
        using var desk = new RunnerDesk();

        // An admin starting a run in a trader's account: the trader is who is told.
        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY", ownerUserId: RunnerDesk.TraderId));
        long run = desk.Runs().Single().Id;

        await using (var db = desk.Db())
        {
            var stop = await desk.RunControl(db).StopAsync(run, "Stopped by admin", flatten: true, by: "admin");
            Assert.True(stop.WasRunning);
        }

        // Between the two, "Stopping: …" once the row is flipped — an UPDATE the
        // in-memory provider cannot run, so it is not seen here.
        var events = desk.DeskEvents.Of(DeskEventKinds.Run);
        Assert.Equal("Started Ghost on NIFTY, 1 lot(s)", events[0].Detail);
        Assert.Equal("Stopped: Stopped by admin", events[^1].Detail);
        Assert.All(events, x =>
        {
            Assert.Equal(run, x.RunId);
            Assert.Equal(RunnerDesk.TraderId, x.UserId);
            Assert.Null(x.Symbol);
        });
    }

    [Fact]
    public async Task A_run_closed_or_adopted_at_restart_tells_its_owner()
    {
        using var desk = new RunnerDesk();
        long gone = desk.SeedRun(RunnerDesk.TraderId, "NIFTY", "Running");
        long alive = desk.SeedRun(RunnerDesk.OtherTraderId, "BANKNIFTY", "Running");
        var runner = desk.RunnerFor(alive);
        await desk.Pids.SetPidAsync(SystemSettingKeys.StrategyRunPid(gone), 424242);
        await desk.Pids.SetPidAsync(SystemSettingKeys.StrategyRunPid(alive), runner.Id);
        desk.Probe = new PidProbe(runner.Id);

        await using (var db = desk.Db())
        {
            Assert.Equal(new StrategyRunControl.ReconcileResult(1, 1, 0), await desk.RunControl(db).ReconcileOrphanedRunsAsync());
        }

        var events = desk.DeskEvents.Of(DeskEventKinds.Run);
        var closed = Assert.Single(events, x => x.RunId == gone);
        Assert.Equal(RunnerDesk.TraderId, closed.UserId);
        Assert.Equal($"Stopped: {StrategyRunControl.RestartReason}", closed.Detail);
        var adopted = Assert.Single(events, x => x.RunId == alive);
        Assert.Equal(RunnerDesk.OtherTraderId, adopted.UserId);
        Assert.StartsWith("Adopted after an API restart", adopted.Detail);
    }

    /// <summary>Alive, and this run's runner, for one pid; gone for every other.</summary>
    private sealed class PidProbe(int alivePid) : IProcessProbe
    {
        public ProbeResult Probe(int pid, string marker, long? runId)
            => pid == alivePid
                ? new ProbeResult(Outcome.Alive, Process.GetProcessById(pid))
                : new ProbeResult(Outcome.Dead, null);
    }

    /// <summary>Answers each probe with the next result in turn, then keeps giving the last.</summary>
    private sealed class ScriptedProbe(params Func<ProbeResult>[] answers) : IProcessProbe
    {
        public int Calls { get; private set; }

        public ProbeResult Probe(int pid, string marker, long? runId)
            => answers[Math.Min(Calls++, answers.Length - 1)]();
    }
}
