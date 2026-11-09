using System.Net;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Simulator;
using AlgoTrading.Contracts.Backtest;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlgoTrading.UnitTests.DeskWriteAccessTests;

namespace AlgoTrading.UnitTests;

public sealed class ReplayMarksTests
{
    private const long RunId = 42;
    private const string Symbol = "NSE:TEST-EQ";
    private static readonly DateTime Start = new(2026, 9, 1, 4, 0, 0, DateTimeKind.Utc);
    private static RunMarksRequest Marks(decimal price = 120m) => new()
    {
        AtUtc = Start.AddMinutes(1), Marks = [new() { Symbol = Symbol, Price = price }],
    };

    [Theory]
    [InlineData("LivePaper")]
    [InlineData("livepaper")]
    [InlineData("unknown")]
    public async Task Direct_service_calls_cannot_replace_live_prices(string mode)
    {
        using var desk = new Desk(mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => desk.Paper.ApplyMarksAsync(RunId, Marks(1_000_000m)));
        Assert.Equal((100m, 0m, Start), await desk.Position());
        await Assert.ThrowsAsync<InvalidOperationException>(() => desk.Paper.ApplyMarksAsync(RunId, new() { AtUtc = Start, Marks = [] }));
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Running")]
    public async Task Valid_replay_marks_use_the_bar_time_and_touch_only_open_positions_of_that_run(string status)
    {
        using var desk = new Desk(status: status);
        desk.Db.PaperPositions.Add(new() { Id = 2, SimulationRunId = RunId, Symbol = Symbol, Status = "Closed", LastMarkPrice = 101m });
        desk.Db.SimulationRuns.Add(new() { Id = 43, UserId = 9, Mode = "LivePaper", Status = "Running" });
        desk.Db.PaperPositions.Add(new() { Id = 3, SimulationRunId = 43, Symbol = Symbol, Status = "Open", LastMarkPrice = 102m });
        await desk.Db.SaveChangesAsync();
        Assert.Equal(1, await desk.Paper.ApplyMarksAsync(RunId, Marks()));
        Assert.Equal((120m, 40m, Start.AddMinutes(1)), await desk.Position());
        Assert.Equal(101m, (await desk.Db.PaperPositions.AsNoTracking().SingleAsync(x => x.Id == 2)).LastMarkPrice);
        Assert.Equal(102m, (await desk.Db.PaperPositions.AsNoTracking().SingleAsync(x => x.Id == 3)).LastMarkPrice);
        // A retry at the same bar has the same result.
        Assert.Equal(1, await desk.Paper.ApplyMarksAsync(RunId, Marks()));
        Assert.Equal((120m, 40m, Start.AddMinutes(1)), await desk.Position());
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Stopped")]
    [InlineData("Failed")]
    public async Task Marks_cannot_modify_a_finished_run(string status)
    {
        using var desk = new Desk(status: status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => desk.Paper.ApplyMarksAsync(RunId, Marks()));
        Assert.Equal((100m, 0m, Start), await desk.Position());
    }

    [Theory]
    [InlineData("missing-time")]
    [InlineData("no-offset")]
    [InlineData("before-range")]
    [InlineData("after-range")]
    [InlineData("missing-range")]
    [InlineData("older-than-position")]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("blank-symbol")]
    [InlineData("duplicate")]
    [InlineData("null-mark")]
    public async Task Invalid_batches_leave_every_position_unchanged(string kind)
    {
        using var desk = new Desk();
        var request = Marks();
        switch (kind)
        {
            case "missing-time": request.AtUtc = default; break;
            case "no-offset": request.AtUtc = DateTime.SpecifyKind(request.AtUtc, DateTimeKind.Unspecified); break;
            case "before-range": request.AtUtc = Start.AddSeconds(-1); break;
            case "after-range": request.AtUtc = Start.AddHours(2); break;
            case "missing-range": (await desk.Db.SimulationRuns.SingleAsync()).FromUtc = null; await desk.Db.SaveChangesAsync(); break;
            case "older-than-position": request.AtUtc = Start.AddMinutes(1); (await desk.Db.PaperPositions.SingleAsync()).UpdatedUtc = Start.AddMinutes(2); await desk.Db.SaveChangesAsync(); break;
            case "negative": request.Marks.Add(new() { Symbol = "OTHER", Price = -1 }); break;
            case "zero": request.Marks.Add(new() { Symbol = "OTHER", Price = 0 }); break;
            case "blank-symbol": request.Marks.Add(new() { Symbol = " ", Price = 1 }); break;
            case "duplicate": request.Marks.Add(new() { Symbol = " " + Symbol + " ", Price = 1 }); break;
            case "null-mark": request.Marks.Add(null!); break;
        }
        var before = await desk.Position();
        await Assert.ThrowsAsync<InvalidOperationException>(() => desk.Paper.ApplyMarksAsync(RunId, request));
        Assert.Equal(before, await desk.Position());
    }

    [Fact]
    public async Task Live_portfolio_refresh_still_uses_the_server_quote()
    {
        using var desk = new Desk("LivePaper");
        desk.Db.LiveQuotesLatest.Add(new() { Symbol = Symbol, LastTradedPrice = 125m, UpdatedUtc = DateTime.UtcNow });
        await desk.Db.SaveChangesAsync();
        var result = await desk.Paper.RefreshPortfolioMarkToMarketAsync(RunId);
        Assert.Equal(50m, result.UnrealizedPnl);
        Assert.Equal(125m, (await desk.Position()).Price);
    }

    [Theory]
    [InlineData(ServiceId, "OfflineReplay", HttpStatusCode.OK)]
    [InlineData(AdminId, "OfflineReplay", HttpStatusCode.OK)]
    [InlineData(TraderId, "OfflineReplay", HttpStatusCode.Forbidden)]
    [InlineData(9L, "OfflineReplay", HttpStatusCode.Forbidden)]
    [InlineData(ServiceId, "LivePaper", HttpStatusCode.BadRequest)]
    [InlineData(AdminId, "LivePaper", HttpStatusCode.BadRequest)]
    [InlineData(TraderId, "LivePaper", HttpStatusCode.Forbidden)]
    [InlineData(null, "OfflineReplay", HttpStatusCode.Unauthorized)]
    public async Task Endpoint_enforces_runner_roles_and_service_mode(long? caller, string mode, HttpStatusCode expected)
    {
        using var desk = new Desk(mode);
        await using var host = await Host.StartAsync<SimulatorController>(desk.Register);
        var body = JsonSerializer.Serialize(Marks());
        Assert.Equal(expected, await host.SendAsync("POST", $"/api/Simulator/runs/{RunId}/marks", body, caller));
        Assert.Equal(expected == HttpStatusCode.OK ? 120m : 100m, (await desk.Position()).Price);
    }

    private sealed class Desk : IDisposable
    {
        public TradingDbContext Db { get; } = new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public PaperTradingService Paper { get; }
        public Desk(string mode = "OfflineReplay", string status = "Running")
        {
            Db.SimulationRuns.Add(new() { Id = RunId, UserId = TraderId, Mode = mode, Status = status, FromUtc = Start, ToUtc = Start.AddHours(1) });
            Db.PaperPositions.Add(new() { Id = 1, SimulationRunId = RunId, Symbol = Symbol, Direction = "LONG", Quantity = 2,
                AveragePrice = 100m, LastMarkPrice = 100m, UnrealizedPnl = 0m, UpdatedUtc = Start, OpenedUtc = Start, Status = "Open" });
            Db.SaveChanges();
            Paper = new(Db, RecapClockTests.Inert<IRiskManagementService>.Create(), new PositionGreeksTests.FixedLots(1));
        }
        public async Task<(decimal? Price, decimal Pnl, DateTime At)> Position()
        {
            var p = await Db.PaperPositions.AsNoTracking().SingleAsync(x => x.Id == 1);
            return (p.LastMarkPrice, p.UnrealizedPnl, p.UpdatedUtc);
        }
        public void Register(IServiceCollection services)
        {
            services.AddSingleton(EveryModuleGranted.Create());
            services.AddSingleton(SimulatorRunAccessTests.OneRun.Create());
            services.AddSingleton<IPaperTradingService>(Paper);
            services.AddFake<ISimulationRunner>();
            services.AddSingleton<BacktestProcessRegistry>();
            foreach (var type in typeof(GetSimulationRunUseCase).Assembly.GetTypes()
                         .Where(t => (t.Namespace is null || t.Namespace == typeof(GetSimulationRunUseCase).Namespace) && t.IsClass && !t.IsAbstract && t.Name.EndsWith("UseCase", StringComparison.Ordinal)))
                services.AddTransient(type);
        }
        public void Dispose() => Db.Dispose();
    }
}
