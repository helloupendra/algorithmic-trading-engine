using System.Diagnostics;
using System.Text;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
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

    /// <summary>Answers each probe with the next result in turn, then keeps giving the last.</summary>
    private sealed class ScriptedProbe(params Func<ProbeResult>[] answers) : IProcessProbe
    {
        public int Calls { get; private set; }

        public ProbeResult Probe(int pid, string marker, long? runId)
            => answers[Math.Min(Calls++, answers.Length - 1)]();
    }
}
