using System.Reflection;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;
using static AlgoTrading.UnitTests.IraActionsTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A confirmed action carried out by the services the console's own buttons use, as "owner via Ira": the start through
/// <see cref="StrategyStartService"/> and every check it makes, the stop through the Stop button's pipeline, the
/// square-off through the manual book's reduce-only close, the switch through AI → Agents' store. Real runner
/// processes for the start and the stop, as in <see cref="StrategyStartTests"/>.
/// </summary>
public sealed class IraDeskHandsTests
{
    private static readonly IraOwner Admin = new(RunnerDesk.AdminId, "admin");

    private static IraDeskHands Hands(
        RunnerDesk desk, TradingDbContext db, IMarketSessionService? sessions = null, bool granted = true, IPaperTradingService? paper = null) => new(
        db, desk.RunControl(db), desk.Registry, desk.Starts(db), paper ?? RecapClockTests.Inert<IPaperTradingService>.Create(), new AiSettingsStore(db),
        new Grants(granted), desk.Limits, RecapClockTests.Inert<IDerivativesInstrumentService>.Create(), desk.Memory,
        RecapClockTests.Inert<ISystemNotifier>.Create(), sessions);

    private static IraStartRun Ghost(string underlying = "NIFTY", int lots = 2) =>
        new(RunnerDesk.GhostId, "Ghost", underlying, lots, RunnerDesk.AdminId, "admin");

    [Fact]
    public async Task Starting_a_run_goes_through_every_check_of_the_start_button()
    {
        using var desk = new RunnerDesk();
        await using var db = desk.Db();

        // The market's hours: a day with no session refuses it, and leaves no row.
        var closed = await Hands(desk, db, new NoSession()).StartRunAsync(Admin, Ghost(), CancellationToken.None);
        Assert.False(closed.Done);
        Assert.StartsWith("Not done: NSE is closed today", closed.Message);
        Assert.Contains("a live run can start only on a trading day", closed.Message);
        Assert.Empty(desk.Runs());

        // The module grant, as [RequireModule] checks it on the Start button's endpoint.
        var ungranted = await Hands(desk, db, granted: false).StartRunAsync(Admin, Ghost(), CancellationToken.None);
        Assert.Equal("Not done: your account does not have access to the strategies module.", ungranted.Message);

        // The host's memory.
        desk.AvailableMemoryBytes = 900L * 1024 * 1024;
        var starved = await Hands(desk, db).StartRunAsync(Admin, Ghost(), CancellationToken.None);
        Assert.Contains("900 MB", starved.Message);
        desk.AvailableMemoryBytes = null;

        var started = await Hands(desk, db).StartRunAsync(Admin, Ghost(), CancellationToken.None);
        Assert.True(started.Done, started.Message);
        var run = Assert.Single(desk.Runs());
        Assert.Equal(("admin via Ira", RunnerDesk.AdminId, RunnerDesk.AdminId, "Running", "Ghost"),
            (run.StartedByName, run.UserId, run.StartedByUserId!.Value, run.Status, run.StrategyName));
        Assert.StartsWith($"Done: run {run.Id} started at ", started.Message);
        Assert.EndsWith(": Ghost on NIFTY, 2 lots, in admin's account.", started.Message);

        // The duplicate check, naming the owner's account, not "admin via Ira"'s.
        var twice = await Hands(desk, db).StartRunAsync(Admin, Ghost(), CancellationToken.None);
        Assert.Equal("Not done: Ghost is already running on NIFTY in admin's account — stop that run or pick another underlying.", twice.Message);

        // The run cap.
        desk.MaxRunsPerAccount = 1;
        var capped = await Hands(desk, db).StartRunAsync(Admin, Ghost("BANKNIFTY"), CancellationToken.None);
        Assert.Equal("Not done: Concurrent strategy limit reached (1).", capped.Message);
        Assert.Single(desk.Runs());
    }

    [Fact]
    public async Task A_start_in_another_account_runs_there_and_records_who_asked()
    {
        using var desk = new RunnerDesk();
        await using var db = desk.Db();

        var started = await Hands(desk, db).StartRunAsync(Admin, Ghost() with { AccountUserId = RunnerDesk.TraderId, Account = "coderforchange" }, CancellationToken.None);

        Assert.True(started.Done, started.Message);
        var run = Assert.Single(desk.Runs());
        Assert.Equal((RunnerDesk.TraderId, RunnerDesk.AdminId, "admin via Ira"), (run.UserId, run.StartedByUserId!.Value, run.StartedByName));
    }

    [Fact]
    public async Task A_stop_takes_the_stop_buttons_path_as_the_owner_via_ira()
    {
        using var desk = new RunnerDesk();
        await using var db = desk.Db();
        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await desk.Start("NIFTY"));
        long runId = Assert.Single(desk.Runs()).Id;
        var target = new IraStopRun(runId, "Ghost", "NIFTY", RunnerDesk.AdminId, "admin");

        var stopped = await Hands(desk, db).StopRunAsync(Admin, target, CancellationToken.None);
        var again = await Hands(desk, db).StopRunAsync(Admin, target, CancellationToken.None);

        Assert.True(stopped.Done, stopped.Message);
        Assert.Matches($@"^Done: run {runId} \(Ghost, NIFTY, admin\) stopped at \d\d:\d\d IST\.$", stopped.Message);
        Assert.Equal($"Not done: run {runId} is not running any more, so there was nothing to stop.", again.Message);
        await using var read = desk.Db();
        Assert.Equal("Stopped", (await read.SimulationRuns.SingleAsync()).Status);
        var signal = await read.SimulationSignals.SingleAsync(s => s.SignalType == StrategyRunControl.RunStoppedSignalType);
        Assert.Contains("\"by\":\"admin via Ira\"", signal.MetadataJson);
        Assert.Contains("Stopped by admin via Ira", signal.MetadataJson);
        Assert.Equal(0, desk.Registry.Count);
    }

    [Fact]
    public async Task A_square_off_closes_only_the_positions_shown_that_are_still_open_on_the_books_path()
    {
        string name = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;
        await using (var seed = NewDb(name))
        {
            SeedUsers(seed);
            SeedDesk(seed, now);
        }

        await using var db = NewDb(name);
        var paper = ClosingPaper.Create(() => NewDb(name));
        var hands = new IraDeskHands(db, null!, null!, null!, paper, new AiSettingsStore(db), new Grants(), null!, null!, null!, null!);

        var done = await hands.SquareOffAsync(new IraOwner(IraActionsTestKit.Admin, "upendra"),
            new IraSquareOff(550, IraActionsTestKit.Admin, "upendra", [77, 79], All: false), CancellationToken.None);

        var call = Assert.Single(((ClosingPaper)(object)paper).Calls);
        Assert.Equal((550L, "Squared off by upendra via Ira", "upendra via Ira"), (call.RunId, call.Reason, call.By));
        Assert.Equal(new long[] { 77 }, call.Ids);
        Assert.True(done.Done, done.Message);
        Assert.Matches(@"^Done: squared off 1 position in upendra's manual book at \d\d:\d\d IST \(1 had closed already\): NSE:NIFTY26OCT25100CE; realized ₹1,500 before charges\.$", done.Message);

        var nothing = await hands.SquareOffAsync(new IraOwner(IraActionsTestKit.Admin, "upendra"),
            new IraSquareOff(550, IraActionsTestKit.Admin, "upendra", [77], All: false), CancellationToken.None);
        Assert.Equal("Not done: none of those positions is open any more: nothing to square off.", nothing.Message);
    }

    [Fact]
    public async Task A_switch_uses_the_agents_page_store_and_says_who_switched_it()
    {
        using var desk = new RunnerDesk();
        await using var db = desk.Db();
        var target = new IraAgentSwitch(AiCatalog.NewsAnalyst, "News Analyst", true, "Owner asked on Telegram");

        var done = await Hands(desk, db).SwitchAgentAsync(Admin, target, CancellationToken.None);
        var again = await Hands(desk, db).SwitchAgentAsync(Admin, target, CancellationToken.None);

        Assert.True(done.Done, done.Message);
        var state = (await new AiSettingsStore(desk.Db()).LoadAsync()).Agent(AiCatalog.NewsAnalyst)!;
        Assert.Equal((true, "admin via Ira", "Owner asked on Telegram"), (state.Enabled, state.UpdatedBy, state.Reason));
        Assert.Equal("Not done: the News Analyst was already on.", again.Message);
    }

    /// <summary>
    /// The paper desk's reduce-only close, recorded: each call's book, positions, reason and actor, and the positions
    /// closed in the database at a fill ₹10 above the entry, as a squared-off long would be.
    /// </summary>
    public class ClosingPaper : DispatchProxy
    {
        public List<(long RunId, long[] Ids, string Reason, string By)> Calls { get; } = [];

        private Func<TradingDbContext> _db = null!;

        public static IPaperTradingService Create(Func<TradingDbContext> db)
        {
            var proxy = DispatchProxy.Create<IPaperTradingService, ClosingPaper>();
            ((ClosingPaper)(object)proxy)._db = db;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IPaperTradingService.ClosePositionsAsync)) throw new NotSupportedException(targetMethod.Name);
            long runId = (long)args![0]!;
            long[] ids = ((IEnumerable<long>)args[1]!).ToArray();
            Calls.Add((runId, ids, (string)args[2]!, (string)args[3]!));
            using var db = _db();
            var open = db.PaperPositions.Where(p => p.SimulationRunId == runId && ids.Contains(p.Id) && p.Status == "Open").ToList();
            foreach (var p in open)
            {
                p.Status = "Closed";
                p.RealizedPnl = 10m * p.Quantity;
                p.Quantity = 0;
            }

            db.SaveChanges();
            return Task.FromResult(open.Count);
        }
    }
}
