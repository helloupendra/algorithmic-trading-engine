using System.Text.Json.Nodes;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI Trader placing paper orders in its own account (owner, 10 Oct 2026): a buy in its own manual book with its stop
/// and target on the position, an exit of its own position, a start or stop of its own run; the Validator before a buy
/// or start (two REJECTs block it, no verdict blocks it too), the Risk Memo that may only tighten, the rules judged again
/// on the fresh book at the price it would pay, and the Execution Reporter on each of its fills.
/// </summary>
public sealed class AiTraderExecutionTests : IDisposable
{
    // Monday 5 Oct 2026, 11:00 IST: the look's clock. The placement happens a minute later.
    private static readonly DateTime Eleven = IstTime.FromIst(new DateTime(2026, 10, 5, 11, 0, 0));
    private static readonly DateTime Now = Eleven.AddMinutes(1);

    private const string Call = "NSE:NIFTY26O0622650CE";

    private const string BuyAtmCall = """
        {"action":"buy","underlying":"NIFTY","option":"CE","strike":"ATM","lots":1,"stopLoss":90,"target":160,
         "reason":"NIFTY above EMA20 and EMA50 with call OI falling at the call wall.","confidence":0.55}
        """;

    private const string Accept = """{"verdict":"ACCEPT","flaw":"The trend is young.","quote":"","confidence":0.6}""";

    private const string Reject = """
        {"verdict":"REJECT","flaw":"It buys a call with the index pressed against its EMAs and nothing else in the brief behind it.",
         "quote":"NIFTY 22,612 above EMA20 and EMA50","confidence":0.7}
        """;

    private const string MemoNoChange = """{"memo":"At its stop it loses about ₹1,950, a fifth of the day's budget.","lots":null,"stopLoss":null,"target":null,"why":""}""";

    private const string MemoTighterStop = """
        {"memo":"At its stop it loses about ₹1,950, a fifth of the day's budget.","lots":null,"stopLoss":95,"target":null,
         "why":"A closer stop keeps a loss near ₹1,650."}
        """;

    // ---------- a buy ----------

    [Fact]
    public async Task An_allowed_buy_is_placed_in_its_own_manual_book_at_the_ask_with_its_stop_and_target_on_the_position()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));
        // Another trader's book, which nothing of the AI Trader's may touch.
        var other = await t.OtherTradersBookAsync();

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((true, true, "ok", string.Empty), (row.Allowed, row.Executed, row.Rule, row.Error));
        var account = await AiTraderAccount.FindAsync(t.Db, default);
        var book = await ManualBook.FindAsync(t.Db, account!.Id, default);
        var position = Assert.Single(t.Db.PaperPositions.AsNoTracking().Where(p => p.SimulationRunId == book!.Id));
        Assert.Equal((Call, "LONG", 1, 120.5m, 90m, 160m, false, "Open"),
            (position.Symbol, position.Direction, position.Quantity, position.AveragePrice, position.StopLossPrice, position.TargetPrice, position.CarryForward,
                position.Status));
        var order = Assert.Single(t.Db.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == book!.Id));
        Assert.Equal(("BUY", 120.5m), (order.Side, order.FillPrice));
        Assert.Empty(t.Db.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == other));

        // The decision says what it placed and where.
        var placed = JsonNode.Parse(row.ResultJson)!["placed"]!;
        Assert.Equal((order.Id, position.Id, book!.Id), ((long)placed["orderId"]!, (long)placed["positionId"]!, (long)placed["bookRunId"]!));
        var view = AiTraderResults.Placement(row)!;
        Assert.Equal(("order", $"/trade/runs/{book.Id}"), (view.Kind, view.Link));
        Assert.StartsWith($"Order #{order.Id}: bought 1 lot(s) of {Call} at ₹120.50", view.Text);

        // The order's signal names the decision, so its fill can be traced back to it.
        var signal = t.Db.SimulationSignals.AsNoTracking().Single(s => s.Id == order.SimulationSignalId);
        Assert.Equal(row.Id, (long)JsonNode.Parse(signal.MetadataJson)!["source"]!["decisionId"]!);
    }

    [Fact]
    public async Task The_validator_is_asked_three_times_and_its_report_keeps_each_answer()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Reject), Answer(Accept), Answer(MemoNoChange));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.True(row.Executed);
        var asks = t.Ai.Provider.Requests.Where(r => r.Model == Judge1).ToList();
        Assert.Equal(5, asks.Count);
        // Sampled: the three asks are not asked cold, and each one is the plan with the brief it rests on.
        Assert.All(asks.Skip(1).Take(3), a =>
        {
            var body = JsonNode.Parse(a.Body)!;
            Assert.Equal(AiTraderValidator.Temperature, (double)body["temperature"]!);
            string asked = Asked(a);
            Assert.Contains("THE PLAN", asked);
            Assert.Contains("MARKET BRIEF THE PLAN RESTS ON", asked);
            Assert.Contains("THE RULES' VERDICT: allowed", asked);
        });

        var report = t.Db.AiReports.AsNoTracking().Single(r => r.AgentKey == AiCatalog.Validator);
        Assert.Equal((AiReportSubject.Decision, row.Id.ToString(), AiReportStatus.Ok), (report.SubjectType, report.SubjectId, report.Status));
        Assert.Equal($"Decision #{row.Id}: ACCEPT (2 of 3)", report.Title);
        var data = JsonNode.Parse(report.DataJson)!;
        Assert.Equal((2, 1), ((int)data["accepts"]!, (int)data["rejects"]!));
        Assert.Equal(3, data["samples"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_majority_reject_blocks_the_buy_with_the_named_flaw_and_nothing_is_placed_or_memoed()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Reject), Answer(Accept), Answer(Reject));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((false, false, AiTraderValidator.RejectedRule), (row.Allowed, row.Executed, row.Rule));
        Assert.Contains("The Validator rejected the plan (2 of 3): It buys a call with the index pressed against its EMAs", row.Why);
        Assert.Empty(t.Db.PaperOrders);
        Assert.Empty(t.Db.PaperPositions);
        // The memo is not asked for a plan that is not placed.
        Assert.Equal(4, t.Ai.Provider.Requests.Count(r => r.Model == Judge1));
        Assert.DoesNotContain(t.Db.AiReports, r => r.AgentKey == AiCatalog.RiskMemo);
        // Its next look reads the refusal, as it reads the rules'.
        Assert.Contains("refused (validator)", AiTraderAgent.LastLooks([row]));
    }

    [Theory]
    [InlineData("NIFTY 22,612 above EMA20 and EMA50", true)]
    [InlineData("NIFTY 22,612 above EMA20...direction.logit p 0.41", true)]
    [InlineData("NIFTY 22,612 above EMA20 | direction.logit p 0.41", true)]
    [InlineData("\"NIFTY 22,612 above EMA20\", \"direction.logit p 0.41\"", true)]
    [InlineData("NIFTY 22,612 above EMA20...the RBI cut rates at noon", false)]
    [InlineData("NIFTY ... EMA20", false)]
    public void A_quote_stitched_from_passages_counts_only_when_each_is_word_for_word(string quote, bool counts)
    {
        const string brief = "NIFTY 22,612 above EMA20 and EMA50; trend.logit p 0.62; direction.logit p 0.41; 225 minutes left.";

        Assert.Equal(counts, AiTraderValidator.Quotes(brief, quote));
    }

    [Fact]
    public async Task A_reject_whose_quote_is_not_in_the_brief_is_not_counted()
    {
        string madeUp = Reject.Replace("NIFTY 22,612 above EMA20 and EMA50", "the RBI cut rates at noon");
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(madeUp), Answer(madeUp), Answer(Accept));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        // One ACCEPT and two answers that do not count: no majority, so nothing is placed.
        Assert.Equal((false, AiTraderValidator.UnavailableRule), (row.Allowed, row.Rule));
        Assert.Empty(t.Db.PaperOrders);
        var data = JsonNode.Parse(t.Db.AiReports.AsNoTracking().Single(r => r.AgentKey == AiCatalog.Validator).DataJson)!;
        Assert.Contains("quote is not in the brief", (string)data["samples"]![0]!["error"]!);
    }

    [Fact]
    public async Task With_no_model_answering_the_validator_the_buy_is_not_placed_and_the_report_says_why()
    {
        using var t = new Trader();
        // The AI Trader's own answer only: every Validator ask finds no model.
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((false, false, AiTraderValidator.UnavailableRule), (row.Allowed, row.Executed, row.Rule));
        Assert.Contains("gave no verdict (0 accept, 0 reject, 3 no answer", row.Why);
        Assert.Contains("never placed", row.Why);
        Assert.Empty(t.Db.PaperOrders);
        var report = t.Db.AiReports.AsNoTracking().Single(r => r.AgentKey == AiCatalog.Validator);
        Assert.Equal(AiReportStatus.Failed, report.Status);
    }

    [Fact]
    public async Task With_the_validator_switched_off_nothing_is_placed_and_it_is_asked_once()
    {
        using var t = new Trader();
        await t.Ai.Store.SetAgentEnabledAsync(AiCatalog.Validator, false, "upendra", "testing");
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((false, AiTraderValidator.UnavailableRule), (row.Allowed, row.Rule));
        Assert.Contains("switched off", row.Why);
        Assert.Empty(t.Db.PaperOrders);
        Assert.Single(t.Db.AiCalls, c => c.AgentKey == AiCatalog.Validator);
    }

    // ---------- the risk memo ----------

    [Fact]
    public async Task A_tighter_stop_from_the_risk_memo_is_the_stop_placed()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoTighterStop));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.True(row.Executed);
        var position = Assert.Single(t.Db.PaperPositions.AsNoTracking());
        Assert.Equal((95m, 160m), (position.StopLossPrice, position.TargetPrice));
        Assert.True(AiTraderResults.Tightened(row.ResultJson));
        var memo = t.Db.AiReports.AsNoTracking().Single(r => r.AgentKey == AiCatalog.RiskMemo);
        Assert.Equal($"Decision #{row.Id}: tightened (stop 90 → 95)", memo.Title);
        // The memo read the account's exposure and the loss at the plan's stop.
        Assert.Contains("AT ITS STOP it loses about ₹1,950", Asked(t.Ai.Provider.Requests.Last(r => r.Model == Judge1)));
    }

    [Fact]
    public void The_risk_memo_applies_only_tightenings_that_are_still_valid()
    {
        var plan = new AiTraderPlan(AiTraderPlan.Buy, "NIFTY", "CE", "ATM", 2, 90m, 160m, null, null, null, "Trend.", 0.5);
        var contract = new AiTraderContract(Call, "NIFTY", "CE", 22650m, new DateOnly(2026, 10, 6), 120m, 65);

        var tighter = AiTraderRiskMemo.Apply(plan, contract, """{"memo":"Smaller.","lots":1,"stopLoss":100,"target":150}""");
        Assert.Equal((1, 100m, 150m), (tighter.Plan.Lots, tighter.Plan.StopLoss, tighter.Plan.Target));
        Assert.Equal(new[] { "lots 2 → 1", "stop 90 → 100", "target 160 → 150" }, tighter.Applied);

        var looser = AiTraderRiskMemo.Apply(plan, contract, """{"memo":"Bigger.","lots":3,"stopLoss":80,"target":200}""");
        Assert.Equal(plan, looser.Plan);
        Assert.Equal(3, looser.Ignored.Count);
        Assert.All(looser.Ignored, why => Assert.Contains("looser, ignored", why));

        // Tighter past the entry is no stop or target at all.
        var past = AiTraderRiskMemo.Apply(plan, contract, """{"memo":"Odd.","lots":0,"stopLoss":125,"target":110}""");
        Assert.Equal(plan, past.Plan);
        Assert.Contains("lots 0 is under 1", past.Ignored);
        Assert.Contains(past.Ignored, why => why.StartsWith("stop 125 is at or above the price it is bought at", StringComparison.Ordinal));
        Assert.Contains(past.Ignored, why => why.StartsWith("target 110 is at or under the price it is bought at", StringComparison.Ordinal));

        // A start has lots to tighten and nothing else.
        var start = new AiTraderPlan(AiTraderPlan.StartStrategy, "NIFTY", null, null, 2, null, null, "ChainFlowBuy", null, null, "Flow.", 0.5);
        var started = AiTraderRiskMemo.Apply(start, null, """{"memo":"One lot.","lots":1,"stopLoss":50}""");
        Assert.Equal((1, (decimal?)null), (started.Plan.Lots, started.Plan.StopLoss));
        Assert.Contains("a strategy start has no stop to tighten", started.Ignored);

        // An answer that is not a memo changes nothing.
        Assert.Equal((AiReportStatus.Invalid, plan), (AiTraderRiskMemo.Apply(plan, contract, "Looks fine to me.").Status, plan));
    }

    [Fact]
    public async Task With_no_risk_memo_the_buy_goes_on_unchanged()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.True(row.Executed);
        Assert.Equal(90m, Assert.Single(t.Db.PaperPositions.AsNoTracking()).StopLossPrice);
        var memo = JsonNode.Parse(row.ResultJson)!["riskMemo"]!;
        Assert.Equal((AiReportStatus.Failed, false), ((string)memo["status"]!, (bool)memo["tightened"]!));
    }

    // ---------- judged again before it is placed ----------

    [Fact]
    public async Task The_buy_is_judged_again_at_the_price_it_would_pay_and_refused_when_that_breaks_a_rule()
    {
        using var t = new Trader();
        // The brief's ask was 120; by the time it is placed the ask is 160, and a 90 stop is now more than 40% under it.
        t.Quote(Call, bid: 159m, ask: 160m);
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((false, false, "stop"), (row.Allowed, row.Executed, row.Rule));
        Assert.StartsWith("Judged again on the fresh book at the price it would pay: The stop ₹90 is more than 40% below the entry ₹160", row.Why);
        Assert.Empty(t.Db.PaperOrders);
    }

    [Fact]
    public async Task The_buy_is_judged_again_on_the_book_as_it_is_once_the_checks_are_done()
    {
        // While the Validator was asked, three positions opened: the book it is placed into is full.
        var full = Enumerable.Range(1, 3)
            .Select(i => new AiTraderOpenPosition(i, $"NSE:NIFTY26O062260{i}PE", 1, 50m, 3250m, 50m, 40m, 70m, 0m)).ToList();
        using var t = new Trader();
        var books = new ChangingBooks(t.Reader, after: 2, full);
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));

        var row = await t.Agent(books).DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((false, "open-positions"), (row.Allowed, row.Rule));
        Assert.StartsWith("Judged again on the fresh book", row.Why);
        Assert.Empty(t.Db.PaperOrders);
    }

    [Fact]
    public async Task A_stale_quote_is_not_bought_on_and_the_decision_says_so()
    {
        using var t = new Trader();
        t.Quote(Call, bid: 119.5m, ask: 120.5m, updatedUtc: Eleven.AddMinutes(-10));
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((true, false), (row.Allowed, row.Executed));
        Assert.StartsWith($"Not placed: The quote for {Call} is 660 s old", row.Error);
        Assert.Empty(t.Db.PaperOrders);
    }

    // ---------- an exit ----------

    [Fact]
    public async Task An_exit_closes_its_own_position_at_the_bid_without_asking_the_validator()
    {
        using var t = new Trader();
        long book = await AiTraderBookReaderTests.Book(t.Db);
        var held = AiTraderBookReaderTests.Held(book, Call, Eleven.AddMinutes(-30), 100m, 1, mark: 119.5m);
        t.Db.PaperPositions.Add(held);
        await t.Db.SaveChangesAsync();
        t.Ai.Provider.On(Judge1, Answer($$"""{"action":"exit","positionId":{{held.Id}},"reason":"Took the move.","confidence":0.6}"""));

        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((true, true, string.Empty), (row.Allowed, row.Executed, row.Error));
        var closed = t.Db.PaperPositions.AsNoTracking().Single(p => p.Id == held.Id);
        Assert.Equal("Closed", closed.Status);
        var sell = t.Db.PaperOrders.AsNoTracking().Single(o => o.SimulationRunId == book && o.Side == "SELL");
        Assert.Equal(119.5m, sell.FillPrice);
        var exit = JsonNode.Parse(row.ResultJson)!["exit"]!;
        Assert.Equal((sell.Id, held.Id, 119.5m), ((long)exit["orderId"]!, (long)exit["positionId"]!, (decimal)exit["bidSeen"]!));
        Assert.Equal("exit", AiTraderResults.Placement(row)!.Kind);
        // Getting flatter is never put to the Validator.
        Assert.Single(t.Ai.Provider.Requests, r => r.Model == Judge1);
        Assert.Empty(t.Db.AiReports);
    }

    [Fact]
    public async Task The_kill_switch_refuses_a_buy_but_never_its_exit()
    {
        using var t = new Trader(killSwitch: true);
        long book = await AiTraderBookReaderTests.Book(t.Db);
        var held = AiTraderBookReaderTests.Held(book, Call, Eleven.AddMinutes(-30), 100m, 1, mark: 119.5m);
        t.Db.PaperPositions.Add(held);
        await t.Db.SaveChangesAsync();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall),
            Answer($$"""{"action":"exit","positionId":{{held.Id}},"reason":"Desk halted.","confidence":0.6}"""));

        var buy = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);
        var exit = await t.Agent().DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Live, null, default);

        Assert.Equal((false, "kill-switch", false), (buy.Allowed, buy.Rule, buy.Executed));
        Assert.Equal((true, true), (exit.Allowed, exit.Executed));
        Assert.Equal("Closed", t.Db.PaperPositions.AsNoTracking().Single(p => p.Id == held.Id).Status);
        Assert.Single(t.Db.PaperOrders.AsNoTracking().Where(o => o.Side == "BUY" || o.Side == "SELL"));
        // Neither was put to the Validator: the buy never passed the rules, and an exit is never asked.
        Assert.Equal(2, t.Ai.Provider.Requests.Count(r => r.Model == Judge1));
    }

    // ---------- its strategy runs ----------

    [Fact]
    public async Task A_start_is_validated_then_started_in_its_own_account_with_lots_inside_the_rules()
    {
        using var t = new Trader();
        var runs = new FakeRuns();
        t.Ai.Provider.On(Judge1,
            Answer("""{"action":"start_strategy","strategy":"chainflowbuy","underlying":"nifty","lots":5,"reason":"Flow is building.","confidence":0.5}"""),
            Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));

        var row = await t.Agent(runs: runs).DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((true, true), (row.Allowed, row.Executed));
        var (account, strategy, underlying, lots) = Assert.Single(runs.Started);
        Assert.Equal((AiTraderAccount.UserName, "ChainFlowBuy", "NIFTY", 1), (account, strategy, underlying, lots));
        var started = JsonNode.Parse(row.ResultJson)!["started"]!;
        Assert.Equal((77L, "ChainFlowBuy"), ((long)started["runId"]!, (string)started["strategy"]!));
        Assert.Equal(("run-start", "/trade/runs/77"), (AiTraderResults.Placement(row)!.Kind, AiTraderResults.Placement(row)!.Link));
    }

    [Fact]
    public async Task A_start_refused_by_the_desk_is_recorded_not_thrown()
    {
        using var t = new Trader();
        var runs = new FakeRuns { Refusal = "NSE is closed today (Sat 10 Oct): a live run can start only on a trading day." };
        t.Ai.Provider.On(Judge1,
            Answer("""{"action":"start_strategy","strategy":"GhostTangentCrossings","underlying":"BANKNIFTY","lots":1,"reason":"Trend day.","confidence":0.5}"""),
            Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));

        var row = await t.Agent(runs: runs).DecideAsync(Eleven, AiTraderModes.Live, null, default);

        Assert.Equal((true, false), (row.Allowed, row.Executed));
        Assert.Equal("Not started: NSE is closed today (Sat 10 Oct): a live run can start only on a trading day.", row.Error);
    }

    [Fact]
    public async Task A_stop_stops_only_its_own_run_without_the_validator()
    {
        using var t = new Trader();
        var runs = new FakeRuns();
        var books = new FixedBooks(new AiTraderBook(Eleven, true, false, 0m, 0, [], [new AiTraderRun(41, "ChainFlowBuy", "NIFTY", -120m)]));
        t.Ai.Provider.On(Judge1,
            Answer("""{"action":"stop_strategy","runId":41,"reason":"Flow faded.","confidence":0.5}"""),
            Answer("""{"action":"stop_strategy","runId":99,"reason":"Not mine.","confidence":0.5}"""));

        var own = await t.Agent(books, runs).DecideAsync(Eleven, AiTraderModes.Live, null, default);
        var other = await t.Agent(books, runs).DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Live, null, default);

        Assert.Equal((true, true), (own.Allowed, own.Executed));
        Assert.Equal(41L, Assert.Single(runs.Stopped).RunId);
        Assert.Equal((false, "own-runs", false), (other.Allowed, other.Rule, other.Executed));
        Assert.Equal(2, t.Ai.Provider.Requests.Count(r => r.Model == Judge1));
    }

    // ---------- shadow and replay are unchanged ----------

    [Theory]
    [InlineData(AiTraderModes.Shadow)]
    [InlineData(AiTraderModes.Replay)]
    public async Task In_shadow_and_replay_an_allowed_buy_goes_to_the_shadow_book_and_nothing_is_placed_or_validated(string mode)
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall));

        var row = await t.Agent().DecideAsync(Eleven, mode, mode == AiTraderModes.Replay ? 4 : null, default);

        Assert.Equal((true, false, string.Empty), (row.Allowed, row.Executed, row.Error));
        Assert.Single(t.Db.AiTraderShadowPositions);
        Assert.Empty(t.Db.PaperOrders);
        Assert.Empty(t.Db.AiReports);
        Assert.Single(t.Ai.Provider.Requests, r => r.Model == Judge1);
        Assert.Null(AiTraderResults.Placement(row));
    }

    // ---------- the execution reporter ----------

    [Fact]
    public async Task Each_fill_gets_a_report_comparing_it_with_the_price_its_decision_saw()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));
        var buy = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);
        long positionId = (long)JsonNode.Parse(buy.ResultJson)!["placed"]!["positionId"]!;
        t.Ai.Provider.On(Judge1, Answer($$"""{"action":"exit","positionId":{{positionId}},"reason":"Done.","confidence":0.6}"""));
        var exit = await t.Agent().DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Live, null, default);
        Assert.True(exit.Executed);
        t.Ai.Provider.On(Extract1,
            Answer("""{"flag":"slipped","note":"Paid ₹0.50 a unit over the ask its decision saw, ₹32.50 in all, 60 s after its look."}"""),
            Answer("""{"flag":"clean","note":"Sold at the bid it saw."}"""));

        var reporter = t.Reporter();
        Assert.True(await reporter.RunOnceAsync(Now, default));
        Assert.True(await reporter.RunOnceAsync(Now, default));
        Assert.False(await reporter.RunOnceAsync(Now, default));

        var reports = t.Db.AiReports.AsNoTracking().Where(r => r.AgentKey == AiCatalog.ExecutionReporter).OrderBy(r => r.Id).ToList();
        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal((AiReportSubject.Order, AiReportStatus.Ok), (r.SubjectType, r.Status)));

        var entry = JsonNode.Parse(reports[0].DataJson)!;
        Assert.Equal(("entry", 120m, 0.5m, 32.5m, 0.42m), ((string)entry["trigger"]!, (decimal)entry["reference"]!, (decimal)entry["slippagePerUnit"]!,
            (decimal)entry["slippageRupees"]!, (decimal)entry["slippagePercent"]!));
        Assert.Equal((60, 30.5m, 39.5m), ((int)entry["secondsToFill"]!, (decimal)entry["stopDistance"]!, (decimal)entry["targetDistance"]!));
        Assert.Equal(buy.Id, (long)entry["entryDecisionId"]!);
        Assert.StartsWith("Paid ₹0.50 a unit", reports[0].Body);
        Assert.Contains("₹0.50 worse than the ask its decision saw (0.42%), 60 s after its look", reports[0].Title);

        var sold = JsonNode.Parse(reports[1].DataJson)!;
        Assert.Equal(("its exit", 119.5m, 0m), ((string)sold["trigger"]!, (decimal)sold["reference"]!, (decimal)sold["slippagePerUnit"]!));
        Assert.Equal(exit.Id, (long)sold["exitDecisionId"]!);
    }

    [Fact]
    public async Task A_note_with_a_number_that_is_not_among_the_facts_is_not_kept()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoNoChange));
        await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);
        t.Ai.Provider.On(Extract1, Answer("""{"flag":"slipped","note":"Paid ₹4.75 over the ask."}"""));

        Assert.True(await t.Reporter().RunOnceAsync(Now, default));

        var report = t.Db.AiReports.AsNoTracking().Single(r => r.AgentKey == AiCatalog.ExecutionReporter);
        Assert.Equal(AiReportStatus.Invalid, report.Status);
        Assert.Contains("4.75", report.Error);
        // Code's numbers are kept all the same.
        Assert.Equal(0.5m, (decimal)JsonNode.Parse(report.DataJson)!["slippagePerUnit"]!);
    }

    [Fact]
    public async Task Fills_in_other_users_books_are_not_its_to_report()
    {
        using var t = new Trader();
        long other = await t.OtherTradersBookAsync();
        t.Db.PaperOrders.Add(AiTraderBookReaderTests.Fill(other, Call, "BUY", 120m, Eleven));
        await AiTraderAccount.EnsureAsync(t.Db, default);
        await t.Db.SaveChangesAsync();

        Assert.False(await t.Reporter().RunOnceAsync(Now, default));
        Assert.Null(await t.Reporter().NextDueAsync(Now, default));
    }

    [Theory]
    [InlineData("Paid ₹0.5 more, 0.4% over.", null)]
    [InlineData("Paid ₹0.50 more, 0.42% over, ₹32.50 in all.", null)]
    [InlineData("Paid ₹1.25 more.", "1.25")]
    public void A_notes_numbers_must_be_the_facts_as_given_or_rounded(string note, string? unknown) =>
        Assert.Equal(unknown, AiTraderExecutionReporter.UnknownNumber(note, """{"slippagePerUnit":0.5,"slippagePercent":0.42,"slippageRupees":32.5}"""));

    [Fact]
    public async Task In_a_chat_the_checks_read_their_own_records_on_a_decision()
    {
        using var t = new Trader();
        t.Ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoTighterStop));
        var row = await t.Agent().DecideAsync(Eleven, AiTraderModes.Live, null, default);
        t.Ai.Provider.On(Extract1, Answer("""{"flag":"slipped","note":"Paid ₹0.50 a unit over the ask its decision saw."}"""));
        await t.Reporter().RunOnceAsync(Now, default);

        var output = await new AlgoTrading.Api.Services.AiTools.AiTraderChecksTool(t.Db, t.Clock)
            .RunAsync(AiToolArgs.Parse($$"""{"decisionId":{{row.Id}}}"""), default);

        var data = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(output.Data))!;
        Assert.Equal(new[] { AiCatalog.ExecutionReporter, AiCatalog.RiskMemo, AiCatalog.Validator },
            data["records"]!.AsArray().Select(r => (string)r!["agent"]!));
        Assert.Equal(3, output.Rows);
    }

    // ---------- its account ----------

    [Fact]
    public async Task Its_account_holds_the_strategies_module_and_its_allowed_strategies_once()
    {
        var db = NewDb();
        // An account made before 10 Oct, with no grants.
        db.AppUsers.Add(new AppUser { UserName = AiTraderAccount.UserName, Email = "ai-trader@localhost", Role = UserRoles.Trader });
        await db.SaveChangesAsync();

        var user = await AiTraderAccount.EnsureAsync(db, default);
        await AiTraderAccount.EnsureAsync(db, default);

        Assert.Equal(PlatformModules.Strategies, Assert.Single(db.UserModuleGrants.AsNoTracking().Where(g => g.UserId == user.Id)).ModuleKey);
        Assert.Equal(new[] { "ChainFlowBuy", "GhostTangentCrossings" },
            db.UserStrategyGrants.AsNoTracking().Where(g => g.UserId == user.Id).Select(g => g.StrategyName).OrderBy(n => n).ToList());
        var access = await new StrategyAccessService(db, new NoLimits()).CanDeployAsync(user.Id, "ChainFlowBuy", "NIFTY", 1, "LivePaper", 1);
        Assert.True(access.Allowed);
        Assert.False((await new StrategyAccessService(db, new NoLimits()).CanDeployAsync(user.Id, "Ghost", "NIFTY", 1, "LivePaper", 1)).Allowed);
    }

    // ---------- harness ----------

    /// <summary>The question a model was asked: the last message of the request, as sent.</summary>
    private static string Asked(Seen request) =>
        JsonNode.Parse(request.Body)!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();

    public void Dispose()
    {
    }

    /// <summary>
    /// The AI Trader on one in-memory database, live: its own account's book read as the rules read it, the order ticket's
    /// own placement and the paper fills, the Validator and the Risk Memo on the scripted provider, a clock a minute after
    /// the look. NIFTY's ATM call (the brief's ask 120) is quoted at 119.50 / 120.50.
    /// </summary>
    private sealed class Trader : IDisposable
    {
        private static readonly IMarketSessionService Sessions = new MarketSessionService(new AlwaysOpen());

        public Trader(bool killSwitch = false)
        {
            // The paper fills run in a transaction, which the in-memory store only warns about.
            Ai = Build(Settings(), new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options));
            Ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null).GetAwaiter().GetResult();
            Reader = AiTraderBookReaderTests.Reader(Ai.Db, killSwitch);
            Db.Instruments.Add(new Instrument
            {
                Symbol = Call, Exchange = "NSE", Segment = "FO", InstrumentType = "CE", OptionType = "CE", Underlying = "NIFTY", StrikePrice = 22650m,
                TickSize = 0.05m, IsEnabled = true, ExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30),
            });
            Db.SaveChanges();
            Quote(Call, bid: 119.5m, ask: 120.5m);
        }

        public Services Ai { get; }

        public TradingDbContext Db => Ai.Db;

        public AiTraderBookReader Reader { get; }

        public TimeProvider Clock { get; } = new AiAgentsTests.FixedTime(Now);

        public void Quote(string symbol, decimal bid, decimal ask, DateTime? updatedUtc = null)
        {
            var row = Db.LiveQuotesLatest.FirstOrDefault(q => q.Symbol == symbol);
            if (row is null) Db.LiveQuotesLatest.Add(row = new LiveQuoteLatest { Symbol = symbol });
            row.BidPrice = bid;
            row.AskPrice = ask;
            row.LastTradedPrice = (bid + ask) / 2;
            row.UpdatedUtc = updatedUtc ?? Eleven;
            Db.SaveChanges();
        }

        public AiTraderAgent Agent(IAiTraderBooks? books = null, IAiTraderRuns? runs = null)
        {
            var lots = new PositionGreeksTests.FixedLots(65);
            var paper = new PaperTradingService(Db, RecapClockTests.Inert<IRiskManagementService>.Create(), lots, time: Clock);
            var reports = new AiReportWriter(Db, Ai.Options, Clock);
            var execution = new AiTraderExecution(Db, new ManualOrderService(Db, paper, lots, NullLogger.Instance), paper, runs ?? new FakeRuns(),
                new AiTraderValidator(Ai.Gateway, reports, NullLogger<AiTraderValidator>.Instance),
                new AiTraderRiskMemo(Ai.Gateway, reports, NullLogger<AiTraderRiskMemo>.Instance), Clock);
            return new AiTraderAgent(Db, Ai.Gateway, new AiTraderAgentTests.FakeBriefs(), books ?? Reader,
                new AiTraderShadowBook(Db, new AiTraderAgentTests.FakeQuotes(), Sessions), Sessions, new NoReplay(), Ai.Options,
                NullLogger<AiTraderAgent>.Instance, null, Clock, null, execution);
        }

        public AiTraderExecutionReporter Reporter() => new(Db, Ai.Gateway, new AiReportWriter(Db, Ai.Options, Clock), new AiSchedulerState(),
            new PositionGreeksTests.FixedLots(65), NullLogger<AiTraderExecutionReporter>.Instance, Clock);

        /// <summary>Another trader's manual book; its run id.</summary>
        public async Task<long> OtherTradersBookAsync()
        {
            var user = new AppUser { UserName = "coderforchange", Email = "c@localhost", Role = UserRoles.Trader };
            Db.AppUsers.Add(user);
            await Db.SaveChangesAsync();
            var book = ManualBook.NewBook(user.Id, Eleven.AddHours(-2));
            Db.SimulationRuns.Add(book);
            await Db.SaveChangesAsync();
            return book.Id;
        }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>Starts and stops recorded, as the desk would answer them.</summary>
    private sealed class FakeRuns : IAiTraderRuns
    {
        public List<(string Account, string Strategy, string Underlying, int Lots)> Started { get; } = [];

        public List<(string Account, long RunId)> Stopped { get; } = [];

        public string? Refusal { get; init; }

        public Task<AiTraderRunStart> StartAsync(AppUser account, string strategy, string underlying, int lots, CancellationToken cancellationToken)
        {
            if (Refusal is not null) return Task.FromResult(new AiTraderRunStart(false, null, Refusal));
            Started.Add((account.UserName, strategy, underlying, lots));
            return Task.FromResult(new AiTraderRunStart(true, 77, $"Started {strategy} on {underlying} (paper)."));
        }

        public Task<AiTraderRunStop> StopAsync(AppUser account, long runId, string reason, CancellationToken cancellationToken)
        {
            Stopped.Add((account.UserName, runId));
            return Task.FromResult(new AiTraderRunStop(true, 1, $"Stopped run {runId}; squared off 1 position(s)."));
        }
    }

    /// <summary>The same account every read.</summary>
    private sealed class FixedBooks(AiTraderBook book) : IAiTraderBooks
    {
        public Task<AiTraderBook> ReadAsync(DateTime clockUtc, bool replay, CancellationToken cancellationToken) => Task.FromResult(book with { ClockUtc = clockUtc });

        public Task<AiTraderLiveDay> DayAsync(DateTime clockUtc, CancellationToken cancellationToken) => Task.FromResult(AiTraderLiveDay.Empty);
    }

    /// <summary>The real account for the first <paramref name="after"/> reads, then the same with <paramref name="opened"/> open besides.</summary>
    private sealed class ChangingBooks(IAiTraderBooks real, int after, IReadOnlyList<AiTraderOpenPosition> opened) : IAiTraderBooks
    {
        private int _reads;

        public async Task<AiTraderBook> ReadAsync(DateTime clockUtc, bool replay, CancellationToken cancellationToken)
        {
            var book = await real.ReadAsync(clockUtc, replay, cancellationToken);
            return ++_reads <= after ? book : book with { Open = [.. book.Open, .. opened] };
        }

        public Task<AiTraderLiveDay> DayAsync(DateTime clockUtc, CancellationToken cancellationToken) => real.DayAsync(clockUtc, cancellationToken);
    }

    private sealed class NoReplay : IReplaySessions
    {
        public Task<ReplaySessionState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult<ReplaySessionState?>(null);
    }

    private sealed class NoLimits : IRiskLimitsStore
    {
        public AlgoTrading.Contracts.Risk.RiskLimitsDto GetLimits() => new();

        public Task UpdateLimitsAsync(AlgoTrading.Contracts.Risk.RiskLimitsDto newLimits, string updatedBy, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlwaysOpen : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public bool IsLoaded => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
