using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.Simulator;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlgoTrading.UnitTests.DeskWriteAccessTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Simulator's portfolio/refresh, progress and complete apply the test its
/// other run endpoints apply: an admin, the Service account every runner signs in
/// as, or the run's own account. Every caller here holds the Strategies grant, so
/// a refusal is the check and not a missing module.
/// </summary>
/// <remarks>
/// Until 2 Nov they needed only that grant: a trader could read any live run's
/// capital and P&amp;L through portfolio/refresh, while GET portfolio refused them,
/// and mark anyone's backtest Completed with a summary of their choosing. The
/// requests go through the API's own policies and the controller's own attributes,
/// in an in-process host.
/// </remarks>
public class SimulatorRunAccessTests
{
    private const long Owner = TraderId;
    private const long OtherTrader = 9;
    private const long TheRun = 42;


    public static TheoryData<string, string?> SimulatorRunnerWrites => new()
    {
        { $"/api/Simulator/runs/{TheRun}/portfolio/refresh", null },
        { $"/api/Simulator/runs/{TheRun}/complete", """{"status":"Completed","summary":{"netPnl":123456}}""" },
        { $"/api/Simulator/runs/{TheRun}/progress", """{"percent":50,"barsProcessed":1,"totalBars":2,"trades":0,"message":"forged"}""" },
    };

    [Theory]
    [MemberData(nameof(SimulatorRunnerWrites))]
    public async Task Another_trader_cannot_touch_a_run_on_the_simulator(string path, string? body)
    {
        await using var host = await Host.StartAsync<SimulatorController>(SimulatorServices);

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, body, OtherTrader));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", path, body, userId: null));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [MemberData(nameof(SimulatorRunnerWrites))]
    public async Task A_run_nobody_knows_is_refused_for_a_trader_as_another_accounts_run_is(string path, string? body)
    {
        await using var host = await Host.StartAsync<SimulatorController>(SimulatorServices);

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path.Replace($"/{TheRun}/", "/4242/"), body, Owner));
        Assert.Empty(host.Calls);
    }

    [Theory]
    [InlineData(ServiceId)]   // the backtest runner and the terminal dashboards sign in as Service
    [InlineData(AdminId)]
    [InlineData(Owner)]
    public async Task The_runner_an_admin_and_the_runs_owner_pass_the_check(long caller)
    {
        await using var host = await Host.StartAsync<SimulatorController>(SimulatorServices);

        Assert.Equal(HttpStatusCode.NoContent, await host.SendAsync("POST", $"/api/Simulator/runs/{TheRun}/portfolio/refresh", null, caller));
        Assert.Contains("IPaperTradingService.RefreshPortfolioMarkToMarketAsync", host.Calls);

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", $"/api/Simulator/runs/{TheRun}/complete", """{"status":"Failed","error":"no candles"}""", caller));
        Assert.Contains("IPaperTradingService.CompleteRunAsync", host.Calls);

        // Past the check, the registry answers: no backtest of this id is running in this host.
        using var progress = await host.ExchangeAsync("POST", $"/api/Simulator/runs/{TheRun}/progress", """{"percent":50,"processId":77}""", caller);
        Assert.Equal(HttpStatusCode.NotFound, progress.StatusCode);
        Assert.Contains("is not running", await progress.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_runner_is_told_about_a_run_that_is_gone_rather_than_refused()
    {
        // backtest_runner.py reads "404 when the run is not running"; a 403 here would read as an auth fault.
        await using var host = await Host.StartAsync<SimulatorController>(SimulatorServices);

        Assert.Equal(HttpStatusCode.NotFound, await host.SendAsync("POST", "/api/Simulator/runs/4242/progress", """{"percent":1}""", ServiceId));
    }

    private static void SimulatorServices(IServiceCollection s)
    {
        s.AddSingleton(EveryModuleGranted.Create());
        s.AddSingleton<ISimulationService>(_ => OneRun.Create());
        s.AddFake<IPaperTradingService>();
        s.AddFake<ISimulationRunner>();
        s.AddSingleton<BacktestProcessRegistry>();
        s.AddUnused<BacktestRunControl>();
        foreach (var useCase in typeof(GetSimulationRunUseCase).Assembly.GetTypes()
                     .Where(t => (t.Namespace is null || t.Namespace == typeof(GetSimulationRunUseCase).Namespace) && t.IsClass && !t.IsAbstract && t.Name.EndsWith("UseCase", StringComparison.Ordinal)))
        {
            s.AddTransient(useCase);
        }
    }

    /// <summary>A simulation service that knows one run, the <see cref="Owner"/>'s.</summary>
    public class OneRun : DispatchProxy
    {
        public static ISimulationService Create() => DispatchProxy.Create<ISimulationService, OneRun>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(ISimulationService.GetRunAsync))
                return RecordingFake<ISimulationService>.Answer(targetMethod.ReturnType, new ConcurrentQueue<string>());

            return Task.FromResult<SimulationRunResponse?>((long)args![0]! == TheRun ? new SimulationRunResponse { Id = TheRun, UserId = Owner } : null);
        }
    }
}
