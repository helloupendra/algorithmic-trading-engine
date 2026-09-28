using System.Diagnostics;
using System.Reflection;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static AlgoTrading.Api.Services.ProcessProbe;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What a restarted API does with the backtests the previous one left open.
/// </summary>
/// <remarks>
/// The same three answers as for a live run's runner (StrategyRunControlTests):
/// the run's own runner (adopt it), gone or recycled (fail the run), or alive
/// but impossible to verify. Until 28 Sep a backtest folded the third into the
/// second (ProcessProbe.TryGetAlive), so a replay still running whose command
/// line could not be read at that moment was failed and squared off under it.
/// </remarks>
public class BacktestRunControlTests
{
    private const string Reason = BacktestStartupReconciler.RestartReason;

    [Fact]
    public async Task ProbeUnknown_NeitherAdoptsNorFails()
    {
        using var desk = new Desk();
        long run = desk.SeedRun("Running");
        await desk.Pids.SetPidAsync(SystemSettingKeys.BacktestRunPid(run), 424242);
        var probe = new ScriptedProbe(() => new ProbeResult(Outcome.Unknown, null));

        BacktestRunControl.ReconcileResult result;
        await using (var db = desk.Db())
        {
            result = await desk.Control(db, probe).ReconcileOrphanedRunsAsync(Reason);
        }

        Assert.Equal(new BacktestRunControl.ReconcileResult(Adopted: 0, Closed: 0, Unverified: 1), result);
        Assert.Equal(4, probe.Calls);                                      // once, then after 2, 4 and 8 s
        var row = desk.Run(run);
        Assert.Equal("Running", row.Status);                               // not failed
        Assert.Null(row.CompletedUtc);
        Assert.Empty(desk.Signals(run));                                   // no RUN_STOPPED
        Assert.Empty(desk.Flattened);                                      // nothing squared off
        Assert.True(desk.Pids.Has(SystemSettingKeys.BacktestRunPid(run))); // the pid is kept for the next start
        Assert.False(desk.Registry.Contains(run));                         // and nothing was adopted
    }

    [Fact]
    public async Task A_runner_found_gone_is_failed_and_squared_off()
    {
        using var desk = new Desk();
        long run = desk.SeedRun("Running");
        await desk.Pids.SetPidAsync(SystemSettingKeys.BacktestRunPid(run), 424242);
        var probe = new ScriptedProbe(() => new ProbeResult(Outcome.Dead, null));

        await using (var db = desk.Db())
        {
            Assert.Equal(new BacktestRunControl.ReconcileResult(0, 1, 0), await desk.Control(db, probe).ReconcileOrphanedRunsAsync(Reason));
        }

        Assert.Equal(1, probe.Calls);
        var row = desk.Run(run);
        Assert.Equal("Failed", row.Status);
        Assert.Equal(Reason, row.LastError);
        Assert.Equal(new[] { run }, desk.Flattened);
        Assert.Contains(desk.Signals(run), s => s.SignalType == BacktestRunControl.RunStoppedSignalType);
        Assert.False(desk.Pids.Has(SystemSettingKeys.BacktestRunPid(run)));
    }

    [Fact]
    public async Task A_runner_found_gone_on_a_retry_is_failed()
    {
        using var desk = new Desk();
        long run = desk.SeedRun("Pending");
        await desk.Pids.SetPidAsync(SystemSettingKeys.BacktestRunPid(run), 424242);
        var probe = new ScriptedProbe(
            () => new ProbeResult(Outcome.Unknown, null),                     // ps timed out under load
            () => new ProbeResult(Outcome.Dead, null));

        await using (var db = desk.Db())
        {
            Assert.Equal(new BacktestRunControl.ReconcileResult(0, 1, 0), await desk.Control(db, probe).ReconcileOrphanedRunsAsync(Reason));
        }

        Assert.Equal(2, probe.Calls);
        Assert.Equal("Failed", desk.Run(run).Status);
        Assert.False(desk.Pids.Has(SystemSettingKeys.BacktestRunPid(run)));
    }

    [Fact]
    public async Task A_runner_verified_on_a_retry_is_adopted()
    {
        using var desk = new Desk();
        long run = desk.SeedRun("Running");
        var runner = desk.Runner();
        await desk.Pids.SetPidAsync(SystemSettingKeys.BacktestRunPid(run), runner.Id);
        var probe = new ScriptedProbe(
            () => new ProbeResult(Outcome.Unknown, null),
            () => new ProbeResult(Outcome.Alive, Process.GetProcessById(runner.Id)));

        await using (var db = desk.Db())
        {
            Assert.Equal(new BacktestRunControl.ReconcileResult(1, 0, 0), await desk.Control(db, probe).ReconcileOrphanedRunsAsync(Reason));
        }

        Assert.True(desk.Registry.Get(run)!.Adopted);
        Assert.Equal("Running", desk.Run(run).Status);
        Assert.Empty(desk.Flattened);
    }

    [Fact]
    public async Task A_run_that_completes_while_it_is_probed_again_keeps_its_verdict()
    {
        // The runner could not be verified, finished the replay during the
        // retry wait (POST /complete: Completed) and exited. A probe now reads
        // "gone"; failing the row would overwrite a real result.
        using var desk = new Desk();
        long run = desk.SeedRun("Running");
        await desk.Pids.SetPidAsync(SystemSettingKeys.BacktestRunPid(run), 424242);
        var probe = new ScriptedProbe(
            () =>
            {
                desk.Complete(run);
                return new ProbeResult(Outcome.Unknown, null);
            },
            () => new ProbeResult(Outcome.Dead, null));

        await using (var db = desk.Db())
        {
            Assert.Equal(new BacktestRunControl.ReconcileResult(0, 0, 0), await desk.Control(db, probe).ReconcileOrphanedRunsAsync(Reason));
        }

        Assert.Equal(1, probe.Calls);
        Assert.Equal("Completed", desk.Run(run).Status);
        Assert.Empty(desk.Signals(run));
        Assert.Empty(desk.Flattened);
    }

    [Fact]
    public async Task A_run_with_no_stored_pid_is_failed_without_a_probe()
    {
        using var desk = new Desk();
        long run = desk.SeedRun("Running");
        var probe = new ScriptedProbe(() => new ProbeResult(Outcome.Unknown, null));

        await using (var db = desk.Db())
        {
            Assert.Equal(new BacktestRunControl.ReconcileResult(0, 1, 0), await desk.Control(db, probe).ReconcileOrphanedRunsAsync(Reason));
        }

        Assert.Equal(0, probe.Calls);
        Assert.Equal("Failed", desk.Run(run).Status);
    }

    /// <summary>Backtest rows, their stored pids and the registry, in memory.</summary>
    private sealed class Desk : IDisposable
    {
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"backtest-control-{Guid.NewGuid():N}";
        private readonly List<Process> _processes = new();
        private readonly List<long> _flattened = new();

        public RunnerDesk.PidStore Pids { get; } = new();

        public BacktestProcessRegistry Registry { get; } = new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BacktestProcessRegistry>.Instance);

        /// <summary>The runs squared off, in order.</summary>
        public IReadOnlyList<long> Flattened
        {
            get { lock (_flattened) return _flattened.ToList(); }
        }

        public TradingDbContext Db()
            => new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(_name, _root).Options);

        /// <summary>The reconcile, with no waits between re-probes.</summary>
        public BacktestRunControl Control(TradingDbContext db, IProcessProbe probe) => new(
            db,
            FlattenRecorder.Create(runId => { lock (_flattened) _flattened.Add(runId); }),
            Pids,
            Registry,
            probe,
            NullLogger<BacktestRunControl>.Instance)
        {
            UnknownProbeRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }
        };

        public long SeedRun(string status)
        {
            using var db = Db();
            var run = new SimulationRun
            {
                UserId = 1, Mode = BacktestRunControl.OfflineReplayMode, Status = status, Symbol = "NSE:NIFTY50-INDEX",
                StrategyName = "Ghost", ParametersJson = "{\"underlying\":\"NIFTY\"}", LastError = string.Empty,
                Resolution = "5", ReplaySpeed = string.Empty, StartedByName = "admin",
                StartedUtc = DateTime.UtcNow.AddMinutes(-10), CreatedUtc = DateTime.UtcNow.AddMinutes(-10),
                FromUtc = new DateTime(2026, 9, 1, 3, 45, 0, DateTimeKind.Utc), ToUtc = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc)
            };
            db.SimulationRuns.Add(run);
            db.SaveChanges();
            return run.Id;
        }

        /// <summary>What POST /complete does to the row.</summary>
        public void Complete(long runId)
        {
            using var db = Db();
            var run = db.SimulationRuns.Single(x => x.Id == runId);
            run.Status = BacktestRunControl.RunStatusCompleted;
            run.CompletedUtc = DateTime.UtcNow;
            db.SaveChanges();
        }

        public SimulationRun Run(long runId)
        {
            using var db = Db();
            return db.SimulationRuns.AsNoTracking().Single(x => x.Id == runId);
        }

        public List<SimulationSignal> Signals(long runId)
        {
            using var db = Db();
            return db.SimulationSignals.AsNoTracking().Where(x => x.SimulationRunId == runId).ToList();
        }

        /// <summary>A live process to hand to the adoption path.</summary>
        public Process Runner()
        {
            var info = TestSleeper.StartInfo();
            info.UseShellExecute = false;
            info.RedirectStandardOutput = true;
            var process = Process.Start(info)!;
            _processes.Add(process);
            return process;
        }

        public void Dispose()
        {
            foreach (var process in Registry.List().Select(x => x.Process).Concat(_processes))
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }
        }
    }

    /// <summary>Answers each probe with the next result in turn, then keeps giving the last.</summary>
    private sealed class ScriptedProbe(params Func<ProbeResult>[] answers) : IProcessProbe
    {
        public int Calls { get; private set; }

        public ProbeResult Probe(int pid, string marker, long? runId)
            => answers[Math.Min(Calls++, answers.Length - 1)]();
    }

    /// <summary>A paper trading service that only notes which runs were squared off.</summary>
    public class FlattenRecorder : DispatchProxy
    {
        private Action<long> _onFlatten = _ => { };

        public static IPaperTradingService Create(Action<long> onFlatten)
        {
            var proxy = DispatchProxy.Create<IPaperTradingService, FlattenRecorder>();
            ((FlattenRecorder)(object)proxy)._onFlatten = onFlatten;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IPaperTradingService.FlattenRunAsync))
            {
                _onFlatten((long)args![0]!);
                return Task.FromResult(0);
            }
            throw new NotSupportedException($"{targetMethod.Name} is not part of the reconcile.");
        }
    }
}
