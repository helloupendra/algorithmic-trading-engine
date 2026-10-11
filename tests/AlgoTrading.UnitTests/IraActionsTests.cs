using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;
using static AlgoTrading.UnitTests.IraActionsTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Ira's actions (step 4, 11 Oct 2026): the Desk Assistant proposes, code resolves the exact target and stores a
/// pending action, and only the owner's PIN lets code carry it out, once. Scripted hands stand in for the desk; the
/// start's own checks are tested on the real start path in <see cref="IraDeskHandsTests"/>.
/// </summary>
public sealed class IraActionsTests
{
    private readonly string _db = Guid.NewGuid().ToString("N");
    private readonly MovableTime _time = new(Monday1030);
    private readonly FakeHands _hands = new();
    private readonly IraPinGuard _guard;
    private readonly AiSettings _settings = Settings();
    private readonly ListLogger<IraActions> _log = new();

    public IraActionsTests()
    {
        FastPins();
        _guard = new IraPinGuard(_time);
        using var db = NewDb(_db);
        SeedUsers(db);
        SeedDesk(db, _time.Now);
    }

    private IraActions Actions(TradingDbContext? db = null) =>
        new(db ?? NewDb(_db), _hands, _guard, new AiTestKit.Monitor(_settings), _log, _time);

    private IraActionResolver Resolver(TradingDbContext? db = null)
    {
        db ??= NewDb(_db);
        return new IraActionResolver(db, null!, Registry(), new AiSettingsStore(db), null, _time);
    }

    private static AiToolCaller Caller(string source = "console", long user = Admin, string agent = AiCatalog.DeskAssistant, string conversation = "c-test") =>
        new(agent, source, user == Admin ? "upendra" : "someone", user, conversation, CallId: 41);

    private async Task<AiToolOutput> Propose(IAiTool tool, string args, AiToolCaller? caller = null) =>
        await tool.RunAsync(AiToolArgs.Parse(args).For(caller ?? Caller()), CancellationToken.None);

    private ProposeStopRunTool StopTool() => new(Actions(), Resolver(), _time);

    private ProposeSquareOffTool SquareOffTool() => new(Actions(), Resolver(), _time);

    private ProposeAgentSwitchTool SwitchTool() => new(Actions(), Resolver(), _time);

    private async Task SetPin(long user = Admin, string pin = Pin)
    {
        var actions = Actions();
        var owner = (await actions.OwnerAsync(user, CancellationToken.None))!;
        var (change, _, _) = await actions.SetPinAsync(owner, pin, null, requireCurrent: false, "the console", CancellationToken.None);
        Assert.Equal(IraPinChange.Set, change);
    }

    private static IraOwner Upendra => new(Admin, "upendra");

    private async Task<AiAction> ProposedStop(string args = """{"run_id":512}""", AiToolCaller? caller = null)
    {
        var output = await Propose(StopTool(), args, caller);
        long id = (long)JsonSerializer.SerializeToNode(output.Data)!["actionId"]!.GetValue<long>();
        await using var db = NewDb(_db);
        return await db.AiActions.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    // ---------- proposals resolve their target, and ambiguity is an error ----------

    [Fact]
    public async Task A_stop_named_by_strategy_underlying_and_account_resolves_one_run_and_only_waits()
    {
        await SetPin();

        var output = await Propose(StopTool(), """{"strategy":"ghost","underlying":"nifty","account":"Upendra"}""");

        var data = JsonSerializer.SerializeToNode(output.Data)!;
        Assert.True(data["proposed"]!.GetValue<bool>());
        Assert.Equal("10:32 IST", data["expiresAt"]!.GetValue<string>());
        Assert.Contains("Do not ask for the PIN", data["note"]!.GetValue<string>());
        await using var db = NewDb(_db);
        var action = await db.AiActions.SingleAsync();
        Assert.Equal((AiActionKind.StopRun, AiActionStatus.Pending, AiActionChannel.Console, "c-test", 41L, Admin),
            (action.Kind, action.Status, action.Channel, action.ConversationId, action.CallId!.Value, action.UserId));
        Assert.Equal(_time.Now.AddSeconds(120), action.ExpiresUtc);
        Assert.Equal("Stop run 512 (Ghost Tangent Crossings on NIFTY, upendra's account) and square off its open positions, as the Stop button does.", action.Summary);
        var target = JsonSerializer.Deserialize<IraStopRun>(action.ParamsJson, IraActionResolver.Json)!;
        Assert.Equal((512L, "NIFTY", Admin, "upendra"), (target.RunId, target.Underlying, target.AccountUserId, target.Account));
        // Proposed is not done: nothing has been carried out.
        Assert.Empty(_hands.Calls);
    }

    [Fact]
    public async Task Two_live_runs_that_match_are_an_error_naming_both_and_nothing_is_stored()
    {
        await SetPin();

        var error = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), """{"strategy":"Ghost","underlying":"NIFTY"}"""));

        Assert.Equal("Two live Ghost Tangent Crossings runs on NIFTY: run 512 (upendra) and run 530 (coderforchange). Which one? Give its run_id.", error.Message);
        await using var db = NewDb(_db);
        Assert.Empty(db.AiActions);
    }

    [Theory]
    [InlineData("""{"run_id":560}""", "Run 560 (Ghost Tangent Crossings on SENSEX, upendra) is not running: its status is Stopped.")]
    [InlineData("""{"run_id":550}""", "Run 550 is upendra's manual book, which is never stopped: square off its positions with propose_square_off instead.")]
    [InlineData("""{"run_id":9999}""", "There is no run 9999.")]
    [InlineData("""{"strategy":"Fulcrum","account":"coderforchange"}""", "No live run is Fulcrum in coderforchange's account. The live runs: run 512 (Ghost Tangent Crossings on NIFTY, upendra), run 530 (Ghost Tangent Crossings on NIFTY, coderforchange), run 540 (Fulcrum on BANKNIFTY, upendra).")]
    [InlineData("{}", "Name the run: its run_id, or its strategy, underlying and account.")]
    public async Task A_stop_of_anything_but_one_live_strategy_run_is_refused_in_words(string args, string expected)
    {
        await SetPin();

        var error = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), args));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task A_square_off_resolves_exactly_the_open_positions_of_the_book()
    {
        await SetPin();

        var output = await Propose(SquareOffTool(), """{"all":true}""");

        await using var db = NewDb(_db);
        var action = await db.AiActions.SingleAsync();
        var target = JsonSerializer.Deserialize<IraSquareOff>(action.ParamsJson, IraActionResolver.Json)!;
        Assert.Equal((550L, Admin, true), (target.BookRunId, target.AccountUserId, target.All));
        Assert.Equal(new long[] { 77, 78 }, target.PositionIds);
        Assert.Equal("Square off all 2 open positions in upendra's manual book at the market: #77 NSE:NIFTY26OCT25100CE BUY 150 at ₹120.50; #78 NSE:NIFTY26OCT25000PE SELL 75 at ₹88.", action.Summary);
        Assert.StartsWith("Proposed action #", output.Summary);
    }

    [Theory]
    [InlineData("""{"position_id":79}""", "Position 79 is not open (it is Closed).")]
    [InlineData("""{"position_id":90}""", "Position 90 is not in upendra's manual book (it belongs to run 512): a strategy run's legs are squared off by stopping the run.")]
    [InlineData("""{"symbol":"NIFTY26OCT"}""", "Two open positions match NIFTY26OCT: #77 NSE:NIFTY26OCT25100CE BUY 150 at ₹120.50; #78 NSE:NIFTY26OCT25000PE SELL 75 at ₹88. Which one?")]
    [InlineData("{}", "Name the position (position_id or symbol), or set all to true for every open position in upendra's book")]
    [InlineData("""{"all":true,"account":"coderforchange"}""", "coderforchange has no open manual book: nothing to square off.")]
    [InlineData("""{"all":true,"account":"mallory"}""", "There is no account named mallory. The accounts: coderforchange, ravi, upendra.")]
    public async Task A_square_off_names_one_position_or_all_and_refuses_the_rest(string args, string expected)
    {
        await SetPin();

        var error = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(SquareOffTool(), args));

        Assert.StartsWith(expected, error.Message);
    }

    [Fact]
    public async Task One_position_by_symbol_is_resolved_to_its_id()
    {
        await SetPin();

        await Propose(SquareOffTool(), """{"symbol":"25000pe"}""");

        await using var db = NewDb(_db);
        var target = JsonSerializer.Deserialize<IraSquareOff>((await db.AiActions.SingleAsync()).ParamsJson, IraActionResolver.Json)!;
        Assert.Equal(new long[] { 78 }, target.PositionIds);
        Assert.False(target.All);
    }

    [Fact]
    public async Task An_agent_switch_resolves_the_agent_and_refuses_what_is_already_so_or_ambiguous()
    {
        await SetPin();
        await using (var db = NewDb(_db)) await new AiSettingsStore(db).SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null);

        await Propose(SwitchTool(), """{"agent":"AI Trader","on":false,"reason":"choppy day"}""");
        var ambiguous = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(SwitchTool(), """{"agent":"trader","on":false}"""));
        var already = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(SwitchTool(), """{"agent":"news-analyst","on":false}"""));
        var itself = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(SwitchTool(), """{"agent":"Desk Assistant","on":false}"""));

        await using var read = NewDb(_db);
        var action = await read.AiActions.SingleAsync();
        var target = JsonSerializer.Deserialize<IraAgentSwitch>(action.ParamsJson, IraActionResolver.Json)!;
        Assert.Equal((AiCatalog.AiTrader, false, "choppy day"), (target.Agent, target.On, target.Reason));
        Assert.StartsWith("Pause the AI Trader: switch it off", action.Summary);
        Assert.Equal("Two agents match trader: trade-reviewer (Trade Reviewer / Coach), ai-trader (AI Trader). Which one?", ambiguous.Message);
        Assert.Equal("The News Analyst is already off: nothing to change.", already.Message);
        Assert.StartsWith("The Desk Assistant is the one answering", itself.Message);
    }

    [Fact]
    public async Task A_newer_proposal_replaces_the_one_still_waiting_in_the_same_conversation()
    {
        await SetPin();
        var first = await ProposedStop();

        var second = await ProposedStop("""{"run_id":540}""");

        await using var db = NewDb(_db);
        var old = await db.AiActions.SingleAsync(a => a.Id == first.Id);
        Assert.Equal((AiActionStatus.Cancelled, $"Replaced by action #{second.Id}."), (old.Status, old.Error));
        Assert.Equal(AiActionStatus.Pending, (await db.AiActions.SingleAsync(a => a.Id == second.Id)).Status);
    }

    // ---------- nothing without a PIN, and the PIN itself ----------

    [Fact]
    public async Task Without_a_pin_a_proposal_is_refused_with_how_to_set_one_and_nothing_is_stored()
    {
        var error = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), """{"run_id":512}"""));

        Assert.Equal("Set a PIN first: /pin 1234 on Telegram or the Action PIN box on AI → Assistant.", error.Message);
        await using var db = NewDb(_db);
        Assert.Empty(db.AiActions);
        Assert.Empty(_hands.Calls);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("schedule")]
    public async Task Outside_the_owners_chat_or_for_a_trader_or_another_agent_nothing_can_be_proposed(string source)
    {
        await SetPin();

        var fromScript = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), """{"run_id":512}""", Caller(source)));
        var fromTrader = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), """{"run_id":512}""", Caller(user: Trader)));
        var fromAnother = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), """{"run_id":512}""", Caller(agent: AiCatalog.AiTrader)));

        Assert.Equal("Actions are proposed only by the Desk Assistant, in a chat with the owner.", fromScript.Message);
        Assert.Equal("Only an admin's actions can be proposed.", fromTrader.Message);
        Assert.Equal(fromScript.Message, fromAnother.Message);
        await using var db = NewDb(_db);
        Assert.Empty(db.AiActions);
    }

    [Fact]
    public async Task The_pin_is_kept_only_as_a_salted_hash()
    {
        await SetPin(Admin, Pin);
        await SetPin(OtherAdmin, Pin);

        await using var db = NewDb(_db);
        var rows = await db.SystemSettings.Where(s => s.Key.StartsWith(IraActions.PinKeyPrefix)).OrderBy(s => s.Key).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.StartsWith("pbkdf2-sha256$1000$", r.Value));
        Assert.All(rows, r => Assert.DoesNotContain(Pin, r.Value));
        // Salted: the same PIN is two different hashes.
        Assert.NotEqual(rows[0].Value, rows[1].Value);
        Assert.True(IraPin.Verify(Pin, rows[0].Value));
        Assert.False(IraPin.Verify("4322", rows[0].Value));
        Assert.All(new[] { "123", "123456789", "12a4", " 1234", "१२३४" }, bad => Assert.False(IraPin.IsShape(bad)));
        Assert.DoesNotContain(db.ActivityLog, e => (e.Summary ?? string.Empty).Contains(Pin));
    }

    [Fact]
    public async Task On_telegram_a_set_pin_changes_only_with_the_old_one_and_a_wrong_old_one_counts()
    {
        await SetPin();
        var actions = Actions();

        var without = await actions.SetPinAsync(Upendra, "9999", null, requireCurrent: true, "Telegram", CancellationToken.None);
        var wrong = await actions.SetPinAsync(Upendra, "9999", "0000", requireCurrent: true, "Telegram", CancellationToken.None);
        var right = await actions.SetPinAsync(Upendra, "9999", Pin, requireCurrent: true, "Telegram", CancellationToken.None);

        Assert.Equal(IraPinChange.NeedCurrent, without.Change);
        Assert.Equal((IraPinChange.WrongCurrent, 2), (wrong.Change, wrong.Left));
        Assert.Equal(IraPinChange.Changed, right.Change);
        await using var db = NewDb(_db);
        Assert.True(IraPin.Verify("9999", (await db.SystemSettings.SingleAsync(s => s.Key == IraActions.PinKey(Admin))).Value));
    }

    // ---------- confirming ----------

    [Fact]
    public async Task A_wrong_pin_is_refused_and_three_lock_actions_for_fifteen_minutes()
    {
        await SetPin();
        var action = await ProposedStop();
        var actions = Actions();

        var first = await actions.ConfirmAsync(action.Id, Upendra, "0000", AiActionChannel.Console, CancellationToken.None);
        var second = await actions.ConfirmAsync(action.Id, Upendra, "1111", AiActionChannel.Console, CancellationToken.None);
        var third = await actions.ConfirmAsync(action.Id, Upendra, "2222", AiActionChannel.Console, CancellationToken.None);
        var rightButLocked = await actions.ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);
        var proposing = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), """{"run_id":540}"""));

        Assert.Equal((IraConfirmKind.WrongPin, 2), (first.Kind, first.AttemptsLeft));
        Assert.Equal("Wrong PIN: 2 tries left before actions lock for 15 minutes. Nothing was done.", first.Message);
        Assert.Equal((IraConfirmKind.WrongPin, 1), (second.Kind, second.AttemptsLeft));
        Assert.Equal((IraConfirmKind.Locked, _time.Now.AddMinutes(15)), (third.Kind, third.LockedUntilUtc!.Value));
        Assert.Equal("Wrong PIN. Actions are locked until 10:45 IST after 3 wrong PINs. Nothing was done.", third.Message);
        Assert.Equal(IraConfirmKind.Locked, rightButLocked.Kind);
        Assert.Equal("Actions are locked until 10:45 IST after 3 wrong PINs.", proposing.Message);
        Assert.Empty(_hands.Calls);
        // The lock is an Information line, never a warning: Sentinel opens no incident for it.
        var locked = Assert.Single(_log.Entries, e => e.Message.Contains("locked until"));
        Assert.Equal((LogLevel.Information, "Ira actions: upendra's actions are locked until 10:45 IST after 3 wrong PINs"), (locked.Level, locked.Message));
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
        await using var db = NewDb(_db);
        Assert.Equal(AiActionStatus.Pending, (await db.AiActions.SingleAsync(a => a.Id == action.Id)).Status);
    }

    [Fact]
    public async Task Wrong_pins_ten_minutes_apart_do_not_lock()
    {
        await SetPin();
        var actions = Actions();

        await actions.ConfirmAsync((await ProposedStop()).Id, Upendra, "0000", AiActionChannel.Console, CancellationToken.None);
        await actions.ConfirmAsync((await ProposedStop()).Id, Upendra, "0000", AiActionChannel.Console, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));
        var third = await actions.ConfirmAsync((await ProposedStop()).Id, Upendra, "0000", AiActionChannel.Console, CancellationToken.None);

        Assert.Equal((IraConfirmKind.WrongPin, 2), (third.Kind, third.AttemptsLeft));
    }

    [Fact]
    public async Task An_expired_action_is_refused_and_marked_expired()
    {
        await SetPin();
        var action = await ProposedStop();
        _time.Advance(TimeSpan.FromSeconds(121));

        var result = await Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);

        Assert.Equal(IraConfirmKind.Expired, result.Kind);
        Assert.Equal($"Action #{action.Id} expired at 10:32 IST: nothing was done. Ask again.", result.Message);
        Assert.Empty(_hands.Calls);
        await using var db = NewDb(_db);
        Assert.Equal(AiActionStatus.Expired, (await db.AiActions.SingleAsync()).Status);
    }

    [Fact]
    public async Task The_right_pin_carries_it_out_once_and_a_second_confirm_runs_nothing()
    {
        await SetPin();
        var action = await ProposedStop();
        _time.Advance(TimeSpan.FromSeconds(30));

        var done = await Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);
        var again = await Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);

        Assert.Equal((IraConfirmKind.Executed, "Done: stop_run for upendra."), (done.Kind, done.Message));
        Assert.Equal((IraConfirmKind.NotPending, $"Action #{action.Id} was already carried out."), (again.Kind, again.Message));
        var (kind, owner, target) = Assert.Single(_hands.Calls);
        Assert.Equal((AiActionKind.StopRun, Upendra), (kind, owner));
        Assert.Equal(512L, ((IraStopRun)target).RunId);
        await using var db = NewDb(_db);
        var row = await db.AiActions.SingleAsync();
        Assert.Equal((AiActionStatus.Executed, _time.Now, _time.Now), (row.Status, row.ConfirmedUtc!.Value, row.ExecutedUtc!.Value));
        Assert.Equal("Done: stop_run for upendra.", IraActions.ResultMessage(row));
    }

    [Fact]
    public async Task Two_confirms_at_once_carry_it_out_once()
    {
        await SetPin();
        var action = await ProposedStop();
        var release = new TaskCompletionSource();
        _hands.Hold = release.Task;

        var one = Task.Run(() => Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None));
        var two = Task.Run(() => Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None));
        // One of them is refused while the other is still carrying it out.
        var first = await Task.WhenAny(one, two).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(IraConfirmKind.NotPending, (await first).Kind);
        release.SetResult();
        var results = await Task.WhenAll(one, two);

        Assert.Single(results, r => r.Kind == IraConfirmKind.Executed);
        Assert.Single(results, r => r.Kind == IraConfirmKind.NotPending && r.Message.Contains("already being carried out"));
        Assert.Single(_hands.Calls);
    }

    [Fact]
    public async Task Another_admins_or_another_channels_action_is_not_found()
    {
        await SetPin();
        await SetPin(OtherAdmin);
        var action = await ProposedStop();

        var ravi = await Actions().ConfirmAsync(action.Id, new IraOwner(OtherAdmin, "ravi"), Pin, AiActionChannel.Console, CancellationToken.None);
        var onTelegram = await Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Telegram, CancellationToken.None);

        Assert.Equal(IraConfirmKind.NotFound, ravi.Kind);
        Assert.Equal(IraConfirmKind.NotFound, onTelegram.Kind);
        Assert.Empty(_hands.Calls);
    }

    [Fact]
    public async Task A_refusal_by_the_desk_is_a_failed_action_with_its_words()
    {
        await SetPin();
        var action = await ProposedStop();
        _hands.Answer = _ => IraOutcome.Refused("run 512 is not running any more, so there was nothing to stop");

        var result = await Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);

        Assert.Equal((IraConfirmKind.Failed, "Not done: run 512 is not running any more, so there was nothing to stop."), (result.Kind, result.Message));
        await using var db = NewDb(_db);
        var row = await db.AiActions.SingleAsync();
        Assert.Equal((AiActionStatus.Failed, result.Message), (row.Status, row.Error));
    }

    [Fact]
    public async Task With_actions_switched_off_a_waiting_action_is_not_carried_out()
    {
        await SetPin();
        var action = await ProposedStop();
        _settings.Actions.Enabled = false;

        var result = await Actions().ConfirmAsync(action.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);
        var proposing = await Assert.ThrowsAsync<AiToolArgumentException>(() => Propose(StopTool(), """{"run_id":540}"""));

        Assert.Equal(IraConfirmKind.Off, result.Kind);
        Assert.StartsWith("Actions are switched off on this desk", proposing.Message);
        Assert.Empty(_hands.Calls);
    }

    // ---------- the activity log ----------

    [Fact]
    public async Task Every_outcome_is_written_to_the_activity_log_as_the_owner_via_ira()
    {
        await SetPin();
        var actions = Actions();

        var executed = await ProposedStop();
        await actions.ConfirmAsync(executed.Id, Upendra, "0000", AiActionChannel.Console, CancellationToken.None);
        await actions.ConfirmAsync(executed.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);
        var failed = await ProposedStop("""{"run_id":540}""");
        _hands.Answer = _ => IraOutcome.Refused("the desk said no");
        await actions.ConfirmAsync(failed.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);
        var cancelled = await ProposedStop();
        await actions.CancelAsync(cancelled.Id, Upendra, AiActionChannel.Console, CancellationToken.None);
        var expired = await ProposedStop();
        _time.Advance(TimeSpan.FromMinutes(3));
        await actions.ConfirmAsync(expired.Id, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);
        var switched = await Propose(SwitchTool(), """{"agent":"news-analyst","on":true}""");
        _hands.Answer = null;
        long switchId = JsonSerializer.SerializeToNode(switched.Data)!["actionId"]!.GetValue<long>();
        await actions.ConfirmAsync(switchId, Upendra, Pin, AiActionChannel.Console, CancellationToken.None);

        await using var db = NewDb(_db);
        var log = await db.ActivityLog.Where(e => e.Method == "IRA" && e.TargetType != "pin").OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(
            new[]
            {
                ("ira-stop-run-wrong-pin", $"ira/actions/{executed.Id}/wrong-pin", 403, false),
                ("ira-stop-run-executed", $"ira/actions/{executed.Id}/executed", 200, true),
                ("ira-stop-run-failed", $"ira/actions/{failed.Id}/failed", 409, false),
                ("ira-stop-run-cancelled", $"ira/actions/{cancelled.Id}/cancelled", 200, false),
                ("ira-stop-run-expired", $"ira/actions/{expired.Id}/expired", 410, false),
                ("ira-agent-switch-executed", $"ira/actions/{switchId}/executed", 200, true),
            },
            log.Select(e => (e.Action, e.Path, e.StatusCode, e.Succeeded)));
        Assert.All(log, e => Assert.Equal(("upendra via Ira", Admin, "Admin"), (e.UserName, e.UserId!.Value, e.Role)));
        Assert.Equal(new[] { "strategies", "strategies", "strategies", "strategies", "strategies", "ai" }, log.Select(e => e.Module));
        Assert.Equal(("run", "512"), (log[1].TargetType, log[1].TargetId));
        Assert.Equal("Executed via Ira (console): Stop run 512 (Ghost Tangent Crossings on NIFTY, upendra's account) and square off its open positions, as the Stop button does. Done: stop_run for upendra.", log[1].Summary);
        Assert.Contains("Not done: the desk said no.", log[2].Summary);
        // Never the PIN, right or wrong.
        Assert.DoesNotContain(db.ActivityLog, e => (e.Summary ?? string.Empty).Contains(Pin) || (e.Summary ?? string.Empty).Contains("0000"));
        Assert.Contains(db.ActivityLog, e => e.Action == "ira-pin-set-set" && e.Summary == "Set the Action PIN on the console.");
    }
}
