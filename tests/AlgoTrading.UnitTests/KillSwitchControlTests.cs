using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace AlgoTrading.UnitTests;

public sealed class KillSwitchControlTests
{
    private static (KillSwitchControl Control, IRiskManagementService Risk, IPaperTradingService Paper) Services(RunnerDesk desk, TradingDbContext db)
    {
        // Each desk has its own database; do not share the production order-risk
        // cache with unrelated test databases running in parallel.
        var risk = DispatchProxy.Create<IRiskManagementService, DatabaseRisk>();
        ((DatabaseRisk)(object)risk).Db = db;
        var paper = RecapClockTests.Inert<IPaperTradingService>.Create();
        return (new KillSwitchControl(risk, paper, desk.RunControl(db), desk.Registry, db), risk, paper);
    }

    public class DatabaseRisk : DispatchProxy
    {
        public TradingDbContext Db { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IRiskManagementService.ActivateKillSwitchAsync) => Save(true, (string?)args![0], (string?)args[1], (CancellationToken)args[2]!),
            nameof(IRiskManagementService.DeactivateKillSwitchAsync) => Save(false, (string?)args![0], (string?)args[1], (CancellationToken)args[2]!),
            nameof(IRiskManagementService.GetKillSwitchStateAsync) => Read(),
            _ => throw new NotSupportedException(method.Name),
        };
        private async Task Save(bool active, string? actor, string? reason, CancellationToken ct)
        {
            var row = await Db.SystemSettings.SingleOrDefaultAsync(x => x.Key == SystemSettingKeys.KillSwitchActive, ct);
            if (row is null) { row = new SystemSetting { Key = SystemSettingKeys.KillSwitchActive }; Db.SystemSettings.Add(row); }
            row.Value = active ? "true" : "false";
            row.UpdatedBy = actor; row.Reason = reason; row.UpdatedUtc = DateTime.UtcNow;
            Db.RiskEvents.Add(new RiskEvent { Kind = active ? "KillSwitchActivated" : "KillSwitchDeactivated", ActorName = actor ?? "system" });
            await Db.SaveChangesAsync(ct);
        }
        private async Task<KillSwitchState> Read()
        {
            var row = await Db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == SystemSettingKeys.KillSwitchActive);
            return new KillSwitchState { IsActive = row?.Value == "true", UpdatedBy = row?.UpdatedBy, Reason = row?.Reason, UpdatedUtc = row?.UpdatedUtc };
        }
    }

    [Fact]
    public async Task Activation_stops_runners_in_every_account_and_blocks_new_starts_until_release()
    {
        using var desk = new RunnerDesk();
        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));
        Assert.IsType<OkObjectResult>(await desk.Start("BANKNIFTY", ownerUserId: RunnerDesk.TraderId));
        var processes = desk.Registry.List().Select(x => x.ProcessId).ToArray();
        await using var db = desk.Db();
        var (control, _, _) = Services(desk, db);
        var result = await control.ActivateAsync("admin", "Emergency stop", CancellationToken.None);
        Assert.True(result.Complete);
        Assert.Equal(2, result.StoppedRuns);
        Assert.Empty(desk.Registry.List());
        Assert.All(desk.Runs(), r => Assert.Equal("Stopped", r.Status));
        Assert.All(processes, pid =>
        {
            try { using var process = System.Diagnostics.Process.GetProcessById(pid); Assert.True(process.HasExited); }
            catch (ArgumentException) { /* The operating system has already reaped it. */ }
        });
        Assert.Equal(2, await db.SimulationSignals.CountAsync(x => x.SignalType == StrategyRunControl.RunStoppedSignalType));
        var blocked = Assert.IsType<ConflictObjectResult>(await desk.Start("NIFTY"));
        Assert.Contains("kill switch", System.Text.Json.JsonSerializer.Serialize(blocked.Value));
        await control.DeactivateAsync("admin", "Resolved", CancellationToken.None);
        Assert.Empty(desk.Registry.List()); // Releasing allows a new start; it never resumes old runs.
        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));
    }

    [Fact]
    public async Task A_start_waiting_behind_activation_cannot_escape_the_global_stop()
    {
        using var desk = new RunnerDesk();
        await using var db = desk.Db();
        var (control, _, _) = Services(desk, db);
        await desk.Registry.StartGate.WaitAsync();
        var activate = control.ActivateAsync("admin", "Stop", CancellationToken.None);
        var start = desk.Start("NIFTY");
        desk.Registry.StartGate.Release();
        Assert.True((await activate).Complete);
        Assert.IsType<ConflictObjectResult>(await start);
        Assert.Empty(desk.Registry.List());
        Assert.Empty(desk.Runs());
    }

    [Fact]
    public async Task Restart_finishes_a_persisted_stop_without_overwriting_the_operator()
    {
        using var desk = new RunnerDesk();
        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));
        await using var db = desk.Db();
        var (control, risk, _) = Services(desk, db);
        await risk.ActivateKillSwitchAsync("operator", "Interrupted request", CancellationToken.None);
        var before = await risk.GetKillSwitchStateAsync(CancellationToken.None);
        Assert.True((await control.ResumeAsync(CancellationToken.None))!.Complete);
        Assert.Empty(desk.Registry.List());
        Assert.Equal("Stopped", desk.Runs().Single().Status);
        var after = await risk.GetKillSwitchStateAsync(CancellationToken.None);
        Assert.True(after.IsActive);
        Assert.Equal(before.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal("operator", after.UpdatedBy);
        Assert.Single(await db.RiskEvents.ToListAsync());
    }

    [Fact]
    public async Task Disconnect_after_persisting_the_switch_does_not_cancel_the_stop()
    {
        using var desk = new RunnerDesk();
        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));
        await using var db = desk.Db();
        var (_, risk, paper) = Services(desk, db);
        using var disconnected = new CancellationTokenSource();
        var wrapped = DispatchProxy.Create<IRiskManagementService, CancelAfterPersist>();
        var proxy = (CancelAfterPersist)(object)wrapped;
        proxy.Inner = risk;
        proxy.Disconnected = disconnected;
        var control = new KillSwitchControl(wrapped, paper, desk.RunControl(db), desk.Registry, db);
        Assert.True((await control.ActivateAsync("admin", "Stop", disconnected.Token)).Complete);
        Assert.True(disconnected.IsCancellationRequested);
        Assert.Empty(desk.Registry.List());
        Assert.Equal("Stopped", desk.Runs().Single().Status);
    }

    public class CancelAfterPersist : DispatchProxy
    {
        public IRiskManagementService Inner { get; set; } = null!;
        public CancellationTokenSource Disconnected { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = targetMethod!.Invoke(Inner, args);
            return targetMethod.Name == nameof(IRiskManagementService.ActivateKillSwitchAsync)
                ? CancelWhenSaved((Task)result!) : result;
        }
        private async Task CancelWhenSaved(Task saved) { await saved; Disconnected.Cancel(); }
    }

    [Fact]
    public async Task An_unclosed_position_returns_failure_while_the_kill_switch_stays_on()
    {
        using var desk = new RunnerDesk();
        long book = desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Running", ManualOrdersController.BookStrategyName);
        await using var db = desk.Db();
        db.PaperPositions.Add(new PaperPosition { SimulationRunId = book, Status = "Open", Symbol = "TEST" });
        await db.SaveChangesAsync();
        var (control, risk, paper) = Services(desk, db); // The inert paper service intentionally leaves this position open.
        var endpoint = new RiskController(risk, paper, RecapClockTests.Inert<ISystemNotifier>.Create())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var response = Assert.IsType<ObjectResult>(await endpoint.ActivateKillSwitch("Stop", control, CancellationToken.None));
        Assert.Equal(503, response.StatusCode);
        Assert.True((await risk.GetKillSwitchStateAsync(CancellationToken.None)).IsActive);
        Assert.Equal("Running", desk.Runs().Single().Status); // A manual book is not a strategy process.
    }
}
