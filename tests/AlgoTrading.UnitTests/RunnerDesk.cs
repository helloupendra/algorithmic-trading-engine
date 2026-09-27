using System.Diagnostics;
using System.Security.Claims;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The start endpoint with everything behind it that a start touches: the
/// catalog, the registry, the run rows and a runner process.
/// </summary>
/// <remarks>
/// The runner is a real process. The engine directory is a temporary one whose
/// strategies/ holds one strategy the catalog finds by scanning ("Ghost") and
/// an execution_runner.py that only sleeps, run by the machine's Python — the
/// same interpreter the notifier and daemon tests use. A fake launcher would
/// test the fake; this way the registry counts, stops and exits real pids.
/// </remarks>
internal sealed class RunnerDesk : IDisposable
{
    public const long AdminId = 1;
    public const long TraderId = 7;
    public const long OtherTraderId = 8;

    private readonly string _engine = Directory.CreateTempSubdirectory("runner-desk-").FullName;
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _name = $"runner-desk-{Guid.NewGuid():N}";
    private readonly PythonEngineLocator _locator;
    private readonly StrategyCatalogService _catalog;
    private readonly List<Process> _processes = new();

    public RunnerDesk()
    {
        var strategies = Directory.CreateDirectory(Path.Combine(_engine, "strategies")).FullName;
        File.WriteAllText(Path.Combine(strategies, "ghost.py"), "class Ghost(BaseStrategy):\n    name = \"Ghost\"\n");
        File.WriteAllText(Path.Combine(strategies, "execution_runner.py"), "import time\ntime.sleep(30)\n");

        Options.EngineDirectory = _engine;
        Options.PythonExecutable = OperatingSystem.IsWindows() ? "python" : "python3";

        _locator = new PythonEngineLocator(Microsoft.Extensions.Options.Options.Create(Options),
            RecapClockTests.Inert<IWebHostEnvironment>.Create());
        _catalog = new StrategyCatalogService(_locator, NullLogger<StrategyCatalogService>.Instance);
        Registry = new StrategyProcessRegistry(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StrategyProcessRegistry>.Instance);

        using var db = Db();
        db.AppUsers.AddRange(
            new AppUser { Id = AdminId, UserName = "admin", Role = UserRoles.Admin },
            new AppUser { Id = TraderId, UserName = "coderforchange", Role = UserRoles.Trader },
            new AppUser { Id = OtherTraderId, UserName = "mallory", Role = UserRoles.Trader });
        foreach (var underlying in new[] { "NIFTY", "BANKNIFTY" })
        {
            db.Instruments.Add(new Instrument
            {
                Symbol = $"NSE:{underlying}26OCT25000CE", Exchange = "NSE", Segment = "FO", InstrumentType = "CE",
                OptionType = "CE", Underlying = underlying, StrikePrice = 25000m,
                ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30), IsEnabled = true
            });
        }
        db.SaveChanges();
    }

    /// <summary>The catalog id of the one strategy this desk has.</summary>
    public static int GhostId => StrategyCatalogService.StableId("Ghost");

    public StrategyRunnerOptions Options { get; } = new();

    public StrategyProcessRegistry Registry { get; }

    /// <summary>What the memory check reads; null is a host where it cannot be read.</summary>
    public long? AvailableMemoryBytes { get; set; }

    /// <summary>The platform's per-account cap on open runs (Risk limits); 0 is none.</summary>
    public int MaxRunsPerAccount { get; set; }

    public TradingDbContext Db()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(_name, _root)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        return new TradingDbContext(options.Options);
    }

    /// <summary>POST /api/Strategy/{Ghost}/start as <paramref name="callerId"/>, on its own request scope.</summary>
    public async Task<IActionResult> Start(string underlying, long callerId = AdminId, long? ownerUserId = null)
    {
        await using var db = Db();
        var controller = Controller(db, callerId);
        return await controller.StartStrategy(
            GhostId,
            new StartStrategyRequest { Underlying = underlying, OwnerUserId = ownerUserId },
            new FixedLimits(MaxRunsPerAccount),
            RecapClockTests.Inert<IDerivativesInstrumentService>.Create(),
            new FixedMemory(() => AvailableMemoryBytes),
            CancellationToken.None);
    }

    /// <summary>The durable runner pids (system_settings), as the reconcile reads them.</summary>
    public PidStore Pids { get; } = new();

    /// <summary>How the adoption path checks a stored pid; the real probe unless a test says otherwise.</summary>
    public IProcessProbe Probe { get; set; } = new SystemProcessProbe(NullLogger<SystemProcessProbe>.Instance);

    /// <summary>What the desk told its operator (alert titles).</summary>
    public List<string> Alerts => _notifier.Titles;

    private readonly RecordingNotifier _notifier = new();

    /// <summary>The stop and adoption paths, on <paramref name="db"/>. No waits between re-probes.</summary>
    public StrategyRunControl RunControl(TradingDbContext db) => new(
        db,
        RecapClockTests.Inert<IPaperTradingService>.Create(),
        Pids,
        Registry,
        null!,                                      // carry forward: only the market close uses it
        _locator,
        Probe,
        _notifier,
        NullLogger<StrategyRunControl>.Instance)
    {
        UnknownProbeRetryDelays = new[] { TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero }
    };

    /// <summary>A controller as <paramref name="callerId"/> sees it, for the endpoints other than start.</summary>
    public StrategyController Controller(TradingDbContext db, long callerId)
    {
        return new StrategyController(
            db,
            _catalog,
            Registry,
            RunControl(db),
            _locator,
            null!,                                  // paper trading
            null!,                                  // lot sizes
            null!,                                  // position views
            null!,                                  // history
            null!,                                  // charges
            null!,                                  // orders
            null!,                                  // watchlist: a failed upsert is only logged
            RecapClockTests.Inert<ISystemNotifier>.Create(),
            new AllowEverything(),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<StrategyController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserFor(callerId) } }
        };
    }

    public List<SimulationRun> Runs()
    {
        using var db = Db();
        return db.SimulationRuns.AsNoTracking().OrderBy(x => x.Id).ToList();
    }

    /// <param name="startedBy">Who started it, as a row from 28 Sep on records it; null is an older row.</param>
    public long SeedRun(long userId, string underlying, string status, string strategy = "Ghost",
        (long Id, string Name)? startedBy = null)
    {
        using var db = Db();
        var run = new SimulationRun
        {
            UserId = userId, Mode = StrategyRunControl.LivePaperMode, Status = status, StrategyName = strategy,
            StartedByUserId = startedBy?.Id, StartedByName = startedBy?.Name ?? string.Empty,
            Symbol = UnderlyingCatalog.SpotSymbolFor(underlying), ParametersJson = $"{{\"underlying\":\"{underlying}\"}}",
            CreatedUtc = DateTime.UtcNow.AddHours(-1), StartedUtc = DateTime.UtcNow.AddHours(-1)
        };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    /// <summary>
    /// A live process a probe recognises as the runner of <paramref name="runId"/>,
    /// as one left behind by the previous API process would be.
    /// </summary>
    public Process RunnerFor(long runId)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            // Windows never reads the command line, so any live process will do.
            info = TestSleeper.StartInfo();
        }
        else
        {
            // "; :" keeps the shell itself alive (a lone command would be exec'd),
            // so its command line reads "/bin/sh -c sleep 30; : execution_runner --run-id <id>".
            info = new ProcessStartInfo("/bin/sh");
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("sleep 30; :");
            info.ArgumentList.Add(ProcessProbe.StrategyRunnerMarker);
            info.ArgumentList.Add("--run-id");
            info.ArgumentList.Add(runId.ToString());
        }

        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;

        var process = Process.Start(info)!;
        _processes.Add(process);
        return process;
    }

    private static ClaimsPrincipal UserFor(long id)
    {
        var (name, admin) = id switch
        {
            AdminId => ("admin", true),
            TraderId => ("coderforchange", false),
            _ => ("mallory", false)
        };
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, name) };
        if (admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
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

        try { Directory.Delete(_engine, recursive: true); } catch (IOException) { }
    }

    /// <summary>system_settings' pid rows, in memory.</summary>
    public sealed class PidStore : IProcessSettingsStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _values = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) && int.TryParse(value, out var pid) && pid > 0
                ? pid
                : (int?)null);

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default)
            => SetAsync(key, processId.ToString(), updatedBy, cancellationToken);

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryRemove(key, out _));

        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default)
            => Task.FromResult(_values.TryGetValue(key, out var value) && value == processId.ToString()
                               && _values.TryRemove(key, out _));

        public bool Has(string key) => _values.ContainsKey(key);
    }

    private sealed class RecordingNotifier : ISystemNotifier
    {
        public List<string> Titles { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            lock (Titles) Titles.Add(title);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedLimits(int maxRuns) : IRiskLimitsStore
    {
        public RiskLimitsDto GetLimits() => new() { MaxConcurrentRuns = maxRuns };

        public Task UpdateLimitsAsync(RiskLimitsDto newLimits, string updatedBy, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FixedMemory(Func<long?> read) : IHostMemory
    {
        public long? AvailableBytes() => read();
    }

    /// <summary>Every package allows everything: the package rules have tests of their own.</summary>
    private sealed class AllowEverything : IStrategyAccessService
    {
        public Task<StrategyAccess> GetAccessAsync(long userId, CancellationToken cancellationToken = default)
            => Task.FromResult(StrategyAccess.Unrestricted);

        public Task<StrategyAccessDecision> CanDeployAsync(long userId, string strategyName, string underlying, int lots,
            string mode, int currentOpenRuns, CancellationToken cancellationToken = default)
            => Task.FromResult(StrategyAccessDecision.Ok);
    }
}
