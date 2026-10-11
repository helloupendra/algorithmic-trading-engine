using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The checks' dry run (11 Oct 2026, the day before the AI Trader's first live paper orders): the Validator and the Risk
/// Memo asked on past plans as a live placement asks them, every answer shown beside what the check made of it, and
/// nothing written to the desk. Every model here is the scripted provider: nothing reaches NVIDIA or AWS.
/// </summary>
public sealed class AiTraderChecksDryRunTests
{
    // Friday 9 Oct 2026, 11:00 IST: the look rehearsed. Sunday noon: the rehearsal.
    private static readonly DateTime Eleven = IstTime.FromIst(new DateTime(2026, 10, 9, 11, 0, 0));
    private static readonly DateTime Sunday = IstTime.FromIst(new DateTime(2026, 10, 11, 12, 0, 0));

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private const string Brief = "MARKET BRIEF 11:00 IST\nNIFTY 22,612 above EMA20 and EMA50; call OI falling at the 22,700 call wall.";

    private static readonly AiTraderPlan BuyPlan = new(AiTraderPlan.Buy, "NIFTY", "CE", "ATM", 1, 90m, 160m, null, null, null,
        "NIFTY above EMA20 and EMA50 with call OI falling at the call wall.", 0.55);

    private static readonly AiTraderContract Contract = new("NSE:NIFTY26O1322650CE", "NIFTY", "CE", 22650m, new DateOnly(2026, 10, 13), 120m, 65);

    private const string Accept = """{"verdict":"ACCEPT","flaw":"The trend is young.","quote":"","confidence":0.6}""";

    private const string Reject = """
        {"verdict":"REJECT","flaw":"It buys a call with the index pressed against its EMAs and nothing else behind it.",
         "quote":"NIFTY 22,612 above EMA20 and EMA50","confidence":0.7}
        """;

    private const string MemoTighterStop = """
        {"memo":"At its stop it loses about ₹1,950, a fifth of the day's budget.","lots":null,"stopLoss":95,"target":null,
         "why":"A closer stop keeps a loss near ₹1,625."}
        """;

    // ---------- what it asks and what it shows ----------

    [Fact]
    public async Task It_asks_the_validator_three_times_and_the_memo_once_and_shows_each_answer_beside_what_the_check_made_of_it()
    {
        var desk = new Desk();
        var row = desk.Decide();
        desk.Ai.Provider.On(Judge1, Answer(Accept), Answer(Reject), Answer(Accept), Answer(MemoTighterStop));

        var result = await desk.DryRun().RunAsync([row.Id], null, null, "upendra", 1, default);

        var d = Assert.Single(result.Decisions);
        Assert.Equal((row.Id, "2026-10-09", "11:00", "NIFTY", AiTraderPlan.Buy, null), (d.DecisionId, d.Day, d.ClockIst, d.Underlying, d.Action, d.Skipped));
        Assert.Equal(Contract, d.Contract);
        var v = d.Validator!;
        Assert.Equal((AiTraderChecksDryRun.Placed, 2, 1, 0), (v.Outcome, v.Accepts, v.Rejects, v.NoVerdict));
        Assert.Equal(new[] { "ACCEPT", "REJECT", "ACCEPT" }, v.Answers.Select(a => a.Verdict));
        Assert.All(v.Answers, a => Assert.Equal((Judge1, true, (string?)null), (a.Model, a.Counted, a.Excerpt)));
        Assert.Equal((true, "NIFTY 22,612 above EMA20 and EMA50"), (v.Answers[1].QuoteFound, v.Answers[1].Quote));
        Assert.StartsWith("It buys a call", v.Answers[1].Flaw);
        Assert.Equal(new[] { 1, 2, 3 }, v.Answers.Select(a => a.Ask));
        Assert.All(v.Answers, a => Assert.NotNull(a.CallId));

        var m = d.RiskMemo!;
        Assert.Equal((true, true, AiReportStatus.Ok, Judge1), (m.AskedLive, m.Parsed, m.Status, m.Model));
        Assert.Equal(95d, m.Proposed!.StopLoss);
        Assert.Null(m.Proposed.Lots);
        Assert.Equal(["stop 90 → 95"], m.Applied);
        Assert.Equal((1, 95m, 160m), (m.Lots, m.StopLoss, m.Target));
        Assert.StartsWith("At its stop it loses", m.Memo);

        var s = result.Summary;
        Assert.Equal((1, 1, 0), (s.Decisions, s.Rehearsed, s.Skipped));
        Assert.Equal((3, 3, 1.0, 3, 1.0), (s.ValidatorAnswers, s.ValidatorParsed, s.ValidatorParsedShare, s.ValidatorCounted, s.ValidatorCountedShare));
        Assert.Equal((1, 1.0, 0, 0), (s.WouldPlace, s.WouldPlaceShare, s.BlockedByValidator, s.ValidatorUnavailable));
        Assert.Equal((1, 1, 1.0), (s.RiskMemos, s.RiskMemosParsed, s.RiskMemoParsedShare));
        var model = Assert.Single(s.Models);
        Assert.Equal((Judge1, 3, 3, 1, 1, 0), (model.Model, model.ValidatorAnswers, model.ValidatorParsed, model.RiskMemos, model.RiskMemosParsed, model.Failures));
        // NVIDIA's free tier has no price.
        Assert.Null(s.CostUsd);
    }

    [Fact]
    public async Task Answers_that_cannot_be_read_are_counted_as_unreadable_with_their_text()
    {
        string madeUp = Reject.Replace("NIFTY 22,612 above EMA20 and EMA50", "the RBI cut rates at noon");
        var desk = new Desk();
        var row = desk.Decide();
        desk.Ai.Provider.On(Judge1, Answer("I would accept this plan."), Answer(madeUp), Answer("""{"verdict":"MAYBE","flaw":"Unsure."}"""),
            Answer("Tighten the stop to 95."));

        var result = await desk.DryRun().RunAsync([row.Id], null, null, "upendra", 1, default);

        var v = Assert.Single(result.Decisions).Validator!;
        Assert.Equal(new[] { AiTraderChecksDryRun.Unreadable, "REJECT", AiTraderChecksDryRun.Unreadable }, v.Answers.Select(a => a.Verdict));
        Assert.All(v.Answers, a => Assert.False(a.Counted));
        Assert.Equal("I would accept this plan.", v.Answers[0].Excerpt);
        Assert.Contains("not the JSON object asked for", v.Answers[0].Error);
        // A REJECT that reads, but quotes words the Validator was never given: not counted.
        Assert.Equal((false, "the RBI cut rates at noon"), (v.Answers[1].QuoteFound, v.Answers[1].Quote));
        Assert.Contains("quote is not in the brief", v.Answers[1].Error);
        Assert.Contains("MAYBE", v.Answers[2].Excerpt);
        // No majority: live, nothing would be placed.
        Assert.Equal((AiTraderValidator.UnavailableRule, 3), (v.Outcome, v.NoVerdict));

        var m = Assert.Single(result.Decisions).RiskMemo!;
        Assert.Equal((false, false, AiReportStatus.Invalid), (m.AskedLive, m.Parsed, m.Status));
        Assert.Equal("Tighten the stop to 95.", m.Excerpt);
        Assert.Null(m.Proposed);
        Assert.Equal((90m, 160m), (m.StopLoss, m.Target));

        var s = result.Summary;
        Assert.Equal((3, 1, 0.333, 0, 0.0), (s.ValidatorAnswers, s.ValidatorParsed, s.ValidatorParsedShare, s.ValidatorCounted, s.ValidatorCountedShare));
        Assert.Equal((0, 0.0, 1), (s.WouldPlace, s.WouldPlaceShare, s.ValidatorUnavailable));
        Assert.Equal((1, 0, 0.0), (s.RiskMemos, s.RiskMemosParsed, s.RiskMemoParsedShare));
    }

    [Fact]
    public async Task A_model_that_does_not_answer_is_no_answer_and_its_failures_are_counted_on_it()
    {
        var desk = new Desk();
        var row = desk.Decide();
        // The tier's first judge has nothing to say (it answers 500); the second answers each ask.
        desk.Ai.Provider.On(Judge2, Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoTighterStop));

        var result = await desk.DryRun().RunAsync([row.Id], null, null, "upendra", 1, default);

        var v = Assert.Single(result.Decisions).Validator!;
        Assert.All(v.Answers, a => Assert.Equal(Judge2, a.Model));
        Assert.Contains(v.Answers[0].Attempts, t => t.Model == Judge1 && t.Outcome != "ok");
        var judge1 = Assert.Single(result.Summary.Models, x => x.Model == Judge1);
        Assert.Equal(0, judge1.ValidatorAnswers);
        Assert.True(judge1.Failures >= 1);
        var judge2 = Assert.Single(result.Summary.Models, x => x.Model == Judge2);
        Assert.Equal((3, 1, 0), (judge2.ValidatorAnswers, judge2.RiskMemos, judge2.Failures));
        Assert.Equal(AiTraderChecksDryRun.Placed, v.Outcome);
    }

    [Fact]
    public async Task A_start_is_asked_on_the_lots_a_live_start_takes()
    {
        var desk = new Desk();
        var start = new AiTraderPlan(AiTraderPlan.StartStrategy, "NIFTY", null, null, 5, null, null, "ChainFlowBuy", null, null, "Flow is one-sided.", 0.5);
        var row = desk.Decide(AiTraderPlan.StartStrategy, plan: start);
        desk.Ai.Provider.On(Judge1, Answer(Accept), Answer(Accept), Answer(Accept), Answer("""{"memo":"One lot is the most it may risk.","lots":1,"why":""}"""));

        var result = await desk.DryRun().RunAsync([row.Id], null, null, "upendra", 1, default);

        var d = Assert.Single(result.Decisions);
        Assert.Equal((1, (AiTraderContract?)null), (d.Plan!.Lots, d.Contract));
        Assert.Equal(AiTraderAgent.ForChecks(start), d.Plan);
        Assert.Contains("\"lots\":1", Asked(desk.Ai.Provider.Requests[0]));
        Assert.Equal(AiTraderChecksDryRun.Placed, d.Validator!.Outcome);
    }

    [Fact]
    public async Task Decisions_the_checks_are_never_asked_on_are_skipped_with_the_reason_and_ask_no_model()
    {
        var desk = new Desk();
        var refused = desk.Decide(allowed: false, rule: "premium");
        var exit = desk.Decide(AiTraderPlan.Exit);

        var result = await desk.DryRun().RunAsync([refused.Id, exit.Id, 999], null, null, "upendra", 1, default);

        Assert.Equal(new long[] { refused.Id, exit.Id, 999 }, result.Decisions.Select(d => d.DecisionId));
        Assert.Contains("did not allow it (rule premium)", result.Decisions[0].Skipped);
        Assert.Contains("only before a buy or a strategy start", result.Decisions[1].Skipped);
        Assert.Equal("No decision 999.", result.Decisions[2].Skipped);
        Assert.All(result.Decisions, d => Assert.Null(d.Validator));
        Assert.Equal((3, 0, 3), (result.Summary.Decisions, result.Summary.Rehearsed, result.Summary.Skipped));
        Assert.Empty(desk.Ai.Provider.Requests);
        Assert.Empty(desk.Db.AiCalls);
    }

    // ---------- the same as live, and nothing written ----------

    [Fact]
    public async Task It_sends_the_models_exactly_what_a_live_check_sends()
    {
        var desk = new Desk();
        var row = desk.Decide();
        // A correction kept for the Validator: its live call reads it, and so must the rehearsal.
        var note = desk.Remember(AiCatalog.Validator, "A REJECT needs the words of the brief itself.");
        desk.Ai.Provider.On(Judge1, Answer(Accept), Answer(Reject), Answer(Accept), Answer(MemoTighterStop));
        desk.Ai.Provider.On(Judge1, Answer(Accept), Answer(Reject), Answer(Accept), Answer(MemoTighterStop));

        // Live: the Validator and the Risk Memo as a live placement asks them.
        var verdict = new AiTraderVerdict(true, "ok", row.Why);
        var live = await desk.Validator().JudgeAsync(row, BuyPlan, Contract, verdict, default);
        await desk.RiskMemo().ReviewAsync(row, BuyPlan, Contract, verdict, desk.Book, AiTraderAgent.Rules, default);
        var rehearsed = await desk.DryRun().RunAsync([row.Id], null, null, "upendra", 1, default);

        var asks = desk.Ai.Provider.Requests.Select(r => r.Body).ToList();
        Assert.Equal(8, asks.Count);
        Assert.Equal(asks.Take(4), asks.Skip(4));
        Assert.Contains("A REJECT needs the words of the brief itself.", asks[0]);
        Assert.Equal(live.Outcome == AiTraderValidation.Accepted ? AiTraderChecksDryRun.Placed : live.Rule, Assert.Single(rehearsed.Decisions).Validator!.Outcome);

        // Live counts the memory's use and writes its reports; the rehearsal does neither.
        Assert.Equal(3, desk.Db.AiMemories.AsNoTracking().Single(m => m.Id == note.Id).Uses);
        Assert.Equal(new[] { AiCatalog.RiskMemo, AiCatalog.Validator }, desk.Db.AiReports.AsNoTracking().Select(r => r.AgentKey).OrderBy(k => k));
        var calls = desk.Db.AiCalls.AsNoTracking().OrderBy(c => c.Id).ToList();
        Assert.Equal(8, calls.Count);
        Assert.All(calls.Take(4), c => Assert.Equal("schedule", c.Source));
        Assert.All(calls.Skip(4), c => Assert.Equal((AiTraderChecksRehearsal.Source, "upendra", 1L), (c.Source, c.RequestedBy, c.UserId)));
        Assert.All(calls.Skip(4), c => Assert.StartsWith("dry-run-", c.ConversationId));
        // The Validator's three asks read its memory, as its live asks did; the memo has none of its own.
        string[] read = [$"[{note.Id}]", $"[{note.Id}]", $"[{note.Id}]", "[]"];
        Assert.Equal([.. read, .. read], calls.Select(c => c.MemoryIdsJson));
        // The prompts on the call rows are the same too.
        Assert.Equal(calls.Take(4).Select(c => (c.SystemPrompt, c.MessagesJson, c.Temperature, c.MaxTokens)),
            calls.Skip(4).Select(c => (c.SystemPrompt, c.MessagesJson, c.Temperature, c.MaxTokens)));
    }

    [Fact]
    public async Task Nothing_is_written_to_the_desk_and_no_decision_changes()
    {
        var desk = new Desk();
        var buy = desk.Decide();
        var start = desk.Decide(AiTraderPlan.StartStrategy,
            plan: new AiTraderPlan(AiTraderPlan.StartStrategy, "NIFTY", null, null, 1, null, null, "ChainFlowBuy", null, null, "Flow.", 0.5));
        var before = desk.Db.AiTraderDecisions.AsNoTracking().OrderBy(d => d.Id).Select(d => JsonSerializer.Serialize(d, Web)).ToList();
        desk.Ai.Provider.On(Judge1, Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoTighterStop),
            Answer(Accept), Answer(Accept), Answer(Accept), Answer("""{"memo":"Fine.","lots":null,"why":""}"""));

        var result = await desk.DryRun().RunAsync(null, 5, null, "upendra", 1, default);

        // Both would be placed live; here nothing is.
        Assert.Equal(new[] { start.Id, buy.Id }, result.Decisions.Select(d => d.DecisionId));
        Assert.All(result.Decisions, d => Assert.Equal(AiTraderChecksDryRun.Placed, d.Validator!.Outcome));
        Assert.Empty(desk.Db.PaperOrders);
        Assert.Empty(desk.Db.PaperPositions);
        Assert.Empty(desk.Db.SimulationRuns);
        Assert.Empty(desk.Db.AiTraderShadowPositions);
        Assert.Empty(desk.Db.AiReports);
        Assert.Equal(before, desk.Db.AiTraderDecisions.AsNoTracking().OrderBy(d => d.Id).Select(d => JsonSerializer.Serialize(d, Web)).ToList());
        // Its model calls are the only rows, marked as a dry run.
        Assert.Equal(8, desk.Db.AiCalls.Count());
        Assert.All(desk.Db.AiCalls.AsNoTracking(), c => Assert.Equal(AiTraderChecksRehearsal.Source, c.Source));
        Assert.Contains("nothing was placed or recorded", result.Note);
    }

    // ---------- the endpoint ----------

    [Fact]
    public async Task A_chain_given_is_the_chain_asked_in_place_of_the_judge_tiers()
    {
        var desk = new Desk();
        var row = desk.Decide();
        // Only the forced model is scripted: an ask of the tier's first model would answer 500 and show here.
        desk.Ai.Provider.On(Judge3, Answer(Accept), Answer(Accept), Answer(Accept), Answer(MemoTighterStop));

        var ok = Assert.IsType<OkObjectResult>(await desk.Controller().ChecksDryRun(
            new AiTraderChecksDryRunRequest([row.Id], null, [Judge3]), desk.DryRun(), desk.Ai.Catalog, default));

        var result = Assert.IsType<AiTraderChecksDryRunResult>(ok.Value);
        Assert.Equal([Judge3], result.Chain);
        Assert.All(result.Decisions[0].Validator!.Answers, a => Assert.Equal(Judge3, a.Model));
        Assert.Equal(Judge3, result.Decisions[0].RiskMemo!.Model);
        // The chain is checked against the model list (a read with no model); every question went to the forced model.
        Assert.All(desk.Ai.Provider.Requests.Where(r => r.Model.Length > 0), r => Assert.Equal(Judge3, r.Model));
        Assert.Equal(4, desk.Ai.Provider.Requests.Count(r => r.Model == Judge3));
        Assert.All(desk.Db.AiCalls.AsNoTracking(), c => Assert.Equal($"[\"{Judge3}\"]", c.ChainJson));
    }

    [Fact]
    public async Task A_chain_is_checked_as_a_tiers_chain_is()
    {
        var desk = new Desk();
        var row = desk.Decide();

        var bad = Assert.IsType<BadRequestObjectResult>(await desk.Controller().ChecksDryRun(
            new AiTraderChecksDryRunRequest([row.Id], null, ["nobody/no-such-model"]), desk.DryRun(), desk.Ai.Catalog, default));

        Assert.Equal("nobody/no-such-model is not in the provider's model list.", Error(bad));
        Assert.DoesNotContain(desk.Ai.Provider.Requests, r => r.Model.Length > 0);
    }

    [Fact]
    public async Task At_most_twenty_decisions_are_rehearsed()
    {
        // Both checks switched off: each decision is two quick refusals, as live would meet them.
        var desk = new Desk();
        await desk.Ai.Store.SetAgentEnabledAsync(AiCatalog.Validator, false, "upendra", "testing");
        await desk.Ai.Store.SetAgentEnabledAsync(AiCatalog.RiskMemo, false, "upendra", "testing");
        var rows = Enumerable.Range(0, 22).Select(_ => desk.Decide()).ToList();

        var tooMany = Assert.IsType<BadRequestObjectResult>(await desk.Controller().ChecksDryRun(
            new AiTraderChecksDryRunRequest(rows.Take(21).Select(r => r.Id).ToList(), null, null), desk.DryRun(), desk.Ai.Catalog, default));
        Assert.StartsWith("At most 20 decisions", Error(tooMany));
        Assert.Empty(desk.Db.AiCalls);

        var ok = Assert.IsType<OkObjectResult>(await desk.Controller().ChecksDryRun(
            new AiTraderChecksDryRunRequest(null, 25, null), desk.DryRun(), desk.Ai.Catalog, default));
        var result = Assert.IsType<AiTraderChecksDryRunResult>(ok.Value);
        Assert.Equal((25, true, 20), (result.Requested, result.Capped, result.Decisions.Count));
        // The newest twenty.
        Assert.Equal(rows.Skip(2).Select(r => r.Id).Reverse(), result.Decisions.Select(d => d.DecisionId));
        Assert.Contains("capped at 20", result.Note);
        // Switched off, as live: one ask, no verdict, never placed.
        var first = result.Decisions[0].Validator!;
        Assert.Equal(AiTraderValidator.UnavailableRule, first.Outcome);
        Assert.Contains("switched off", Assert.Single(first.Answers).Error);
        Assert.Equal(AiTraderChecksDryRun.NoAnswer, first.Answers[0].Verdict);
    }

    [Theory]
    [InlineData(true, true, null, "not both")]
    [InlineData(false, false, null, "Give decisionIds")]
    [InlineData(false, true, 0, "take is 1 to 20")]
    public void A_request_must_name_its_decisions_one_way(bool ids, bool take, int? takeValue, string error) =>
        Assert.Contains(error, AiTraderChecksDryRun.Problem(ids ? [1, 2] : null, take ? takeValue ?? 3 : null));

    [Fact]
    public void The_dry_run_is_admin_only()
    {
        var policy = Assert.Single(typeof(AiTraderController).GetCustomAttributes<AuthorizeAttribute>(true)).Policy;
        Assert.Equal(AuthorizationPolicies.AdminOnly, policy);
        var action = typeof(AiTraderController).GetMethod(nameof(AiTraderController.ChecksDryRun))!;
        Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(true));
        Assert.Empty(action.GetCustomAttributes<AuthorizeAttribute>(true));
        Assert.Equal("checks/dry-run", action.GetCustomAttribute<HttpPostAttribute>()!.Template);
    }

    // ---------- the rate limit it paces itself by ----------

    [Fact]
    public void The_rate_limit_says_how_long_until_a_users_next_call()
    {
        var clock = new MovingTime(Sunday);
        var limiter = new AiRateLimiter(new AiTestKit.Monitor(Settings(s => s.PerUserPer10Min = 2)), clock);
        limiter.TryAcquire("upendra").Lease!.Dispose();
        clock.Now = Sunday.AddMinutes(1);
        limiter.TryAcquire("upendra").Lease!.Dispose();

        clock.Now = Sunday.AddMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(8), limiter.WaitFor("upendra"));
        Assert.Equal(TimeSpan.Zero, limiter.WaitFor("someone-else"));
        clock.Now = Sunday.AddMinutes(10);
        Assert.Equal(TimeSpan.Zero, limiter.WaitFor("upendra"));
        Assert.NotNull(limiter.TryAcquire("upendra").Lease);
    }

    // ---------- harness ----------

    private static string? Error(BadRequestObjectResult bad) => bad.Value!.GetType().GetProperty("error")!.GetValue(bad.Value) as string;

    private static string Asked(Seen request) =>
        System.Text.Json.Nodes.JsonNode.Parse(request.Body)!["messages"]!.AsArray().Last()!["content"]!.GetValue<string>();

    /// <summary>The AI services on the scripted provider, one in-memory database, the clock on Sunday noon, an empty account.</summary>
    private sealed class Desk
    {
        public Services Ai { get; } = Build(Settings());

        public TradingDbContext Db => Ai.Db;

        public TimeProvider Clock { get; } = new AiAgentsTests.FixedTime(Sunday);

        public AiTraderBook Book { get; } = new(Sunday, false, false, 0m, 0, [], []);

        public AiTraderValidator Validator() => new(Ai.Gateway, new AiReportWriter(Db, Ai.Options, Clock), NullLogger<AiTraderValidator>.Instance);

        public AiTraderRiskMemo RiskMemo() => new(Ai.Gateway, new AiReportWriter(Db, Ai.Options, Clock), NullLogger<AiTraderRiskMemo>.Instance);

        public AiTraderChecksDryRun DryRun() =>
            new(Db, Validator(), RiskMemo(), new FixedBooks(Book), Ai.Limiter, NullLogger<AiTraderChecksDryRun>.Instance, Clock);

        public AiTraderController Controller() => new(Db, Ai.Store, Ai.Options, Clock)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "upendra"), new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin")], "test")),
                },
            },
        };

        /// <summary>A look of Friday 11:00 that the rules allowed: a buy with its contract kept, or a start.</summary>
        public AiTraderDecision Decide(string action = AiTraderPlan.Buy, bool allowed = true, string rule = "ok", AiTraderPlan? plan = null)
        {
            plan ??= action == AiTraderPlan.Buy ? BuyPlan : BuyPlan with { Action = action };
            var row = new AiTraderDecision
            {
                CreatedUtc = Eleven, ClockUtc = Eleven, Day = IstTime.DateOf(Eleven), Mode = AiTraderModes.Shadow, BriefHash = "hash", Brief = Brief,
                Model = Judge1, Action = action, Underlying = "NIFTY", PlanJson = JsonSerializer.Serialize(plan, Web), Reason = plan.Reason,
                Confidence = plan.Confidence, Allowed = allowed, Rule = rule, Why = allowed ? "Within every limit." : "Over the premium cap.",
                ResultJson = action == AiTraderPlan.Buy
                    ? JsonSerializer.Serialize(new { contract = Contract, shadowPositionId = 7 }, Web)
                    : """{"note":"Strategy runs are not simulated in shadow: recorded only."}""",
            };
            Db.AiTraderDecisions.Add(row);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            return row;
        }

        public AiMemory Remember(string agent, string text)
        {
            var m = new AiMemory
            {
                AgentKey = agent, Kind = AiMemoryKind.Correction, Status = AiMemoryStatus.Active, Text = text, Source = AiMemorySource.Owner,
                CreatedBy = "upendra", CreatedUtc = Sunday, UpdatedUtc = Sunday, ActivatedUtc = Sunday,
            };
            Db.AiMemories.Add(m);
            Db.SaveChanges();
            return m;
        }
    }

    /// <summary>The same account every read.</summary>
    private sealed class FixedBooks(AiTraderBook book) : IAiTraderBooks
    {
        public Task<AiTraderBook> ReadAsync(DateTime clockUtc, bool replay, CancellationToken cancellationToken) => Task.FromResult(book with { ClockUtc = clockUtc });

        public Task<AiTraderLiveDay> DayAsync(DateTime clockUtc, CancellationToken cancellationToken) => Task.FromResult(AiTraderLiveDay.Empty);
    }

    private sealed class MovingTime(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }
}
