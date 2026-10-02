using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The tools each agent reads its own work with when the owner talks with it: the AI Trader's decisions, shadow book,
/// scoreboard, lessons and a look now that saves nothing; the Trade Reviewer's reviews; the News Analyst's records with
/// the text it read; the Incident Explainer's incidents and explanations. Each answer is read as the model gets it.
/// </summary>
public sealed class AiAgentChatToolTests
{
    // Monday 5 Oct 2026, 11:00 IST.
    private static readonly DateTime Eleven = IstTime.FromIst(new DateTime(2026, 10, 5, 11, 0, 0));
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const string BuyAtmCall = """
        {"action":"buy","underlying":"NIFTY","option":"CE","strike":"ATM","lots":1,"stopLoss":90,"target":160,
         "reason":"NIFTY above EMA20 and EMA50 with call OI falling at the call wall.","confidence":0.55}
        """;

    // ---------- the AI Trader ----------

    [Fact]
    public async Task Its_decisions_of_a_day_list_what_it_proposed_and_the_verdict_with_the_days_totals()
    {
        using var db = NewDb();
        var buy = Decision(db, Eleven, AiTraderPlan.Buy, allowed: true, rule: "ok", plan: BuyAtmCall);
        Decision(db, Eleven.AddMinutes(10), AiTraderPlan.None, allowed: true, rule: "ok", plan: """{"action":"none","reason":"Waiting."}""");
        Decision(db, Eleven.AddMinutes(20), AiTraderPlan.Buy, allowed: false, rule: "stop", plan: BuyAtmCall.Replace("\"stopLoss\":90", "\"stopLoss\":40"));
        Decision(db, Eleven.AddMinutes(30), string.Empty, allowed: false, rule: "no-answer");
        Decision(db, Eleven.AddDays(-1), AiTraderPlan.Buy, allowed: true, rule: "ok", plan: BuyAtmCall);   // another day
        Decision(db, Eleven, AiTraderPlan.Buy, allowed: true, rule: "ok", plan: BuyAtmCall, replay: 12);   // a replay of the same day

        var json = await Run(new AiTraderDecisionsTool(db, new AiAgentsTests.FixedTime(Eleven)), """{"day":"2026-10-05","action":"buy"}""");

        var listed = json.GetProperty("decisions").EnumerateArray().ToList();
        Assert.Equal(3, listed.Count);
        var first = listed.Single(d => d.GetProperty("decisionId").GetInt64() == buy.Id);
        Assert.Equal(("2026-10-05 11:00", "CE", "ATM", 90m, "allowed, opened in the shadow book"),
            (first.GetProperty("at").GetString(), first.GetProperty("option").GetString(), first.GetProperty("strike").GetString(),
             first.GetProperty("stopLoss").GetDecimal(), first.GetProperty("verdict").GetString()));
        Assert.Contains("call wall", first.GetProperty("reason").GetString());
        Assert.Contains(listed, d => d.TryGetProperty("replay", out var r) && r.GetInt64() == 12);
        var totals = json.GetProperty("totals");
        Assert.Equal((5, 3, 2, 1, 1), (totals.GetProperty("looks").GetInt32(), totals.GetProperty("acted").GetInt32(), totals.GetProperty("allowed").GetInt32(),
            totals.GetProperty("refused").GetInt32(), totals.GetProperty("noAnswer").GetInt32()));
    }

    [Fact]
    public async Task One_decision_carries_the_brief_it_read_the_memories_it_was_given_and_its_shadow_position()
    {
        using var db = NewDb();
        var lesson = new AiMemory { AgentKey = AiCatalog.AiTrader, Kind = AiMemoryKind.Lesson, Status = AiMemoryStatus.Retired, Text = "Wait for the first half hour." };
        db.AiMemories.Add(lesson);
        db.SaveChanges();
        var call = new AiCall { AgentKey = AiCatalog.AiTrader, Outcome = AiCallOutcome.Ok, MemoryIdsJson = $"[{lesson.Id}]" };
        db.AiCalls.Add(call);
        db.SaveChanges();
        var d = Decision(db, Eleven, AiTraderPlan.Buy, allowed: true, rule: "ok", plan: BuyAtmCall, callId: call.Id,
            brief: "MARKET BRIEF — Mon 5 Oct 2026, 11:00 IST\nNIFTY 22,612 above EMA20 and EMA50.");
        db.AiTraderShadowPositions.Add(new AiTraderShadowPosition
        {
            DecisionId = d.Id, Day = Monday, Symbol = "NSE:NIFTY26O0622650CE", Underlying = "NIFTY", OptionType = "CE", Strike = 22650m,
            Expiry = new DateOnly(2026, 10, 6), Lots = 1, LotSize = 65, EntryUtc = Eleven, EntryPrice = 120m, StopLoss = 90m, Target = 160m,
            ExitUtc = Eleven.AddMinutes(40), ExitPrice = 88m, ExitReason = "stop", Charges = 62m, NetPnl = -2142m,
        });
        db.SaveChanges();

        var json = await Run(new AiTraderDecisionTool(db), $$"""{"decisionId":{{d.Id}}}""");

        Assert.Contains("NIFTY 22,612 above EMA20", json.GetProperty("brief").GetString());
        Assert.Equal("buy", json.GetProperty("plan").GetProperty("action").GetString());
        var memory = json.GetProperty("memoriesGiven").EnumerateArray().Single();
        // Given to it then, though retired now: a decision explains itself by what it read.
        Assert.Equal((lesson.Id, "retired", "Wait for the first half hour."),
            (memory.GetProperty("memoryId").GetInt64(), memory.GetProperty("statusNow").GetString(), memory.GetProperty("text").GetString()));
        var position = json.GetProperty("shadowPositions").EnumerateArray().Single();
        Assert.Equal(("stop", -2142m), (position.GetProperty("ended").GetString(), position.GetProperty("netPnl").GetDecimal()));

        var ex = await Assert.ThrowsAsync<AiToolArgumentException>(() => new AiTraderDecisionTool(db).RunAsync(AiToolArgs.Parse("""{"decisionId":999}"""), default));
        Assert.Contains("get_ai_trader_decisions lists them", ex.Message);
    }

    [Fact]
    public async Task Its_shadow_book_of_a_day_or_a_replay_is_netted_after_charges()
    {
        using var db = NewDb();
        Position(db, Monday, null, net: -2142m, charges: 62m);
        Position(db, Monday, null, net: 1809m, charges: 60m);
        Position(db, Monday, 12, net: 500m, charges: 50m);

        var day = await Run(new AiTraderBookTool(db, new AiAgentsTests.FixedTime(Eleven)), "{}");
        var replay = await Run(new AiTraderBookTool(db, new AiAgentsTests.FixedTime(Eleven)), """{"replay":12}""");

        Assert.Equal((2, -333m, 122m), (day.GetProperty("totals").GetProperty("positions").GetInt32(), day.GetProperty("totals").GetProperty("netPnl").GetDecimal(),
            day.GetProperty("totals").GetProperty("charges").GetDecimal()));
        Assert.Equal(500m, replay.GetProperty("totals").GetProperty("netPnl").GetDecimal());
    }

    [Fact]
    public async Task Its_scoreboard_sets_each_day_beside_the_baseline_rule()
    {
        using var db = NewDb();
        for (int i = 0; i < 6; i++) Decision(db, IstTime.FromIst(new DateTime(2026, 10, 5, 9, 30, 0)).AddHours(i), AiTraderPlan.None, true, "ok");
        Position(db, Monday, null, net: -1200m, charges: 60m);
        db.AiTraderBaselines.Add(new AiTraderBaseline { Day = Monday, Rule = AiTraderBaselineScorer.TrendRule, OptionType = "CE", Symbol = "NSE:NIFTY26O0622650CE", NetPnl = 800m });
        db.SaveChanges();

        var json = await Run(new AiTraderScoreboardTool(db), "{}");

        var row = json.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal((true, -1200m, 800m, -2000m), (row.GetProperty("full").GetBoolean(), row.GetProperty("netPnl").GetDecimal(),
            row.GetProperty("baseline").GetProperty("netPnl").GetDecimal(), row.GetProperty("youLessBaseline").GetDecimal()));
        Assert.Equal((1, 0), (json.GetProperty("totals").GetProperty("fullScoredRows").GetInt32(), json.GetProperty("totals").GetProperty("rowsYouBeatTheRule").GetInt32()));
        Assert.Contains("At 11:00 IST", json.GetProperty("ruleText").GetString());
    }

    [Fact]
    public async Task Its_lessons_carry_their_evidence_and_read_now_leaves_out_a_lesson_learned_today()
    {
        using var db = NewDb();
        var old = Lesson(db, "Wait for the first half hour before buying.", new DateOnly(2026, 10, 2), AiMemoryStatus.Active);
        var fresh = Lesson(db, "Do not buy calls into a call wall.", Monday, AiMemoryStatus.Active);
        var dropped = Lesson(db, "Buy every dip.", new DateOnly(2026, 10, 1), AiMemoryStatus.Rejected);
        db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.AiTraderLessonCheck, SubjectType = AiReportSubject.Check, SubjectId = AiTraderLessonCheck.SubjectOf(old.Id),
            DataJson = """{"points":[1,2,3],"control":{"net":-500,"bad":0},"treatment":{"net":2768,"bad":0},"gain":3268,"helped":3,"hurt":1,"passed":true,"used":true,"verdict":"check: +₹3,268 over 3 looks; helped 3, hurt 1"}""",
        });
        db.SaveChanges();
        var monitor = new AiTestKit.Monitor(Settings());

        var json = await Run(new AiTraderLessonsTool(db, monitor, new AiAgentsTests.FixedTime(IstTime.FromIst(new DateTime(2026, 10, 5, 20, 0, 0)))), "{}");

        var lessons = json.GetProperty("lessons").EnumerateArray().ToList();
        Assert.Equal(3, lessons.Count);
        var tested = lessons.Single(l => l.GetProperty("lessonId").GetInt64() == old.Id);
        Assert.Equal((3268m, 3, 1, "2026-10-02"), (tested.GetProperty("evidence").GetProperty("gain").GetDecimal(), tested.GetProperty("evidence").GetProperty("helped").GetInt32(),
            tested.GetProperty("evidence").GetProperty("hurt").GetInt32(), tested.GetProperty("learnedFrom").GetString()));
        var readNow = json.GetProperty("readNow").EnumerateArray().Select(m => m.GetProperty("memoryId").GetInt64()).ToList();
        Assert.Equal(new[] { old.Id }, readNow);
        Assert.DoesNotContain(fresh.Id, readNow);
        Assert.DoesNotContain(dropped.Id, readNow);

        var active = await Run(new AiTraderLessonsTool(db, monitor), """{"status":"active"}""");
        Assert.Equal(2, active.GetProperty("lessons").GetArrayLength());
    }

    [Fact]
    public async Task A_look_now_asks_its_own_prompt_judges_the_plan_and_saves_nothing()
    {
        var (agent, ai, _, _) = AiTraderAgentTests.Agent();
        var old = Lesson(ai.Db, "Wait for the first half hour before buying.", new DateOnly(2026, 10, 2), AiMemoryStatus.Active);
        var fresh = Lesson(ai.Db, "Do not buy calls into a call wall.", Monday, AiMemoryStatus.Active);
        ai.Provider.On(Judge1, Answer(BuyAtmCall));
        var tool = new AiTraderLookNowTool(Scopes(agent), new AiAgentsTests.FixedTime(Eleven));

        var output = await tool.RunAsync(AiToolArgs.Empty.For(new AiToolCaller(AiCatalog.AiTrader, "console", "upendra", 1, "c-chat")), default);
        var json = Read(output);

        Assert.True(json.GetProperty("taken").GetBoolean());
        Assert.Equal(("buy", "NIFTY", "CE"), (json.GetProperty("plan").GetProperty("action").GetString(), json.GetProperty("plan").GetProperty("underlying").GetString(),
            json.GetProperty("plan").GetProperty("option").GetString()));
        Assert.Equal((true, "ok"), (json.GetProperty("verdict").GetProperty("allowed").GetBoolean(), json.GetProperty("verdict").GetProperty("rule").GetString()));
        Assert.Equal("NSE:NIFTY26O0622650CE", json.GetProperty("contract").GetProperty("symbol").GetString());
        Assert.Contains("MARKET BRIEF", json.GetProperty("briefSummary").GetString());
        Assert.DoesNotContain("Decide now", json.GetProperty("briefSummary").GetString());
        Assert.Equal("buy NIFTY CE → allowed", output.Summary);

        // Nothing saved: no decision, no shadow position; the lessons it read are not counted as used.
        Assert.Equal(0, await ai.Db.AiTraderDecisions.CountAsync());
        Assert.Equal(0, await ai.Db.AiTraderShadowPositions.CountAsync());
        Assert.Equal(0, (await ai.Db.AiMemories.AsNoTracking().SingleAsync(m => m.Id == old.Id)).Uses);

        // One call, as the owner (his rate limits), its own prompt with only the lessons a look now reads.
        var row = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal((AiCatalog.AiTrader, AiTraderAgent.PreviewSource, "upendra"), (row.AgentKey, row.Source, row.RequestedBy));
        Assert.Equal(new[] { old.Id }, AiGateway.MemoryIds(row.MemoryIdsJson));
        Assert.StartsWith(AiCatalog.AiTraderPrompt, row.SystemPrompt);
        Assert.Contains("Wait for the first half hour", row.SystemPrompt);
        Assert.DoesNotContain(fresh.Text, row.SystemPrompt);
        Assert.Equal(new[] { old.Id }, json.GetProperty("memoriesRead").EnumerateArray().Select(m => m.GetProperty("memoryId").GetInt64()));
    }

    [Fact]
    public async Task A_look_now_outside_the_rules_says_which_rule_and_still_saves_nothing()
    {
        var (agent, ai, _, _) = AiTraderAgentTests.Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall.Replace("\"stopLoss\":90", "\"stopLoss\":40")));

        var json = Read(await new AiTraderLookNowTool(Scopes(agent), new AiAgentsTests.FixedTime(Eleven)).RunAsync(AiToolArgs.Empty, default));

        Assert.Equal((false, "stop"), (json.GetProperty("verdict").GetProperty("allowed").GetBoolean(), json.GetProperty("verdict").GetProperty("rule").GetString()));
        Assert.Equal(0, await ai.Db.AiTraderDecisions.CountAsync());
        // No caller (a script): it asks as the AI Trader.
        Assert.Equal(AiCatalog.AiTrader, (await ai.Db.AiCalls.SingleAsync()).RequestedBy);
    }

    [Fact]
    public async Task No_look_now_while_a_replay_it_decides_in_is_playing()
    {
        var session = new ReplaySessionState(4, "2026-09-30", 1, "09:15", MarketReplayService.StatePlaying, [], DateTime.UtcNow, null, null, "admin", AiTrader: true);
        var (agent, ai, _, _) = AiTraderAgentTests.Agent(session: session);

        var output = await new AiTraderLookNowTool(Scopes(agent), new AiAgentsTests.FixedTime(Eleven)).RunAsync(AiToolArgs.Empty, default);
        var json = Read(output);

        Assert.False(json.GetProperty("taken").GetBoolean());
        Assert.Contains("A market replay of 2026-09-30 that the AI Trader decides in is playing (replay 4", json.GetProperty("note").GetString());
        Assert.Empty(ai.Provider.Requests);
        Assert.Equal(0, await ai.Db.AiCalls.CountAsync());
    }

    [Fact]
    public void The_brief_summary_keeps_every_section_s_first_lines_and_drops_the_instruction()
    {
        string brief = "MARKET BRIEF — Mon\nNSE session.\n\nINDICES\n" + new string('x', 2000) + "\n\nOPTION CHAINS\nNIFTY ATM 22650\n\nYOUR BOOK\nnothing open\nDecide now: one JSON object.";

        string summary = AiTraderLookNowTool.BriefSummary(brief, perBlock: 100, total: 1000);

        Assert.Contains("INDICES\nxxx", summary);
        Assert.Contains("…", summary);
        Assert.Contains("OPTION CHAINS\nNIFTY ATM 22650", summary);
        Assert.Contains("YOUR BOOK\nnothing open", summary);
        Assert.DoesNotContain("Decide now", summary);
        Assert.True(summary.Length < 600);
    }

    // ---------- the Trade Reviewer ----------

    [Fact]
    public async Task Its_reviews_of_the_week_say_which_runs_broke_their_spec()
    {
        using var db = NewDb();
        long kept = Run(db, "GhostTangentCrossings", "coderforchange");
        long broke = Run(db, "Fulcrum", "admin");
        Review(db, kept, Monday.AddDays(-1), "followed", "Kept every rule.");
        Review(db, broke, Monday, "deviated", "Entered at 09:16, before the 09:20 window.", deviations: ["09:16:04 entry before 09:20"]);
        Review(db, Run(db, "ChainFlowBuy", "admin"), Monday.AddDays(-20), "deviated", "An old one.");

        var json = await Run(new TradeReviewsTool(db, new AiAgentsTests.FixedTime(Eleven)), """{"verdict":"deviated"}""");

        var review = json.GetProperty("reviews").EnumerateArray().Single();
        Assert.Equal((broke, "Fulcrum", "admin", "deviated", 1), (review.GetProperty("runId").GetInt64(), review.GetProperty("strategy").GetString(),
            review.GetProperty("account").GetString(), review.GetProperty("verdict").GetString(), review.GetProperty("deviations").GetInt32()));
        Assert.Equal((2, 1, 1), (json.GetProperty("totals").GetProperty("reviews").GetInt32(), json.GetProperty("totals").GetProperty("followed").GetInt32(),
            json.GetProperty("totals").GetProperty("deviated").GetInt32()));
    }

    [Fact]
    public async Task One_review_in_full_by_its_run_and_a_run_not_reviewed_is_answered_in_words()
    {
        using var db = NewDb();
        long run = Run(db, "Fulcrum", "admin");
        Review(db, run, Monday, "deviated", "Entered before the window.", deviations: ["09:16:04 entry before 09:20"], journal: "It entered at 09:16:04 IST.");

        var json = await Run(new TradeReviewTool(db), $$"""{"runId":{{run}}}""");

        Assert.Equal(("deviated", "Fulcrum"), (json.GetProperty("verdict").GetString(), json.GetProperty("run").GetProperty("strategy").GetString()));
        Assert.Contains("09:16:04", json.GetProperty("journal").GetString());
        Assert.Equal("09:16:04 entry before 09:20", json.GetProperty("deviations")[0].GetString());
        var ex = await Assert.ThrowsAsync<AiToolArgumentException>(() => new TradeReviewTool(db).RunAsync(AiToolArgs.Parse("""{"runId":4242}"""), default));
        Assert.Contains("No review of run 4242", ex.Message);
    }

    // ---------- the News Analyst ----------

    [Fact]
    public async Task Its_records_of_the_last_hour_name_each_item_its_direction_and_what_is_not_read_yet()
    {
        using var db = NewDb();
        var rbi = Headline(db, "RBI cuts repo rate by 25 bps", "The MPC cut the repo rate by 25 bps to 5.25%.", Eleven.AddMinutes(-20));
        var tcs = Filing(db, "TCS", "Order win", "TCS wins a ₹2,000 crore order.", Eleven.AddMinutes(-40));
        Headline(db, "Not read yet", "Arrived a minute ago.", Eleven.AddMinutes(-1));
        Headline(db, "Yesterday's news", "Old.", Eleven.AddHours(-30));
        NewsRecord(db, AiReportSubject.News, rbi.Id, "policy", "positive", ["NIFTY"]);
        NewsRecord(db, AiReportSubject.Filing, tcs.Id, "order win", "positive", ["TCS"]);

        var hour = await Run(new NewsEventsTool(db, new AiAgentsTests.FixedTime(Eleven)), """{"hours":1}""");
        var tcsOnly = await Run(new NewsEventsTool(db, new AiAgentsTests.FixedTime(Eleven)), """{"hours":1,"symbol":"TCS"}""");

        var records = hour.GetProperty("records").EnumerateArray().ToList();
        Assert.Equal(new[] { $"n{rbi.Id}", $"f{tcs.Id}" }, records.Select(r => r.GetProperty("item").GetString()));
        Assert.Equal(("policy", "positive"), (records[0].GetProperty("event").GetString(), records[0].GetProperty("direction").GetString()));
        Assert.Equal((3, 2, 1), (hour.GetProperty("totals").GetProperty("itemsInWindow").GetInt32(), hour.GetProperty("totals").GetProperty("read").GetInt32(),
            hour.GetProperty("totals").GetProperty("notReadYet").GetInt32()));
        Assert.Equal($"f{tcs.Id}", tcsOnly.GetProperty("records").EnumerateArray().Single().GetProperty("item").GetString());
    }

    [Fact]
    public async Task One_record_comes_with_the_text_it_read_exactly_as_it_was_handed_over()
    {
        using var db = NewDb();
        string longSummary = "The MPC cut the repo rate by 25 bps to 5.25%. " + new string('y', 700);
        var rbi = Headline(db, "RBI cuts repo rate by 25 bps", longSummary, Eleven.AddMinutes(-20));
        NewsRecord(db, AiReportSubject.News, rbi.Id, "policy", "positive", ["NIFTY"]);

        var json = await Run(new NewsEventTool(db), $$"""{"item":"n{{rbi.Id}}"}""");

        Assert.Equal(NewsAnalystAgent.HeadlineText(rbi.Title, rbi.Summary), json.GetProperty("textRead").GetString());
        Assert.True(json.GetProperty("textRead").GetString()!.Length < longSummary.Length);
        Assert.Equal("25 bps", json.GetProperty("numbers")[0].GetProperty("quote").GetString());
        Assert.Equal("headline", json.GetProperty("source").GetProperty("kind").GetString());
    }

    // ---------- the Incident Explainer ----------

    [Fact]
    public async Task One_incident_comes_with_its_evidence_masked_and_its_explanation()
    {
        const string Password = "hunter2-db-pass";
        using var db = NewDb();
        var incident = new Incident
        {
            Fingerprint = "f1", Agent = "trading", Rule = "feed_stall", Severity = IncidentSeverity.High, Status = IncidentStatus.Open,
            Title = "Dhan feed stalled", Summary = "No NIFTY ticks for 3 minutes.",
            EvidenceJson = JsonSerializer.Serialize(new[] { "Recv-Q 950000 on the feed socket", $"db password={Password}", "ip-172-31-20-148 load 7.9" }),
            FirstSeenUtc = Eleven.AddMinutes(-5), LastSeenUtc = Eleven,
        };
        db.Incidents.Add(incident);
        db.SaveChanges();
        db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.IncidentExplainer, SubjectType = AiReportSubject.Incident, SubjectId = incident.Id.ToString(), SessionDate = Monday,
            Status = AiReportStatus.Ok, Title = "The feed stopped draining its socket", Body = "**What happened.** The feed fell behind.",
            DataJson = """{"urgency":"now","confidence":0.7}""", CreatedUtc = Eleven, UpdatedUtc = Eleven,
        });
        db.SaveChanges();

        var json = await Run(new IncidentTool(db), $$"""{"incidentId":{{incident.Id}}}""");
        var list = await Run(new IncidentExplanationsTool(db, new AiAgentsTests.FixedTime(Eleven.AddHours(1))), "{}");

        string all = json.GetRawText();
        Assert.Equal(3, json.GetProperty("evidence").GetArrayLength());
        Assert.DoesNotContain(Password, all);
        Assert.DoesNotContain("ip-172-31-20-148", all);
        Assert.Contains("Recv-Q 950000", all);
        Assert.Equal(("now", "The feed stopped draining its socket"), (json.GetProperty("explanation").GetProperty("urgency").GetString(),
            json.GetProperty("explanation").GetProperty("title").GetString()));
        var row = list.GetProperty("explanations").EnumerateArray().Single();
        Assert.Equal((incident.Id, "high", "now"), (row.GetProperty("incidentId").GetInt64(), row.GetProperty("incident").GetProperty("severity").GetString(),
            row.GetProperty("urgency").GetString()));
    }

    // ---------- helpers ----------

    private static async Task<JsonElement> Run(IAiTool tool, string args) => Read(await tool.RunAsync(AiToolArgs.Parse(args), default));

    private static JsonElement Read(AiToolOutput output) => JsonDocument.Parse(JsonSerializer.Serialize(output.Data, Wire)).RootElement;

    /// <summary>A scope factory whose scope holds the agent, as the API's does.</summary>
    private static IServiceScopeFactory Scopes(AiTraderAgent agent)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAiScheduledAgent>(_ => agent);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static AiTraderDecision Decision(TradingDbContext db, DateTime clockUtc, string action, bool allowed, string rule, string plan = "{}",
        long? replay = null, long? callId = null, string brief = "MARKET BRIEF")
    {
        var d = new AiTraderDecision
        {
            CreatedUtc = clockUtc, ClockUtc = clockUtc, Day = IstTime.DateOf(clockUtc), Mode = replay is null ? AiTraderModes.Shadow : AiTraderModes.Replay,
            ReplaySessionId = replay, Action = action, Underlying = action is AiTraderPlan.Buy ? "NIFTY" : string.Empty, PlanJson = plan, Allowed = allowed,
            Rule = rule, Why = allowed ? "Inside every limit." : "The stop ₹40 is more than 40% below the entry.", CallId = callId, Brief = brief,
            Reason = plan.Contains("call wall") ? "NIFTY above EMA20 and EMA50 with call OI falling at the call wall." : "Waiting.",
        };
        db.AiTraderDecisions.Add(d);
        db.SaveChanges();
        return d;
    }

    private static void Position(TradingDbContext db, DateOnly day, long? replay, decimal net, decimal charges)
    {
        db.AiTraderShadowPositions.Add(new AiTraderShadowPosition
        {
            Day = day, ReplaySessionId = replay, Mode = replay is null ? AiTraderModes.Shadow : AiTraderModes.Replay, Symbol = "NSE:NIFTY26O0622650CE",
            Underlying = "NIFTY", OptionType = "CE", Strike = 22650m, Lots = 1, LotSize = 65, EntryUtc = Eleven, EntryPrice = 120m, StopLoss = 90m,
            Target = 160m, ExitUtc = Eleven.AddMinutes(30), ExitPrice = 100m, ExitReason = "exit", Charges = charges, NetPnl = net,
        });
        db.SaveChanges();
    }

    private static AiMemory Lesson(TradingDbContext db, string text, DateOnly learnedFrom, string status)
    {
        var reflection = new AiReport
        {
            AgentKey = AiCatalog.AiTraderReflect, SubjectType = "day", SubjectId = $"day:{learnedFrom:yyyy-MM-dd}", SessionDate = learnedFrom,
            DataJson = "{}", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        db.AiReports.Add(reflection);
        db.SaveChanges();
        var m = new AiMemory
        {
            AgentKey = AiCatalog.AiTrader, Kind = AiMemoryKind.Lesson, Status = status, Text = text, Source = AiMemorySource.Check, Via = "check",
            SourceReportId = reflection.Id, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            ActivatedUtc = status == AiMemoryStatus.Active ? DateTime.UtcNow : null,
        };
        db.AiMemories.Add(m);
        db.SaveChanges();
        return m;
    }

    private static long Run(TradingDbContext db, string strategy, string account)
    {
        var user = db.AppUsers.FirstOrDefault(u => u.UserName == account);
        if (user is null)
        {
            user = new AppUser { UserName = account, Email = $"{account}@example.com" };
            db.AppUsers.Add(user);
            db.SaveChanges();
        }

        var run = new SimulationRun { UserId = user.Id, StrategyName = strategy, Symbol = "NSE:NIFTY50-INDEX", Mode = "LivePaper", Status = "Stopped", StartedUtc = Eleven };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    private static void Review(TradingDbContext db, long runId, DateOnly day, string verdict, string title, string[]? deviations = null, string journal = "A journal.")
    {
        db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.TradeReviewer, SubjectType = AiReportSubject.Run, SubjectId = runId.ToString(), SessionDate = day, Status = AiReportStatus.Ok,
            Title = title, Body = $"**Verdict:** {verdict}\n\n{journal}", CreatedUtc = Eleven, UpdatedUtc = Eleven,
            DataJson = JsonSerializer.Serialize(new { verdict, followed = new[] { "Sized one lot." }, deviations = deviations ?? [], staleFills = 0, lesson = "" }),
        });
        db.SaveChanges();
    }

    private static NewsItem Headline(TradingDbContext db, string title, string summary, DateTime publishedUtc)
    {
        var n = new NewsItem { Source = "ET Markets", Title = title, Summary = summary, Link = "https://example.com/n", LinkHash = Guid.NewGuid().ToString("N"),
            PublishedUtc = publishedUtc, FirstSeenUtc = publishedUtc };
        db.NewsItems.Add(n);
        db.SaveChanges();
        return n;
    }

    private static CorporateAnnouncement Filing(TradingDbContext db, string symbol, string subject, string details, DateTime announcedUtc)
    {
        var a = new CorporateAnnouncement { Symbol = symbol, Company = symbol, Subject = subject, Details = details, AnnouncedUtc = announcedUtc,
            FirstSeenUtc = announcedUtc, UniqueKey = Guid.NewGuid().ToString("N") };
        db.CorporateAnnouncements.Add(a);
        db.SaveChanges();
        return a;
    }

    private static void NewsRecord(TradingDbContext db, string subjectType, long itemId, string ev, string direction, string[] symbols)
    {
        db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.NewsAnalyst, SubjectType = subjectType, SubjectId = itemId.ToString(), SessionDate = Monday, Status = AiReportStatus.Ok,
            Title = "A record", CreatedUtc = Eleven, UpdatedUtc = Eleven,
            DataJson = JsonSerializer.Serialize(new
            {
                @event = ev, direction, symbols, confidence = 0.8, summary = "One line.",
                numbers = new[] { new { what = "cut", value = 25, unit = "bps", quote = "25 bps" } },
            }),
        });
        db.SaveChanges();
    }
}
