using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/Risk/exposure for a trader: their own live runs, and nobody else's.
/// </summary>
/// <remarks>
/// The trader Library warns before a start when runs are already live, and it
/// reads that from this endpoint. The endpoint was admin-only, so for a trader
/// it answered 403 and the warning never appeared. It now sits behind the
/// strategies grant, as the runs, positions and orders do, and is scoped the
/// same way: a trader sees their own runs; an admin sees every account's.
/// </remarks>
public class RiskExposureScopeTests : IDisposable
{
    private readonly RunnerDesk _desk = new();
    private readonly List<Process> _processes = new();

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        _desk.Dispose();
        foreach (var process in _processes) process.Dispose();
    }

    [Fact]
    public async Task A_trader_sees_only_their_own_live_runs_and_their_P_and_L()
    {
        var (own, others) = SeedTwoAccounts();

        var exposure = await Exposure(RunnerDesk.TraderId);

        Assert.Equal(1, exposure.ActiveRunsCount);
        var run = Assert.Single(exposure.ActiveRuns);
        Assert.Equal(own, run.RunId);
        Assert.Equal(250m, exposure.TotalRealizedPnL);
        Assert.DoesNotContain(exposure.ActiveRuns, r => r.RunId == others);
    }

    [Fact]
    public async Task An_admin_still_sees_every_account()
    {
        var (own, others) = SeedTwoAccounts();

        var exposure = await Exposure(RunnerDesk.AdminId);

        Assert.Equal(2, exposure.ActiveRunsCount);
        Assert.Equal(new[] { own, others }, exposure.ActiveRuns.Select(r => r.RunId).OrderBy(id => id));
        Assert.Equal(250m + 900m, exposure.TotalRealizedPnL);
    }

    [Fact]
    public void The_endpoint_is_behind_the_strategies_grant_not_the_admin_policy()
    {
        var method = typeof(RiskController).GetMethod(nameof(RiskController.GetExposure))!;

        Assert.Equal(PlatformModules.Strategies, ModuleOf(method.GetCustomAttribute<RequireModuleAttribute>()));
        Assert.DoesNotContain(method.GetCustomAttributes<AuthorizeAttribute>(),
            a => a.Policy == AuthorizationPolicies.AdminOnly);
        Assert.Null(typeof(RiskController).GetCustomAttribute<AuthorizeAttribute>());
    }

    // ------------------------------------------------------------ helpers

    /// <summary>One live run for the trader (₹250 booked) and one for another trader (₹900 booked).</summary>
    private (long Own, long Others) SeedTwoAccounts()
    {
        long own = _desk.SeedRun(RunnerDesk.TraderId, "NIFTY", StrategyRunControl.RunStatusRunning);
        long others = _desk.SeedRun(RunnerDesk.OtherTraderId, "BANKNIFTY", StrategyRunControl.RunStatusRunning);

        using (var db = _desk.Db())
        {
            db.PaperPositions.AddRange(Closed(own, "NSE:NIFTY26OCT25000CE", 250m), Closed(others, "NSE:BANKNIFTY26OCT25000CE", 900m));
            db.SaveChanges();
        }

        Assert.True(_desk.Registry.TryAdd(Live(own, RunnerDesk.TraderId, "NIFTY")));
        Assert.True(_desk.Registry.TryAdd(Live(others, RunnerDesk.OtherTraderId, "BANKNIFTY")));
        return (own, others);
    }

    private async Task<RiskExposureResponse> Exposure(long callerId)
    {
        await using var db = _desk.Db();
        var lots = new PositionGreeksTests.FixedLots(75);
        var charges = new RunCharges(db, lots);
        var catalog = new StrategyCatalogService(
            new PythonEngineLocator(Microsoft.Extensions.Options.Options.Create(_desk.Options),
                RecapClockTests.Inert<IWebHostEnvironment>.Create()),
            NullLogger<StrategyCatalogService>.Instance);
        var history = new LiveRunHistoryBuilder(db, _desk.Registry, catalog, lots, charges, new RunPnl(db, lots, charges));

        var controller = new RiskController(
            RecapClockTests.Inert<IRiskManagementService>.Create(),
            RecapClockTests.Inert<IPaperTradingService>.Create(),
            RecapClockTests.Inert<ISystemNotifier>.Create())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = UserFor(callerId) } }
        };

        var result = Assert.IsType<OkObjectResult>(await controller.GetExposure(_desk.Registry, history, CancellationToken.None));
        return Assert.IsType<RiskExposureResponse>(result.Value);
    }

    private static PaperPosition Closed(long runId, string symbol, decimal realized) => new()
    {
        SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = symbol, Direction = "SHORT",
        Quantity = 0, AveragePrice = 100m, LastMarkPrice = 90m, RealizedPnl = realized, Status = "Closed",
        OpenedUtc = DateTime.UtcNow.AddMinutes(-30), ClosedUtc = DateTime.UtcNow.AddMinutes(-10), UpdatedUtc = DateTime.UtcNow.AddMinutes(-10)
    };

    /// <summary>A registry entry around a live process: one that has exited is swept out at once.</summary>
    private RunningStrategy Live(long runId, long userId, string underlying)
    {
        var info = TestSleeper.StartInfo();
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        var process = Process.Start(info)!;
        _processes.Add(process);

        return new RunningStrategy(
            RunnerDesk.GhostId, "Ghost", process, StartedBy: "admin", UserId: userId, StartedUtc: DateTime.UtcNow,
            RunId: runId, Underlying: underlying, SpotSymbol: UnderlyingCatalog.SpotSymbolFor(underlying), Lots: 1,
            Risk: new RiskRulesDto());
    }

    private static string? ModuleOf(RequireModuleAttribute? attribute)
        => attribute is null
            ? null
            : (string?)typeof(RequireModuleAttribute)
                .GetField("_moduleKey", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(attribute);

    private static ClaimsPrincipal UserFor(long id)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()), new(ClaimTypes.Name, $"user{id}") };
        if (id == RunnerDesk.AdminId) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }
}
