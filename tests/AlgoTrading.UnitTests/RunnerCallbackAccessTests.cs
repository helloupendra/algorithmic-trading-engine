using System.Diagnostics;
using System.Net;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlgoTrading.UnitTests.DeskWriteAccessTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What a live run's runner reports, only the runner's account and an admin may
/// report. Every caller here holds the Strategies grant, so a refusal is the check
/// and not a missing module.
/// </summary>
/// <remarks>
/// Until 2 Nov a trader could inject signals into any live run's page and send the
/// desk fake "feed stalled" alerts about real runs, past the ten-minute throttle,
/// by naming a new underlying each call. The requests go through the API's own
/// policies and the controller's own attributes, in an in-process host.
/// </remarks>
public class RunnerCallbackAccessTests
{
    private const long Owner = TraderId;
    private const long OtherTrader = 9;
    private const long TheRun = 42;


    private const string StalledBody = """{"isStalled":true,"silentSeconds":200}""";

    public static TheoryData<string, string> StrategyRunnerCallbacks => new()
    {
        { $"/api/Strategy/runs/{TheRun}/feed", StalledBody },
        { $"/api/Strategy/runs/{TheRun}/signals", """{"signalType":"FORGED"}""" },
        { "/api/Strategy/7/signals", """{"signalType":"FORGED"}""" },
        // Past the policy the action looks the run up; this one is not there, so nothing is recorded.
        { "/api/Strategy/runs/77/runner", """{"processId":4242}""" },
    };

    [Theory]
    [MemberData(nameof(StrategyRunnerCallbacks))]
    public async Task Only_the_runner_and_an_admin_reach_the_runner_callbacks(string path, string body)
    {
        var notifier = new Notifier();
        await using var host = await StrategyHost(notifier);

        // The run's own owner is refused too: a person has no process to report and no feed to vouch for.
        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, body, Owner));
        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, body, OtherTrader));
        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", path, body, userId: null));
        Assert.Empty(notifier.Sent);
        Assert.Empty(notifier.Recorded);

        foreach (var caller in new[] { ServiceId, AdminId })
        {
            var status = await host.SendAsync("POST", path, body, caller);
            Assert.True(status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{path} answered {(int)status} to {caller}");
        }
    }

    [Fact]
    public async Task A_trader_cannot_raise_a_feed_alert_and_the_runner_still_can()
    {
        var notifier = new Notifier();
        await using var host = await StrategyHost(notifier);

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", $"/api/Strategy/runs/{TheRun}/feed", StalledBody, Owner));
        Assert.Empty(notifier.Sent);

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", $"/api/Strategy/runs/{TheRun}/feed", StalledBody, ServiceId));
        Assert.Equal("[coderforchange] Feed stalled — Ghost on NIFTY", Assert.Single(notifier.Sent));
    }

    [Fact]
    public async Task The_throttle_and_the_alert_go_by_the_runs_underlying_whatever_the_body_says()
    {
        var notifier = new Notifier();
        await using var host = await StrategyHost(notifier);

        foreach (var text in new[] { "FAKE-ONE", "FAKE-TWO", "nifty", "" })
        {
            var body = $$"""{"isStalled":true,"silentSeconds":200,"underlying":"{{text}}"}""";
            Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", $"/api/Strategy/runs/{TheRun}/feed", body, ServiceId));
        }

        Assert.Equal("[coderforchange] Feed stalled — Ghost on NIFTY", Assert.Single(notifier.Sent));
        Assert.Equal(3, notifier.Recorded.Count);
        Assert.All(notifier.Recorded, title => Assert.EndsWith(" on NIFTY", title));
        Assert.DoesNotContain(notifier.Messages, m => m.Contains("FAKE", StringComparison.Ordinal));

        // Per underlying still: another underlying's first stall is its own message.
        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", "/api/Strategy/runs/43/feed", StalledBody, ServiceId));
        Assert.Equal(2, notifier.Sent.Count);
        Assert.EndsWith(" on BANKNIFTY", notifier.Sent.Last());
    }

    [Fact]
    public async Task A_recovery_is_keyed_on_the_same_underlying_as_its_stall()
    {
        var notifier = new Notifier();
        await using var host = await StrategyHost(notifier);

        await host.SendAsync("POST", $"/api/Strategy/runs/{TheRun}/feed", StalledBody, ServiceId);
        await host.SendAsync("POST", $"/api/Strategy/runs/{TheRun}/feed", """{"isStalled":false,"silentSeconds":3,"underlying":"SOMETHING-ELSE"}""", ServiceId);

        Assert.Equal("[coderforchange] Feed recovered — Ghost on NIFTY", notifier.Sent.Last());
    }

    [Fact]
    public async Task A_trader_cannot_put_a_signal_on_a_live_runs_page_and_the_runner_can()
    {
        await using var host = await StrategyHost(new Notifier());
        var registry = host.Services.GetRequiredService<StrategyProcessRegistry>();

        var info = TestSleeper.StartInfo();
        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var sleeper = Process.Start(info)!;
        try
        {
            Assert.True(registry.TryAdd(new RunningStrategy(
                7, "Ghost", sleeper, StartedBy: "admin", UserId: Owner, StartedUtc: DateTime.UtcNow, RunId: TheRun,
                Underlying: "NIFTY", SpotSymbol: "NSE:NIFTY50-INDEX", Lots: 1, Risk: new RiskRulesDto())));

            foreach (var path in new[] { $"/api/Strategy/runs/{TheRun}/signals", "/api/Strategy/7/signals" })
            {
                Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, """{"signalType":"FORGED"}""", OtherTrader));
                Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", path, """{"signalType":"FORGED"}""", Owner));
            }
            Assert.Empty(registry.GetSignals(TheRun));

            Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", $"/api/Strategy/runs/{TheRun}/signals", """{"signalType":"ENTRY"}""", ServiceId));
            Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", "/api/Strategy/7/signals", """{"signalType":"ENTRY"}""", ServiceId));
            Assert.Equal(2, registry.GetSignals(TheRun).Count);
        }
        finally
        {
            sleeper.Kill(entireProcessTree: true);
        }
    }

    [Theory]
    [InlineData(Owner, HttpStatusCode.OK)]
    [InlineData(OtherTrader, HttpStatusCode.Forbidden)]
    public async Task The_owner_still_reads_their_runs_signals(long caller, HttpStatusCode expected)
    {
        await using var host = await StrategyHost(new Notifier());

        Assert.Equal(expected, await host.SendAsync("GET", $"/api/Strategy/runs/{TheRun}/signals", null, caller));
    }

    private static async Task<Host> StrategyHost(Notifier notifier)
    {
        string database = $"run-access-{Guid.NewGuid():N}";
        var host = await Host.StartAsync<StrategyController>(s =>
        {
            s.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(database));
            s.AddSingleton(EveryModuleGranted.Create());
            s.AddSingleton<ISystemNotifier>(notifier);
            s.AddSingleton<FeedStallAlertGate>();
            s.AddSingleton<StrategyProcessRegistry>();
            s.AddFake<IPaperTradingService>();
            s.AddFake<ILotSizeResolver>();
            s.AddFake<IStrategyAccessService>();
            s.AddUnused<StrategyCatalogService>();
            s.AddUnused<StrategyRunControl>();
            s.AddUnused<PythonEngineLocator>();
            s.AddUnused<PositionViewBuilder>();
            s.AddUnused<LiveRunHistoryBuilder>();
            s.AddUnused<RunCharges>();
            s.AddUnused<GetPaperOrdersUseCase>();
            s.AddUnused<UpsertWatchlistItemUseCase>();
        });

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.AppUsers.Add(new AppUser { Id = Owner, UserName = "coderforchange", Email = "a@example.com", PasswordHash = "x", Role = "Trader" });
        db.SimulationRuns.AddRange(
            new SimulationRun { Id = TheRun, UserId = Owner, Mode = "LivePaper", Status = "Running", StrategyName = "Ghost", Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m", ParametersJson = """{"underlying":"NIFTY","lots":1}""" },
            new SimulationRun { Id = 43, UserId = Owner, Mode = "LivePaper", Status = "Running", StrategyName = "Ghost", Symbol = "NSE:NIFTYBANK-INDEX", Resolution = "1m", ParametersJson = """{"underlying":"BANKNIFTY","lots":1}""" });
        await db.SaveChangesAsync();
        return host;
    }

    private sealed class Notifier : ISystemNotifier
    {
        public List<string> Sent { get; } = new();
        public List<string> Recorded { get; } = new();
        public List<string> Messages { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add(title);
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task RecordAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Recorded.Add(title);
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
