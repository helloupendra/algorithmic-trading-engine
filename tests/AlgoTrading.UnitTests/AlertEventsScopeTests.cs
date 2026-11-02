using System.Net;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;
using static AlgoTrading.UnitTests.DeskWriteAccessTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/Alerts/events: an admin reads the desk's alerts, anyone else those of
/// their own runs.
/// </summary>
/// <remarks>
/// Until 2 Nov every signed-in account read the whole desk's last 500: other
/// accounts' run alerts and run ids, who pulled the kill switch and why, and the
/// broker and feed failures.
/// </remarks>
public class AlertEventsScopeTests
{
    private const long Owner = TraderId;
    private const long OtherTrader = 9;
    private const string Path = "/api/Alerts/events";

    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    private static async Task<Host> AlertsHost()
    {
        string database = $"alert-scope-{Guid.NewGuid():N}";
        var host = await Host.StartAsync<AlertsController>(s =>
        {
            s.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(database));
            s.AddFake<IConnectionMultiplexer>();
            s.AddUnused<AlertsSupervisor>();
        });

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.SimulationRuns.AddRange(
            new SimulationRun { Id = 42, UserId = Owner, Mode = "LivePaper", Status = "Running", StrategyName = "Ghost", Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m" },
            new SimulationRun { Id = 43, UserId = OtherTrader, Mode = "LivePaper", Status = "Running", StrategyName = "Ghost", Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m" });
        db.AlertEvents.AddRange(
            Alert(1, "own run", 42, Now.AddMinutes(1)),
            Alert(2, "other account's run", 43, Now.AddMinutes(2)),
            Alert(3, "kill switch, by admin", null, Now.AddMinutes(3)),
            Alert(4, "run that is gone", 999, Now.AddMinutes(4)));
        await db.SaveChangesAsync();
        return host;
    }

    private static AlertEvent Alert(long id, string title, long? runId, DateTime at) => new()
    {
        Id = id, Title = title, SimulationRunId = runId, OccurredUtc = at, Source = "test", Underlying = "NIFTY", Severity = "info", Message = title,
    };

    private static async Task<List<string>> TitlesAsync(Host host, long caller, string query = "")
    {
        using var response = await host.ExchangeAsync("GET", Path + query, null, caller);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToList();
    }

    [Fact]
    public async Task A_trader_reads_the_alerts_of_their_own_runs_and_nothing_else()
    {
        await using var host = await AlertsHost();

        Assert.Equal(new[] { "own run" }, await TitlesAsync(host, Owner));
        Assert.Equal(new[] { "other account's run" }, await TitlesAsync(host, OtherTrader));
    }

    [Fact]
    public async Task An_account_with_no_runs_reads_nothing()
    {
        await using var host = await AlertsHost();

        Assert.Empty(await TitlesAsync(host, 77));
    }

    [Fact]
    public async Task An_admin_reads_the_whole_desk_newest_first()
    {
        await using var host = await AlertsHost();

        Assert.Equal(new[] { "run that is gone", "kill switch, by admin", "other account's run", "own run" }, await TitlesAsync(host, AdminId));
    }

    [Fact]
    public async Task The_service_account_is_no_admin_here_and_owns_no_runs()
    {
        await using var host = await AlertsHost();

        Assert.Empty(await TitlesAsync(host, ServiceId));
    }

    [Fact]
    public async Task The_limit_counts_what_the_caller_may_see()
    {
        // The other alerts are newer: a limit taken before the scope would leave this trader nothing.
        await using var host = await AlertsHost();

        Assert.Equal(new[] { "own run" }, await TitlesAsync(host, Owner, "?limit=1"));
        Assert.Equal(new[] { "run that is gone" }, await TitlesAsync(host, AdminId, "?limit=1"));
    }

    [Fact]
    public async Task Anonymous_is_refused()
    {
        await using var host = await AlertsHost();

        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("GET", Path, null, userId: null));
    }
}
