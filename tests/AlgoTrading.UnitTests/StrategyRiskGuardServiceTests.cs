using System.Diagnostics;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The guard judges a leg only on a price the market is still at.
/// </summary>
/// <remarks>
/// On 24 Sep the feed stalled from 11:27:36 to 11:34:06, and the guard went on
/// evaluating leg targets for runs 205, 206, 207, 234 and 235 on the frozen
/// quotes. A stale mark now pauses the leg and group rules of its run, the
/// owner hears about it once per stall, and the rules resume on the first
/// fresh quote.
/// </remarks>
public class StrategyRiskGuardServiceTests
{
    private const string Call = "NSE:NIFTY2692925000CE";
    private const string Put = "NSE:NIFTY2692925000PE";

    private static RiskRulesDto LegAndGroupStops() => new()
    {
        // A SELL leg 60 points against it trips a 20-point leg stop at once.
        Leg = new LegRiskDto { StopLossPoints = 20m },
        Group = new GroupRiskDto { StopLoss = 1_000m },
    };

    [Fact]
    public async Task StaleMark_SkipsRules_AlertsOnce()
    {
        using var desk = new GuardDesk();
        long run = desk.NewRun();
        long leg = desk.Fill(run, "G1", Call, "SELL", 2, 100m);
        desk.Register(run, LegAndGroupStops());
        desk.Quote(Call, 160m, age: TimeSpan.FromMinutes(6));
        var guard = desk.Guard();

        await guard.SweepOnceAsync(CancellationToken.None);
        await guard.SweepOnceAsync(CancellationToken.None);
        await guard.SweepOnceAsync(CancellationToken.None);

        Assert.Equal("Open", desk.Position(leg).Status);
        var alert = Assert.Single(desk.Notifier.Sent);
        Assert.Equal("Risk rules paused — Ghost on NIFTY", alert.Title);
        Assert.Matches($@"^Run #{run}: no price newer than 30s for NIFTY 25000 CE \(oldest 36[01]s\)\. ", alert.Message);
        Assert.Single(desk.Registry.GetLogs(run, 100), x => x.Contains("leg and group rules paused"));

        // The quote moves again: the rules are back, and the stop trips on a
        // price the market is actually at.
        desk.Quote(Call, 160m, age: TimeSpan.FromSeconds(1));
        await guard.SweepOnceAsync(CancellationToken.None);

        Assert.Equal("Closed", desk.Position(leg).Status);
        Assert.Single(desk.Notifier.Sent);
    }

    [Fact]
    public async Task A_stale_leg_takes_its_group_out_but_not_the_other_groups()
    {
        using var desk = new GuardDesk();
        long run = desk.NewRun();
        long stale = desk.Fill(run, "G1", Call, "SELL", 2, 100m);
        long freshInStaleGroup = desk.Fill(run, "G1", Put, "SELL", 2, 100m);
        desk.Quote(Call, 101m, age: TimeSpan.FromMinutes(2));
        // 10 points against it: inside the leg stop, but the group of both
        // legs is well past its ₹1,000 stop (−10 × 2 lots × 65 = −1,300).
        desk.Quote(Put, 110m, age: TimeSpan.FromSeconds(1));
        desk.Register(run, LegAndGroupStops());

        await desk.Guard().SweepOnceAsync(CancellationToken.None);

        Assert.Equal("Open", desk.Position(stale).Status);
        Assert.Equal("Open", desk.Position(freshInStaleGroup).Status);
    }

    [Fact]
    public async Task A_second_stall_is_a_second_incident()
    {
        using var desk = new GuardDesk();
        long run = desk.NewRun();
        desk.Fill(run, "G1", Call, "SELL", 2, 100m);
        desk.Register(run, LegAndGroupStops());
        var guard = desk.Guard();

        desk.Quote(Call, 100m, age: TimeSpan.FromMinutes(2));
        await guard.SweepOnceAsync(CancellationToken.None);
        desk.Quote(Call, 100m, age: TimeSpan.FromSeconds(1));
        await guard.SweepOnceAsync(CancellationToken.None);
        desk.Quote(Call, 100m, age: TimeSpan.FromMinutes(2));
        await guard.SweepOnceAsync(CancellationToken.None);

        Assert.Equal(2, desk.Notifier.Sent.Count);
        Assert.Contains(desk.Registry.GetLogs(run, 100), x => x.Contains("marks are fresh again"));
    }

    [Fact]
    public async Task A_run_with_only_overall_rules_has_nothing_to_pause()
    {
        using var desk = new GuardDesk();
        long run = desk.NewRun();
        desk.Fill(run, "G1", Call, "SELL", 2, 100m);
        desk.Quote(Call, 100m, age: TimeSpan.FromMinutes(6));
        desk.Register(run, new RiskRulesDto { Overall = new OverallRiskDto { StopLoss = 50_000m } });

        await desk.Guard().SweepOnceAsync(CancellationToken.None);

        Assert.Empty(desk.Notifier.Sent);
    }

    /// <summary>One run on a registry, over an in-memory database, with a notifier that keeps what it is sent.</summary>
    private sealed class GuardDesk : IDisposable
    {
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"guard-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;
        private readonly List<Process> _processes = new();

        public GuardDesk()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<TradingDbContext>(Configure);
            services.AddSingleton<IRiskManagementService>(RecapClockTests.Inert<IRiskManagementService>.Create());
            services.AddSingleton<IProcessSettingsStore>(RecapClockTests.Inert<IProcessSettingsStore>.Create());
            services.AddSingleton<ILotSizeResolver>(new PositionGreeksTests.FixedLots(65));
            services.AddSingleton<ISystemNotifier>(Notifier);
            services.AddScoped<IPaperTradingService, PaperTradingService>();
            services.AddSingleton<StrategyProcessRegistry>();
            services.AddScoped<PositionCarryForward>();
            services.AddScoped<StrategyRunControl>();
            services.AddScoped<RunCharges>();
            services.Configure<StrategyRunnerOptions>(o => o.RiskGuardIntervalSeconds = 1);
            _provider = services.BuildServiceProvider();
            Registry = _provider.GetRequiredService<StrategyProcessRegistry>();
        }

        public StrategyProcessRegistry Registry { get; }
        public RecordingNotifier Notifier { get; } = new();

        private void Configure(DbContextOptionsBuilder options) => options
            .UseInMemoryDatabase(_name, _root)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

        private TradingDbContext Db()
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>();
            Configure(options);
            return new TradingDbContext(options.Options);
        }

        public long NewRun()
        {
            using var db = Db();
            var run = new SimulationRun
            {
                UserId = 7, Mode = PaperTradingService.LivePaperMode, Status = "Running", Symbol = "NSE:NIFTY50-INDEX",
                StrategyName = "Ghost", ParametersJson = "{}"
            };
            db.SimulationRuns.Add(run);
            db.SaveChanges();
            return run.Id;
        }

        public long Fill(long runId, string group, string symbol, string side, int lots, decimal price)
        {
            using var db = Db();
            var position = new PaperPosition
            {
                SimulationRunId = runId, StrategyName = "Ghost", GroupId = group, Symbol = symbol,
                Direction = side == "BUY" ? "LONG" : "SHORT", Quantity = lots, AveragePrice = price,
                LastMarkPrice = price, Status = "Open", OpenedUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            db.PaperPositions.Add(position);
            db.SaveChanges();
            return position.Id;
        }

        public void Quote(string symbol, decimal ltp, TimeSpan age)
        {
            using var db = Db();
            var row = db.LiveQuotesLatest.SingleOrDefault(x => x.Symbol == symbol);
            if (row is null)
            {
                row = new LiveQuoteLatest { Symbol = symbol, RawPayload = "{}", SourceKey = "dhan" };
                db.LiveQuotesLatest.Add(row);
            }
            row.LastTradedPrice = ltp;
            row.UpdatedUtc = DateTime.UtcNow - age;
            db.SaveChanges();
        }

        public PaperPosition Position(long id)
        {
            using var db = Db();
            return db.PaperPositions.AsNoTracking().Single(x => x.Id == id);
        }

        public void Register(long runId, RiskRulesDto risk)
        {
            var info = TestSleeper.StartInfo();
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.UseShellExecute = false;
            var process = Process.Start(info)!;
            _processes.Add(process);

            Assert.True(Registry.TryAdd(new RunningStrategy(
                StrategyCatalogService.StableId("Ghost"), "Ghost", process, StartedBy: "admin", UserId: 7,
                StartedUtc: DateTime.UtcNow.AddMinutes(-30), RunId: runId, Underlying: "NIFTY",
                SpotSymbol: "NSE:NIFTY50-INDEX", Lots: 2, Risk: risk)));
        }

        public StrategyRiskGuardService Guard() => new(
            Registry,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<IOptionsMonitor<StrategyRunnerOptions>>(),
            NullLogger<StrategyRiskGuardService>.Instance);

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
                    // Already gone.
                }
                process.Dispose();
            }
            _provider.Dispose();
        }
    }

    internal sealed class RecordingNotifier : ISystemNotifier
    {
        private readonly object _lock = new();
        private readonly List<(string Title, string Message)> _sent = new();

        public IReadOnlyList<(string Title, string Message)> Sent
        {
            get { lock (_lock) return _sent.ToList(); }
        }

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            lock (_lock) _sent.Add((title, message));
            return Task.CompletedTask;
        }

        public Task RecordAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
