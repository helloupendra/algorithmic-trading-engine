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

    /// <summary>A controller as <paramref name="callerId"/> sees it, for the endpoints other than start.</summary>
    public StrategyController Controller(TradingDbContext db, long callerId)
    {
        var runControl = new StrategyRunControl(
            db,
            RecapClockTests.Inert<IPaperTradingService>.Create(),
            RecapClockTests.Inert<IProcessSettingsStore>.Create(),
            Registry,
            null!,                                  // carry forward: no stop happens here
            NullLogger<StrategyRunControl>.Instance);

        return new StrategyController(
            db,
            _catalog,
            Registry,
            runControl,
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

    public long SeedRun(long userId, string underlying, string status, string strategy = "Ghost")
    {
        using var db = Db();
        var run = new SimulationRun
        {
            UserId = userId, Mode = StrategyRunControl.LivePaperMode, Status = status, StrategyName = strategy,
            Symbol = UnderlyingCatalog.SpotSymbolFor(underlying), ParametersJson = $"{{\"underlying\":\"{underlying}\"}}",
            CreatedUtc = DateTime.UtcNow.AddHours(-1), StartedUtc = DateTime.UtcNow.AddHours(-1)
        };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
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
        foreach (var entry in Registry.List())
        {
            try
            {
                if (!entry.Process.HasExited) entry.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        try { Directory.Delete(_engine, recursive: true); } catch (IOException) { }
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
