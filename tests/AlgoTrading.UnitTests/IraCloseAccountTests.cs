using System.Text.Json;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;
using static AlgoTrading.UnitTests.IraActionsTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Ira's account close (owner, 12 Oct 2026: "If I tell Ira to close all trades of a particular account, everything
/// should close together"): code lists what is open in the account when it proposes, and one PIN stops every run and
/// squares off every manual position, at most four at a time, each item ending on its own. Scripted hands stand in
/// for the desk; the stop's own path with real runner processes is in <see cref="IraDeskHandsTests"/>.
/// </summary>
public sealed class IraCloseAccountTests
{
    private const long CoderX = 20;
    private const long DeskService = 21;
    private const long OldTimer = 22;

    private readonly string _db = Guid.NewGuid().ToString("N");
    private readonly MovableTime _time = new(Monday1030);
    private readonly FakeHands _hands = new() { DayNet = -12_880m };
    private readonly IraPinGuard _guard;
    private readonly AiSettings _settings = Settings();

    public IraCloseAccountTests()
    {
        FastPins();
        _guard = new IraPinGuard(_time);
        using var db = NewDb(_db);
        SeedUsers(db);
        SeedDesk(db, _time.Now);
        db.AppUsers.AddRange(
            new AppUser { Id = CoderX, UserName = "coder-x", Role = UserRoles.Trader, IsActive = true },
            new AppUser { Id = DeskService, UserName = "desk-svc", Role = UserRoles.Service, IsActive = true },
            new AppUser { Id = OldTimer, UserName = "oldtimer", Role = UserRoles.Trader, IsActive = false });

        // Beside upendra's two running runs (512 with leg 90, 540 with none): one being stopped, with a leg; a recap
        // and the alerts runner, which are not trading; and a quote that marks manual position 77.
        db.SimulationRuns.AddRange(
            Run(572, "Fulcrum", "SENSEX", "Stopping"),
            Run(573, "GhostTangentCrossings", "NIFTY", "Running", """{"underlying":"NIFTY","session":"recap"}"""),
            Run(574, "LogicEngine", "NIFTY", "Running", """{"underlying":"NIFTY","role":"alerts"}"""));
        db.PaperPositions.Add(new PaperPosition
        {
            Id = 91, SimulationRunId = 572, StrategyName = "Fulcrum", GroupId = "G91", Symbol = "BSE:SENSEX26OCT82000PE", Direction = "Sell",
            Quantity = 2, AveragePrice = 210m, Status = "Open", OpenedUtc = _time.Now.AddHours(-1), UnrealizedPnl = 200m,
        });
        db.SaveChanges();
        db.PaperPositions.Single(p => p.Id == 90).UnrealizedPnl = -1_240.40m;
        db.PaperPositions.Single(p => p.Id == 78).UnrealizedPnl = -150m;
        // As the desk stores a side.
        db.PaperPositions.Single(p => p.Id == 77).Direction = "LONG";
        db.PaperPositions.Single(p => p.Id == 78).Direction = "SHORT";
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = "NSE:NIFTY26OCT25100CE", LastTradedPrice = 120.60m, UpdatedUtc = _time.Now, RawPayload = "{}", SourceKey = "test" });
        // The instrument master knows 77's contract (its expiry is the master's); 78's is read from its symbol.
        db.Instruments.Add(new Instrument
        {
            Symbol = "NSE:NIFTY26OCT25100CE", Exchange = "NSE", Segment = "FO", InstrumentType = "CE", OptionType = "CE", Underlying = "NIFTY",
            StrikePrice = 25100m, ExpiryDate = new DateOnly(2026, 10, 27), IsEnabled = true,
        });
        db.SaveChanges();

        SimulationRun Run(long id, string strategy, string underlying, string status, string? parameters = null) => new()
        {
            Id = id, UserId = Admin, Mode = StrategyRunControl.LivePaperMode, Status = status, StrategyName = strategy,
            Symbol = Infrastructure.Services.UnderlyingCatalog.SpotSymbolFor(underlying),
            ParametersJson = parameters ?? $$"""{"underlying":"{{underlying}}"}""", CreatedUtc = _time.Now.AddHours(-1), StartedUtc = _time.Now.AddHours(-1),
        };
    }

    private static IraOwner Upendra => new(Admin, "upendra");

    private IraActions Actions(bool together = true) =>
        new(NewDb(_db), _hands, _guard, new AiTestKit.Monitor(_settings), new ListLogger<IraActions>(), _time, together ? Scopes(_hands) : null);

    private IraActionResolver Resolver()
    {
        var db = NewDb(_db);
        var lots = new PositionGreeksTests.FixedLots(1);
        return new IraActionResolver(db, null!, Registry(), new AiSettingsStore(db), null, _time, new RunPnl(db, lots, new RunCharges(db, lots)));
    }

    private ProposeCloseAccountTool Tool() => new(Actions(), Resolver(), _time);

    private static AiToolCaller Caller(string source = "console", long user = Admin, string agent = AiCatalog.DeskAssistant) =>
        new(agent, source, user == Admin ? "upendra" : "someone", user, "c-close", CallId: 77);

    private async Task SetPin()
    {
        var (change, _, _) = await Actions().SetPinAsync(Upendra, Pin, null, requireCurrent: false, "the console", CancellationToken.None);
        Assert.Equal(IraPinChange.Set, change);
    }

    private async Task<(AiAction Action, JsonElement Data)> Proposed(string account = "my account", AiToolCaller? caller = null)
    {
        var output = await Tool().RunAsync(AiToolArgs.Parse(JsonSerializer.Serialize(new { account })).For(caller ?? Caller()), CancellationToken.None);
        var data = JsonSerializer.SerializeToElement(output.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await using var db = NewDb(_db);
        return (await db.AiActions.AsNoTracking().SingleAsync(a => a.Id == data.GetProperty("actionId").GetInt64()), data);
    }

    private Task<IraConfirmResult> Confirm(AiAction action, bool together = true) =>
        Actions(together).ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);

    private async Task SetStatus(long runId, string status)
    {
        await using var db = NewDb(_db);
        (await db.SimulationRuns.SingleAsync(r => r.Id == runId)).Status = status;
        await db.SaveChangesAsync();
    }

    private async Task ClosePosition(long positionId)
    {
        await using var db = NewDb(_db);
        (await db.PaperPositions.SingleAsync(p => p.Id == positionId)).Status = "Closed";
        await db.SaveChangesAsync();
    }

    private List<long> Stopped() => _hands.Calls.Where(c => c.Kind == AiActionKind.StopRun).Select(c => ((IraStopRun)c.Target).RunId).Order().ToList();

    // ---------- the proposal ----------

    [Fact]
    public async Task The_proposal_lists_every_live_run_and_manual_position_of_the_account_with_what_it_stands_at()
    {
        await SetPin();

        var (action, data) = await Proposed("my account");

        Assert.Equal((AiActionKind.CloseAccount, AiActionStatus.Pending), (action.Kind, action.Status));
        var target = JsonSerializer.Deserialize<IraCloseAccount>(action.ParamsJson, IraActionResolver.Json)!;
        Assert.Equal((Admin, "upendra", (long?)550), (target.AccountUserId, target.Account, target.BookRunId));
        // Running or Stopping, upendra's own: not his stopped run, the recap, the alerts runner or coderforchange's run.
        Assert.Equal(
            new[] { (512L, "GhostTangentCrossings", "NIFTY", 1, (decimal?)-1_240.40m), (540L, "Fulcrum", "BANKNIFTY", 0, 0m), (572L, "Fulcrum", "SENSEX", 1, 200m) },
            target.Runs.Select(r => (r.RunId, r.Strategy, r.Underlying, r.OpenLegs, r.Net)));
        // The manual book's open positions: 77 marked at its quote ((120.60 − 120.50) × 150 lots × 1), 78 at its stored mark.
        Assert.Equal(
            new[] { (77L, "BUY", 150, (decimal?)15m), (78L, "SELL", 75, -150m) },
            target.Positions.Select(p => (p.PositionId, p.Side, p.Lots, p.Unrealized)));
        Assert.Equal(
            "Close everything in upendra's account: stop 3 runs (Ghost Tangent Crossings NIFTY run 512, Fulcrum BANKNIFTY run 540 and " +
            "Fulcrum SENSEX run 572) and square off their 2 open positions, and square off 2 manual positions " +
            $"(NIFTY 25100 CE · 27 Oct ×150 and {target.Positions[1].Contract} ×75). " +
            "Net now: runs −₹1,040 after charges, manual positions −₹135 unrealized.",
            action.Summary);
        Assert.Equal("NIFTY 25100 CE · 27 Oct", target.Positions[0].Contract);
        Assert.StartsWith("NIFTY 25000 PE · ", target.Positions[1].Contract);

        // The confirm card's full list, in the tool's answer.
        var items = data.GetProperty("items");
        Assert.Equal((3, 2, 2, 0), (items.GetProperty("runCount").GetInt32(), items.GetProperty("positionCount").GetInt32(),
            items.GetProperty("openLegs").GetInt32(), items.GetProperty("notListed").GetInt32()));
        Assert.Equal(new[] { 512L, 540, 572 }, items.GetProperty("runs").EnumerateArray().Select(r => r.GetProperty("runId").GetInt64()));
        Assert.Equal("Ghost Tangent Crossings", items.GetProperty("runs")[0].GetProperty("strategy").GetString());
        Assert.Equal(new[] { 77L, 78 }, items.GetProperty("positions").EnumerateArray().Select(p => p.GetProperty("positionId").GetInt64()));
        // Proposed is not done.
        Assert.Empty(_hands.Calls);
    }

    [Fact]
    public async Task Nothing_open_in_the_account_is_an_error_the_model_reads_and_nothing_is_stored()
    {
        await SetPin();

        var ravi = await Assert.ThrowsAsync<AiToolArgumentException>(() => Proposed("ravi"));
        await SetStatus(530, "Stopped");
        var coder = await Assert.ThrowsAsync<AiToolArgumentException>(() => Proposed("coderforchange"));

        Assert.Equal("Nothing is open in ravi's account.", ravi.Message);
        Assert.Equal("Nothing is open in coderforchange's account.", coder.Message);
        await using var db = NewDb(_db);
        Assert.Empty(db.AiActions);
    }

    [Fact]
    public async Task A_long_list_is_capped_in_the_summary_and_listed_in_full_for_the_card()
    {
        await SetPin();
        await using (var db = NewDb(_db))
        {
            for (long id = 600; id < 609; id++)
            {
                db.SimulationRuns.Add(new SimulationRun
                {
                    Id = id, UserId = Admin, Mode = StrategyRunControl.LivePaperMode, Status = "Running", StrategyName = "Fulcrum",
                    Symbol = "NSE:NIFTY50-INDEX", ParametersJson = """{"underlying":"NIFTY"}""", StartedUtc = _time.Now, CreatedUtc = _time.Now,
                });
            }

            await db.SaveChangesAsync();
        }

        var (action, data) = await Proposed("upendra");

        Assert.Contains("stop 12 runs (Ghost Tangent Crossings NIFTY run 512, Fulcrum BANKNIFTY run 540, Fulcrum SENSEX run 572, " +
                        "Fulcrum NIFTY run 600, Fulcrum NIFTY run 601, Fulcrum NIFTY run 602 and 6 more)", action.Summary);
        Assert.True(action.Summary.Length < 1000, action.Summary);
        Assert.Equal(12, JsonSerializer.Deserialize<IraCloseAccount>(action.ParamsJson, IraActionResolver.Json)!.Runs.Count);
        Assert.Equal(12, data.GetProperty("items").GetProperty("runs").GetArrayLength());
        // On Telegram the bot sends the same capped sentence.
        Assert.StartsWith($"Action #{action.Id}: Close everything in upendra's account: stop 12 runs (", IraActionText.AskPin(action));
    }

    [Fact]
    public async Task A_very_large_account_still_fits_the_tool_answer_and_the_card_says_how_many_are_not_listed()
    {
        await SetPin();
        await using (var db = NewDb(_db))
        {
            for (long id = 1000; id < 1100; id++)
            {
                db.SimulationRuns.Add(new SimulationRun
                {
                    Id = id, UserId = Admin, Mode = StrategyRunControl.LivePaperMode, Status = "Running", StrategyName = "GhostTangentCrossings",
                    Symbol = "NSE:NIFTYBANK-INDEX", ParametersJson = """{"underlying":"BANKNIFTY"}""", StartedUtc = _time.Now, CreatedUtc = _time.Now,
                });
            }

            await db.SaveChangesAsync();
        }

        var (action, data) = await Proposed();

        var items = data.GetProperty("items");
        Assert.Equal((103, 2, IraActionResolver.MaxCardItems, 0, 25), (items.GetProperty("runCount").GetInt32(), items.GetProperty("positionCount").GetInt32(),
            items.GetProperty("runs").GetArrayLength(), items.GetProperty("positions").GetArrayLength(), items.GetProperty("notListed").GetInt32()));
        // The gateway refuses an answer over Ai:MaxToolResultChars, so the card's list must stay well under it.
        Assert.InRange(JsonSerializer.Serialize(data).Length, 1, _settings.MaxToolResultChars - 4_000);
        Assert.Equal(103, JsonSerializer.Deserialize<IraCloseAccount>(action.ParamsJson, IraActionResolver.Json)!.Runs.Count);
    }

    // ---------- naming the account ----------

    [Theory]
    [InlineData("my account", "upendra")]
    [InlineData("Mine", "upendra")]
    [InlineData("MY OWN ACCOUNT", "upendra")]
    [InlineData("Upendra", "upendra")]
    [InlineData("upendra's account", "upendra")]
    [InlineData("upendra’s", "upendra")]
    [InlineData("CoderForChange", "coderforchange")]
    [InlineData("@coderforchange", "coderforchange")]
    [InlineData("coderforchange account", "coderforchange")]
    [InlineData("forchange", "coderforchange")]
    [InlineData("coder x", "coder-x")]
    [InlineData("oldtimer", "oldtimer")]
    public async Task An_account_is_named_by_its_user_name_in_any_case_or_my_account_for_the_owners_own(string asked, string expected)
    {
        var (_, name) = await Resolver().ManagedAccountAsync(asked, Upendra, CancellationToken.None);

        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("code", "Two accounts match code: coder-x, coderforchange. Which one?")]
    [InlineData("desk-svc", "There is no account named desk-svc. The accounts: coder-x, coderforchange, ravi, upendra.")]
    [InlineData("old", "There is no account named old. The accounts: coder-x, coderforchange, ravi, upendra.")]
    [InlineData("mallory", "There is no account named mallory. The accounts: coder-x, coderforchange, ravi, upendra.")]
    [InlineData("  ", "Name the account: its user name (as get_runs shows it), or \"my account\" for your own.")]
    public async Task An_ambiguous_unknown_or_service_account_is_an_error_naming_the_accounts(string asked, string expected)
    {
        var error = await Assert.ThrowsAsync<AiToolArgumentException>(() => Resolver().ManagedAccountAsync(asked, Upendra, CancellationToken.None));

        Assert.Equal(expected, error.Message);
    }

    // ---------- one PIN closes it all ----------

    [Fact]
    public async Task One_pin_stops_every_run_and_squares_off_every_manual_position_together()
    {
        await SetPin();
        var (action, _) = await Proposed();

        var result = await Confirm(action);

        Assert.Equal(IraConfirmKind.Executed, result.Kind);
        Assert.Equal(
            "Done: upendra's account closed at 10:30 IST: 3 runs stopped, 3 strategy positions and 2 manual positions squared off " +
            "(the manual ones realized ₹0 before charges). Day net −₹12,880 after charges.",
            result.Message);
        Assert.Equal(new long[] { 512, 540, 572 }, Stopped());
        Assert.All(_hands.Calls, c => Assert.Equal(Upendra, c.Owner));
        Assert.All(_hands.Calls.Where(c => c.Kind == AiActionKind.StopRun).Select(c => (IraStopRun)c.Target),
            s => Assert.Equal((Admin, "upendra"), (s.AccountUserId, s.Account)));
        var squareOff = (IraSquareOff)Assert.Single(_hands.Calls, c => c.Kind == AiActionKind.SquareOff).Target;
        Assert.Equal((550L, true), (squareOff.BookRunId, squareOff.All));
        Assert.Equal(new long[] { 77, 78 }, squareOff.PositionIds);
        await using var db = NewDb(_db);
        var row = await db.AiActions.SingleAsync();
        Assert.Equal(AiActionStatus.Executed, row.Status);
        Assert.Equal(result.Message, IraActions.ResultMessage(row));
    }

    [Fact]
    public async Task What_closed_before_the_pin_or_while_it_ran_is_skipped_and_reported()
    {
        await SetPin();
        var (action, _) = await Proposed();
        await SetStatus(540, "Stopped");
        await ClosePosition(78);
        // Run 572 finishes its own stop between the check and the hands.
        _hands.Respond = (kind, target) => target is IraStopRun { RunId: 572 }
            ? IraOutcome.AlreadyClosed("run 572 is not running any more, so there was nothing to stop")
            : null;

        var result = await Confirm(action);

        Assert.Equal(new long[] { 512, 572 }, Stopped());
        Assert.Equal(new long[] { 77 }, ((IraSquareOff)_hands.Calls.Single(c => c.Kind == AiActionKind.SquareOff).Target).PositionIds);
        Assert.Equal(IraConfirmKind.Executed, result.Kind);
        Assert.Matches(
            @"^Done: upendra's account closed at 10:30 IST: 1 run stopped, 1 strategy position and 1 manual position squared off \(the manual ones realized ₹0 before charges\); " +
            @"3 had closed already: run 540, run 572 and NIFTY 25000 PE[^×]* ×75\. Day net −₹12,880 after charges\.$",
            result.Message);
    }

    [Fact]
    public async Task Everything_closed_before_the_pin_does_nothing_and_says_so()
    {
        await SetPin();
        var (action, _) = await Proposed("coderforchange");
        await SetStatus(530, "Stopped");

        var result = await Confirm(action);

        Assert.Empty(_hands.Calls);
        Assert.Equal(IraConfirmKind.Failed, result.Kind);
        Assert.Equal("Not done: everything in coderforchange's account had closed already (run 530): nothing was left to close. Day net −₹12,880 after charges.",
            result.Message);
    }

    [Fact]
    public async Task One_failing_item_does_not_stop_the_others_and_is_reported()
    {
        await SetPin();
        var (action, _) = await Proposed();
        _hands.Respond = (kind, target) => target switch
        {
            IraStopRun { RunId: 512 } => throw new InvalidOperationException("runner gone mad"),
            IraStopRun { RunId: 540 } => IraOutcome.Refused("the runner did not answer"),
            _ => null,
        };

        var result = await Confirm(action);

        // Every item was tried, the failures among them.
        Assert.Equal(new long[] { 512, 540, 572 }, Stopped());
        Assert.Single(_hands.Calls, c => c.Kind == AiActionKind.SquareOff);
        Assert.Equal(IraConfirmKind.Failed, result.Kind);
        Assert.Equal(
            "Partly done: upendra's account at 10:30 IST: 1 run stopped, 1 strategy position and 2 manual positions squared off " +
            "(the manual ones realized ₹0 before charges); 2 failed: run 512 (it failed on the server (InvalidOperationException); see api.log) and " +
            "run 540 (the runner did not answer). Day net −₹12,880 after charges.",
            result.Message);
        await using var db = NewDb(_db);
        Assert.Equal(AiActionStatus.Failed, (await db.AiActions.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_square_off_the_desk_refuses_leaves_the_positions_failed_with_its_words_and_the_runs_stopped()
    {
        await SetPin();
        var (action, _) = await Proposed();
        _hands.Respond = (kind, _) => kind == AiActionKind.SquareOff ? IraOutcome.Refused("no quote and no mark for NIFTY26OCT25100CE") : null;

        var result = await Confirm(action);

        Assert.Equal(new long[] { 512, 540, 572 }, Stopped());
        Assert.Matches(
            @"^Partly done: upendra's account at 10:30 IST: 3 runs stopped, 3 strategy positions squared off; 2 failed: " +
            @"NIFTY 25100 CE[^×]* ×150 \(no quote and no mark for NIFTY26OCT25100CE\) and NIFTY 25000 PE[^×]* ×75 \(no quote and no mark for NIFTY26OCT25100CE\)\.",
            result.Message);
    }

    // ---------- at most four at once ----------

    [Fact]
    public async Task Items_close_four_at_a_time_and_never_more()
    {
        await SetPin();
        await using (var db = NewDb(_db))
        {
            for (long id = 600; id < 609; id++)
            {
                db.SimulationRuns.Add(new SimulationRun
                {
                    Id = id, UserId = Admin, Mode = StrategyRunControl.LivePaperMode, Status = "Running", StrategyName = "Fulcrum",
                    Symbol = "NSE:NIFTY50-INDEX", ParametersJson = """{"underlying":"NIFTY"}""", StartedUtc = _time.Now, CreatedUtc = _time.Now,
                });
            }

            await db.SaveChangesAsync();
        }

        var (action, _) = await Proposed();
        var release = new TaskCompletionSource();
        _hands.Hold = release.Task;

        var confirming = Task.Run(() => Confirm(action));
        await Until(() => _hands.InFlight == IraAccountCloser.Parallelism);
        // Held there: a fifth does not start while four are in hand.
        await Task.Delay(300);
        Assert.Equal((4, 4), (_hands.InFlight, _hands.Calls.Count));
        release.SetResult();
        var result = await confirming.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(IraConfirmKind.Executed, result.Kind);
        // 12 runs and the manual square-off: thirteen items, four at a time.
        Assert.Equal(13, _hands.Calls.Count);
        Assert.Equal(IraAccountCloser.Parallelism, _hands.MostAtOnce);
    }

    [Fact]
    public async Task Without_scopes_of_their_own_the_items_close_one_after_another()
    {
        await SetPin();
        var (action, _) = await Proposed();

        var result = await Confirm(action, together: false);

        Assert.Equal(IraConfirmKind.Executed, result.Kind);
        Assert.Equal(4, _hands.Calls.Count);
        Assert.Equal(1, _hands.MostAtOnce);
    }

    // ---------- the activity log ----------

    [Fact]
    public async Task Each_item_and_the_whole_action_are_rows_of_the_activity_log_as_the_owner_via_ira()
    {
        await SetPin();
        var (action, _) = await Proposed();
        await SetStatus(540, "Stopped");
        _hands.Respond = (_, target) => target is IraStopRun { RunId: 572 } ? IraOutcome.Refused("the runner did not answer") : null;

        await Confirm(action);

        await using var db = NewDb(_db);
        var log = await db.ActivityLog.Where(e => e.Method == "IRA" && e.TargetType != "pin").OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(
            new (string, string, int, bool, string?, string?)[]
            {
                ("ira-close-account-run-done", $"ira/actions/{action.Id}/run/512", 200, true, "run", "512"),
                ("ira-close-account-run-already-closed", $"ira/actions/{action.Id}/run/540", 410, false, "run", "540"),
                ("ira-close-account-run-failed", $"ira/actions/{action.Id}/run/572", 409, false, "run", "572"),
                ("ira-close-account-position-done", $"ira/actions/{action.Id}/position/77", 200, true, "position", "77"),
                ("ira-close-account-position-done", $"ira/actions/{action.Id}/position/78", 200, true, "position", "78"),
                ("ira-close-account-failed", $"ira/actions/{action.Id}/failed", 409, false, "account", "1"),
            },
            log.Select(e => (e.Action, e.Path, e.StatusCode, e.Succeeded, e.TargetType, e.TargetId)));
        Assert.All(log, e => Assert.Equal(("upendra via Ira", Admin, "Admin", "strategies"), (e.UserName, e.UserId!.Value, e.Role, e.Module)));
        Assert.Equal($"Closing upendra's account (action #{action.Id}) via Ira (console): run 512 (Ghost Tangent Crossings on NIFTY) stopped, 1 position squared off.",
            log[0].Summary);
        Assert.Equal($"Closing upendra's account (action #{action.Id}) via Ira (console): run 540 (Fulcrum on BANKNIFTY): nothing to do, it had stopped before the PIN.",
            log[1].Summary);
        Assert.Equal($"Closing upendra's account (action #{action.Id}) via Ira (console): run 572 (Fulcrum on SENSEX) not closed: the runner did not answer.",
            log[2].Summary);
        Assert.StartsWith("Close everything in upendra's account via Ira (console). Partly done: upendra's account at 10:30 IST:", log[5].Summary);
    }

    // ---------- who may propose it ----------

    [Theory]
    [InlineData("api")]
    [InlineData("schedule")]
    public async Task A_trader_a_script_or_another_agent_cannot_propose_it(string source)
    {
        await SetPin();

        var fromScript = await Assert.ThrowsAsync<AiToolArgumentException>(() => Proposed("my account", Caller(source)));
        var fromTrader = await Assert.ThrowsAsync<AiToolArgumentException>(() => Proposed("my account", Caller(user: Trader)));
        var fromAnother = await Assert.ThrowsAsync<AiToolArgumentException>(() => Proposed("my account", Caller(agent: AiCatalog.AiTrader)));

        Assert.Equal("Actions are proposed only by the Desk Assistant, in a chat with the owner.", fromScript.Message);
        Assert.Equal("Only an admin's actions can be proposed.", fromTrader.Message);
        Assert.Equal(fromScript.Message, fromAnother.Message);
        await using var db = NewDb(_db);
        Assert.Empty(db.AiActions);
        Assert.Empty(_hands.Calls);
    }

    [Fact]
    public void The_tool_is_one_of_the_assistants_actions_and_its_prompt_says_when_to_use_it()
    {
        Assert.Contains(AiToolNames.ProposeCloseAccount, AiToolNames.Actions);
        Assert.Contains("propose_close_account", AiCatalog.ActionsPrompt);
        Assert.Contains("one of five", AiCatalog.ActionsPrompt);
        Assert.All(AiCatalog.Agents, a => Assert.DoesNotContain(AiToolNames.ProposeCloseAccount, (a.Tools ?? []).Concat(a.ChatTools ?? [])));
        Assert.Contains(AiActionKind.CloseAccount, AiActionKind.All);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not come true in 10 s.");
            await Task.Delay(10);
        }
    }
}
