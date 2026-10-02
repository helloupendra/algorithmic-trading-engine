using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI Trader's lessons: a reflection on each finished day proposes them, a test on past looks decides each one,
/// and a decision reads only those learned from days before its own.
/// </summary>
public class AiTraderLessonsTests
{
    // Mon 28 Sep to Fri 2 Oct 2026.
    private static readonly DateOnly Mon = new(2026, 9, 28);
    private static readonly DateOnly Tue = new(2026, 9, 29);
    private static readonly DateOnly Wed = new(2026, 9, 30);
    private static readonly DateOnly Thu = new(2026, 10, 1);
    private static readonly DateOnly Fri = new(2026, 10, 2);
    private const string Call = "NSE:NIFTY26O0622650CE";
    private const string None = """{"action":"none","reason":"Nothing clear in the brief.","confidence":0.3}""";

    private static readonly MarketSessionService Sessions = new(new MarketReplayTests.OpenCalendar());

    private static DateTime At(DateOnly day, int hour, int minute) => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute)));

    private static string Buy(decimal stop, decimal target) =>
        $$"""{"action":"buy","underlying":"NIFTY","option":"CE","strike":"ATM","lots":1,"stopLoss":{{stop}},"target":{{target}},"reason":"NIFTY above EMA20 and EMA50.","confidence":0.5}""";

    // ---------- recall: a decision reads only what was known before its day ----------

    [Fact]
    public async Task A_replay_reads_only_lessons_learned_from_days_before_the_replayed_day_and_its_call_keeps_which()
    {
        var (agent, ai, _, _) = AiTraderAgentTests.Agent();
        var earlier = Lesson(ai.Db, "Wait for the opening range to break before buying.", AiMemoryStatus.Active, Reflected(ai.Db, Mon, "replay:1").Id);
        var later = Lesson(ai.Db, "Buy puts when the open gaps down and holds.", AiMemoryStatus.Active, Reflected(ai.Db, Thu, "replay:3").Id);
        var sameDay = Lesson(ai.Db, "Take profit at the first target on an expiry day.", AiMemoryStatus.Active, Reflected(ai.Db, Wed, "replay:2").Id);
        var untested = Lesson(ai.Db, "A lesson still under test.", AiMemoryStatus.Proposed, Reflected(ai.Db, Mon, "replay:4").Id);
        ai.Provider.On(Judge1, Answer(None));

        // A replay of Wed 30 Sep, decided on its clock.
        var row = await agent.DecideAsync(At(Wed, 11, 0), AiTraderModes.Replay, 9, default);

        string system = SystemOf(ai.Provider.Requests.Single(r => r.Model == Judge1));
        Assert.StartsWith(AiCatalog.AiTraderPrompt, system);
        Assert.Contains($"[M{earlier.Id}] lesson: Wait for the opening range to break before buying.", system);
        Assert.DoesNotContain(later.Text, system);
        Assert.DoesNotContain(sameDay.Text, system);
        Assert.DoesNotContain(untested.Text, system);
        var call = await ai.Db.AiCalls.SingleAsync(c => c.Id == row.CallId);
        Assert.Equal(new[] { earlier.Id }, AiGateway.MemoryIds(call.MemoryIdsJson));
        Assert.Equal((1, 0), (earlier.Uses, later.Uses));
    }

    [Fact]
    public async Task A_live_look_reads_every_earlier_lesson_and_the_owners_notes_written_before_it()
    {
        var (agent, ai, _, _) = AiTraderAgentTests.Agent();
        var lesson = Lesson(ai.Db, "Wait for the opening range to break before buying.", AiMemoryStatus.Active, Reflected(ai.Db, Thu, "day:2026-10-01").Id);
        var note = Note(ai.Db, "Prefer NIFTY over SENSEX: its chain is recorded every minute.", At(Fri, 8, 0));
        var tooLate = Note(ai.Db, "A note written after the look.", At(Fri, 12, 0));
        ai.Provider.On(Judge1, Answer(None));

        var row = await agent.DecideAsync(At(Fri, 11, 0), AiTraderModes.Shadow, null, default);

        string system = SystemOf(ai.Provider.Requests.Single(r => r.Model == Judge1));
        Assert.Contains($"[M{note.Id}] the owner's note: Prefer NIFTY", system);
        Assert.Contains($"[M{lesson.Id}] lesson: Wait", system);
        Assert.DoesNotContain(tooLate.Text, system);
        // Notes before lessons, as the gateway orders memories.
        Assert.True(system.IndexOf($"[M{note.Id}]", StringComparison.Ordinal) < system.IndexOf($"[M{lesson.Id}]", StringComparison.Ordinal));
        var call = await ai.Db.AiCalls.SingleAsync(c => c.Id == row.CallId);
        Assert.Equal(new[] { note.Id, lesson.Id }, AiGateway.MemoryIds(call.MemoryIdsJson));
    }

    [Fact]
    public async Task With_memory_switched_off_the_ai_trader_reads_its_own_prompt_alone()
    {
        var (agent, ai, _, _) = AiTraderAgentTests.Agent();
        ai.Options.CurrentValue.MemoryEnabled = false;
        Lesson(ai.Db, "Wait for the opening range to break before buying.", AiMemoryStatus.Active, Reflected(ai.Db, Mon, "replay:1").Id);
        ai.Provider.On(Judge1, Answer(None));

        await agent.DecideAsync(At(Wed, 11, 0), AiTraderModes.Replay, 9, default);

        Assert.Equal(AiCatalog.AiTraderPrompt, SystemOf(ai.Provider.Requests.Single(r => r.Model == Judge1)));
    }

    [Fact]
    public async Task A_caller_with_its_own_prompt_names_the_memories_in_it_and_the_row_keeps_them()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("ok"), Answer("ok"));

        var own = await ai.Gateway.AskAsync(Question() with { SystemPrompt = "You are terse.\n[M7] lesson: x", GivenMemoryIds = [7, 7, 9] }, new RecordingSink(), default);
        var none = await ai.Gateway.AskAsync(Question() with { GivenMemoryIds = [7] }, new RecordingSink(), default);

        Assert.Equal(new long[] { 7, 9 }, own.MemoryIds);
        Assert.Empty(none.MemoryIds); // without a prompt of its own, the gateway's recall decides
    }

    // ---------- the reflection ----------

    [Fact]
    public async Task A_finished_replay_is_reflected_once_and_its_general_lessons_are_proposed_with_where_they_came_from()
    {
        var (reflection, ai) = Reflection();
        var buy = Look(ai.Db, Wed, 10, 0, AiTraderPlan.Buy, replay: 12, plan: Buy(70, 150), allowed: true);
        var wait = Look(ai.Db, Wed, 10, 10, AiTraderPlan.None, replay: 12);
        Position(ai.Db, buy, replay: 12, exit: 66m, reason: AiTraderShadowBook.Stopped);
        string answer = new JsonObject
        {
            ["lessons"] = new JsonArray(
                Proposal("Wait for the first half hour's range to break before buying a call.", buy.Id),
                Proposal("Do not buy calls while NIFTY is under 24,500.", buy.Id),
                Proposal("On 30 Sep the market fell: buy puts.", buy.Id),
                Proposal("Never buy NSE:NIFTY26O0622650CE again.", buy.Id),
                Proposal("Exit a position whose premium stalls for half an hour.", 999_999)),
            ["summary"] = "It bought early and was stopped.",
        }.ToJsonString();
        ai.Provider.On(Judge1, Answer(answer));
        var now = At(Thu, 18, 0);

        var due = await reflection.DueAsync(now, default);
        Assert.Equal(new[] { "replay:12" }, due.Select(s => s.Id));
        var report = (await reflection.ReflectAsync(due[0], now, default))!;

        Assert.Equal((AiCatalog.AiTraderReflect, AiReportSubject.Check, "replay:12", (DateOnly?)Wed, AiReportStatus.Ok),
            (report.AgentKey, report.SubjectType, report.SubjectId, report.SessionDate, report.Status));
        var lesson = await ai.Db.AiMemories.SingleAsync();
        Assert.Equal((AiCatalog.AiTrader, AiMemoryKind.Lesson, AiMemoryStatus.Proposed, AiMemorySource.Check, "check", (long?)report.Id, report.CallId),
            (lesson.AgentKey, lesson.Kind, lesson.Status, lesson.Source, lesson.Via, lesson.SourceReportId, lesson.SourceCallId));
        Assert.Equal("Wait for the first half hour's range to break before buying a call.", lesson.Text);
        Assert.Contains($"decisions {buy.Id}", lesson.Context);

        var data = JsonNode.Parse(report.DataJson)!;
        Assert.Equal(lesson.Id, data["lessons"]![0]!["memoryId"]!.GetValue<long>());
        Assert.Equal(new[] { "names a price or a level", "names a date", "names a contract", "rests on no decision of the day" },
            data["dropped"]!.AsArray().Select(d => d!["why"]!.GetValue<string>()));

        // The Judge read the day: each look by number, the position and how it ended, and the baseline rule's day.
        string asked = UserOf(ai.Provider.Requests.Single(r => r.Model == Judge1));
        Assert.Contains($"#{buy.Id} 10:00 buy NIFTY ATM CE, 1 lot(s), stop 70, target 150 → allowed; position P", asked);
        Assert.Contains($"#{wait.Id} 10:10 none NIFTY → nothing to judge.", asked);
        Assert.Contains("by its stop; net −₹", asked);
        Assert.Contains("THE BASELINE RULE (nifty-trend-1100)", asked);
        Assert.Contains("That day: no trade:", asked);

        // Never reflected again.
        Assert.Empty(await reflection.DueAsync(now.AddHours(1), default));
    }

    [Fact]
    public async Task A_replay_still_playing_or_with_a_position_open_and_a_live_day_before_its_close_has_settled_are_not_due()
    {
        var playing = new ReplaySessionState(13, "2026-09-30", 2, "09:15", MarketReplayService.StatePlaying, [], DateTime.UtcNow, null, null, "admin", AiTrader: true);
        var (reflection, ai) = Reflection(playing);
        Look(ai.Db, Wed, 10, 0, AiTraderPlan.None, replay: 13);
        var open = Look(ai.Db, Tue, 10, 0, AiTraderPlan.Buy, replay: 11, plan: Buy(70, 150), allowed: true);
        Position(ai.Db, open, replay: 11, exit: null, reason: string.Empty);
        Look(ai.Db, Tue, 10, 0, AiTraderPlan.None, replay: 10, rule: "no-answer");
        Look(ai.Db, Thu, 10, 0, AiTraderPlan.None);

        Assert.Empty(await reflection.DueAsync(At(Thu, 15, 40), default));
        Assert.Equal(new[] { "day:2026-10-01" }, (await reflection.DueAsync(At(Thu, 15, 45), default)).Select(s => s.Id));
    }

    [Fact]
    public async Task A_live_day_is_reflected_the_same_evening_with_the_baseline_rule_scored_for_it()
    {
        var (reflection, ai) = Reflection();
        Look(ai.Db, Thu, 10, 0, AiTraderPlan.None);
        ai.Provider.On(Judge1, Answer("""{"lessons":[],"summary":"A quiet day: it waited."}"""));
        var now = At(Thu, 16, 0);

        var report = (await reflection.ReflectAsync((await reflection.DueAsync(now, default)).Single(), now, default))!;

        Assert.Equal(("day:2026-10-01", AiReportStatus.Ok), (report.SubjectId, report.Status));
        Assert.EndsWith("no lesson", report.Title);
        Assert.Equal(Thu, (await ai.Db.AiTraderBaselines.SingleAsync()).Day);
        Assert.Contains("live, in shadow mode", UserOf(ai.Provider.Requests.Single(r => r.Model == Judge1)));
    }

    [Fact]
    public async Task An_answer_that_is_not_the_json_asked_for_is_kept_as_failed_and_tried_again()
    {
        var now = At(Thu, 18, 0);
        var clock = new Clock(now);
        var (reflection, ai) = Reflection(clock: clock);
        Look(ai.Db, Wed, 10, 0, AiTraderPlan.None, replay: 12);
        ai.Provider.On(Judge1, Answer("The day was choppy; it should have waited."));

        var report = (await reflection.ReflectAsync((await reflection.DueAsync(now, default))[0], now, default))!;

        Assert.Equal((AiReportStatus.Failed, 1), (report.Status, report.Attempts));
        Assert.Empty(await ai.Db.AiMemories.ToListAsync());
        clock.Now = now.AddMinutes(5);
        Assert.Empty(await reflection.DueAsync(clock.Now, default));
        clock.Now = now.AddMinutes(20);
        Assert.Single(await reflection.DueAsync(clock.Now, default));
    }

    [Theory]
    [InlineData("Wait for the first half hour's range to break before buying.", null)]
    [InlineData("Exit by 14:30 when the trade has not reached half its target.", null)]
    [InlineData("Keep the stop under the last 5-minute swing low, not 40% away.", null)]
    [InlineData("Do not buy when the premium is ₹200 or more.", "names a price or a level")]
    [InlineData("Avoid calls under 24500.", "names a price or a level")]
    [InlineData("Avoid the 22650 CE.", "names a contract")]
    [InlineData("Remember 16 Sep: the gap filled by noon.", "names a date")]
    [InlineData("Do not trade like on 2026-09-16.", "names a date")]
    [InlineData("Be careful.", "too short to be a rule")]
    public void A_lesson_must_be_general(string text, string? problem) => Assert.Equal(problem, AiTraderReflection.Problem(text));

    // ---------- the test ----------

    [Fact]
    public async Task A_lesson_that_turns_a_none_into_a_winning_buy_on_a_past_look_is_used_with_its_evidence()
    {
        var tape = Tape.Rising(Tue, 10, 0);
        var (check, ai, _) = Check(tape);
        var look = Look(ai.Db, Tue, 10, 0, AiTraderPlan.None);
        var lesson = Lesson(ai.Db, "Buy the at-the-money call when the trend is up.", AiMemoryStatus.Proposed, Reflected(ai.Db, Wed, "replay:7").Id);
        ai.Provider.On(Judge1, Answer(None), Answer(Buy(70, 150)));

        Assert.True(await check.StepAsync(At(Wed, 18, 0), startNew: true, default));

        decimal net = Math.Round(51m * 65 - AiTraderShadowBook.Charges(Call, 100m, 151m, 65), 2);
        Assert.Equal(AiMemoryStatus.Active, lesson.Status);
        Assert.Equal($"check: {AiTraderReflection.Rupees(net)} over 1 look; helped 1, hurt 0", lesson.DecidedBy);

        var report = await ai.Db.AiReports.SingleAsync(r => r.AgentKey == AiCatalog.AiTraderLessonCheck);
        Assert.Equal(($"M{lesson.Id}", (DateOnly?)Wed), (report.SubjectId, report.SessionDate));
        var evidence = AiTraderController.Evidence(report.DataJson)!;
        Assert.Equal((1, 0m, net, net, 1, 0, true, true), (evidence.Points, evidence.ControlNet, evidence.TreatmentNet, evidence.Gain, evidence.Helped,
            evidence.Hurt, evidence.Passed, evidence.Used));
        Assert.Contains($"| #{look.Id} | 29 Sep 10:00 | none | ₹0 | buy {Call} ×1: 10:00 at 100 → 10:20 at 151 by its target |", report.Body);

        // Both asks are the AI Trader's, on the stored brief; only the treatment carried the lesson.
        var calls = await ai.Db.AiCalls.Where(c => c.AgentKey == AiCatalog.AiTrader).OrderBy(c => c.Id).ToListAsync();
        Assert.All(calls, c => Assert.Equal(("check", AiCatalog.AiTraderLessonCheck), (c.Source, c.RequestedBy)));
        Assert.Equal(new[] { "[]", $"[{lesson.Id}]" }, calls.Select(c => c.MemoryIdsJson));
        Assert.DoesNotContain(lesson.Text, calls[0].SystemPrompt);
        Assert.Contains($"[M{lesson.Id}] lesson: {lesson.Text}", calls[1].SystemPrompt);
        Assert.All(ai.Provider.Requests.Where(r => r.Model == Judge1), r => Assert.Equal($"MARKET BRIEF {look.Id}", UserOf(r)));
    }

    [Fact]
    public async Task A_lesson_that_loses_money_against_the_looks_without_it_is_dropped()
    {
        var tape = Tape.Rising(Tue, 10, 0);
        var (check, ai, _) = Check(tape);
        Look(ai.Db, Tue, 10, 0, AiTraderPlan.Buy, plan: Buy(70, 150), allowed: true);
        var lesson = Lesson(ai.Db, "Never buy before eleven.", AiMemoryStatus.Proposed, Reflected(ai.Db, Wed, "replay:7").Id);
        ai.Provider.On(Judge1, Answer(Buy(70, 150)), Answer(None));

        await check.StepAsync(At(Wed, 18, 0), startNew: true, default);

        Assert.Equal(AiMemoryStatus.Rejected, lesson.Status);
        Assert.StartsWith("check: −₹", lesson.DecidedBy);
        Assert.EndsWith("with it over 1 look, under the +₹500 needed", lesson.DecidedBy);
    }

    [Fact]
    public async Task The_control_reads_the_lessons_active_before_each_looks_day_and_never_a_look_from_the_lessons_own_day()
    {
        var tape = Tape.Rising(Tue, 10, 0);
        var (check, ai, state) = Check(tape);
        var before = Lesson(ai.Db, "Wait for the opening range to break before buying.", AiMemoryStatus.Active, Reflected(ai.Db, Mon, "replay:1").Id);
        var after = Lesson(ai.Db, "Buy puts when the open gaps down and holds.", AiMemoryStatus.Active, Reflected(ai.Db, Thu, "replay:3").Id);
        var tuesday = Look(ai.Db, Tue, 10, 0, AiTraderPlan.None);
        Look(ai.Db, Wed, 10, 0, AiTraderPlan.None);
        Look(ai.Db, Tue, 15, 0, AiTraderPlan.None);   // after 14:45: a buy is refused either way
        var lesson = Lesson(ai.Db, "Buy the at-the-money call when the trend is up.", AiMemoryStatus.Proposed, Reflected(ai.Db, Wed, "replay:7").Id);
        ai.Provider.On(Judge1, Answer(None), Answer(None));

        await check.StepAsync(At(Wed, 18, 0), startNew: true, default);

        var calls = await ai.Db.AiCalls.Where(c => c.AgentKey == AiCatalog.AiTrader).OrderBy(c => c.Id).ToListAsync();
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { before.Id }, AiGateway.MemoryIds(calls[0].MemoryIdsJson));
        Assert.Equal(new[] { lesson.Id, before.Id }, AiGateway.MemoryIds(calls[1].MemoryIdsJson));
        Assert.DoesNotContain(after.Text, calls[1].SystemPrompt);
        var data = JsonNode.Parse((await ai.Db.AiReports.SingleAsync(r => r.AgentKey == AiCatalog.AiTraderLessonCheck)).DataJson)!;
        Assert.Equal(new[] { tuesday.Id }, data["points"]!.AsArray().Select(p => p!["decisionId"]!.GetValue<long>()));
        Assert.Null(state.Current);
    }

    [Fact]
    public void The_looks_are_spread_across_days_newest_first_and_mix_actions_with_nones()
    {
        var c = new List<AiTraderLessonCheck.LessonCandidate>();
        long id = 0;
        foreach (var day in new[] { Mon, Tue, Wed })
        {
            c.Add(new(++id, day, At(day, 10, 0), AiTraderPlan.Buy));
            c.Add(new(++id, day, At(day, 11, 0), AiTraderPlan.Buy));
            c.Add(new(++id, day, At(day, 10, 30), AiTraderPlan.None));
            c.Add(new(++id, day, At(day, 12, 0), AiTraderPlan.None));
            c.Add(new(++id, day, At(day, 14, 50), AiTraderPlan.Buy));   // past 14:45: never asked
        }

        var picked = AiTraderLessonCheck.Pick(c, 6, AiTraderAgent.Rules);

        // Each day's newest action, then each day's newest none, alternating, Wednesday first.
        Assert.Equal(new long[] { 12, 14, 7, 9, 2, 4 }, picked);
        Assert.Equal(12, AiTraderLessonCheck.Pick(c, 16, AiTraderAgent.Rules).Count);
        Assert.Equal(new long[] { 12, 7, 2 }, AiTraderLessonCheck.Pick(c.Where(x => x.Action == AiTraderPlan.Buy), 3, AiTraderAgent.Rules));
    }

    [Theory]
    [InlineData(new[] { 0, 0, 0 }, new[] { 600, 0, 0 }, 0, 0, true)]
    [InlineData(new[] { 0, 0, 0 }, new[] { 499, 0, 0 }, 0, 0, false)]           // under ₹500
    [InlineData(new[] { 0, 0, 0 }, new[] { 2000, -300, -300 }, 0, 0, false)]   // gains, but hurt 2 and helped 1
    [InlineData(new[] { 0, 0, 0 }, new[] { 1000, 0, 0 }, 0, 1, false)]         // one more unreadable with it
    [InlineData(new[] { 0, 0, 0 }, new[] { 1000, 0, 0 }, 1, 1, true)]
    public void A_lesson_is_used_on_a_gain_of_500_as_many_helped_as_hurt_and_no_more_bad_answers(
        int[] control, int[] treatment, int controlBad, int treatmentBad, bool passed)
    {
        static LessonArm Arm(int net, bool bad) => new(bad ? string.Empty : AiTraderPlan.None, bad ? "unreadable" : "ok", "x", net, null);
        var points = control.Select((c, i) => new LessonPoint(i + 1, Tue, "10:00", Arm(c, i < controlBad), Arm(treatment[i], i < treatmentBad))).ToList();

        Assert.Equal(passed, AiTraderLessonCheck.Judge(points, 500m).Passed);
    }

    [Fact]
    public async Task A_call_turned_away_for_capacity_pauses_the_test_and_the_same_look_is_asked_again()
    {
        var (check, ai, state) = Check(Tape.Rising(Tue, 10, 0));
        await ai.Store.SetAgentChainAsync(AiCatalog.AiTrader, [Judge1], "upendra", null);
        Look(ai.Db, Tue, 10, 0, AiTraderPlan.None);
        var lesson = Lesson(ai.Db, "Buy the at-the-money call when the trend is up.", AiMemoryStatus.Proposed, Reflected(ai.Db, Wed, "replay:7").Id);
        ai.Provider.On(Judge1, Answer(None), Script.Status(429, "Too many requests"), Answer(Buy(70, 150)));
        var now = At(Wed, 18, 0);

        Assert.True(await check.StepAsync(now, startNew: true, default));
        Assert.Equal(AiMemoryStatus.Proposed, lesson.Status);
        Assert.Equal(now + AiTraderLessonCheck.PauseAfterRefusal, state.PauseUntilUtc);
        Assert.False(await check.StepAsync(now.AddMinutes(1), startNew: true, default));

        // The control's answer was kept: only the treatment is asked again.
        Assert.True(await check.StepAsync(now.AddMinutes(3), startNew: true, default));
        Assert.Equal(AiMemoryStatus.Active, lesson.Status);
        Assert.Equal(3, ai.Provider.Requests.Count(r => r.Model == Judge1));
    }

    [Fact]
    public async Task A_call_no_model_answers_is_asked_once_more_then_counts_as_no_answer()
    {
        var (check, ai, _) = Check(Tape.Rising(Tue, 10, 0));
        await ai.Store.SetAgentChainAsync(AiCatalog.AiTrader, [Judge1], "upendra", null);
        Look(ai.Db, Tue, 10, 0, AiTraderPlan.None);
        var lesson = Lesson(ai.Db, "Buy the at-the-money call when the trend is up.", AiMemoryStatus.Proposed, Reflected(ai.Db, Wed, "replay:7").Id);
        ai.Provider.On(Judge1, Answer(None), Script.Status(500, "boom"), Script.Status(500, "boom"));

        await check.StepAsync(At(Wed, 18, 0), startNew: true, default);

        Assert.Equal(AiMemoryStatus.Rejected, lesson.Status);
        var evidence = AiTraderController.Evidence((await ai.Db.AiReports.SingleAsync(r => r.AgentKey == AiCatalog.AiTraderLessonCheck)).DataJson)!;
        Assert.Equal((0, 1), (evidence.ControlBad, evidence.TreatmentBad));
    }

    [Fact]
    public async Task Past_the_cap_a_stronger_lesson_retires_the_weakest_and_a_weaker_one_is_not_used()
    {
        var (check, ai, _) = Check(Tape.Rising(Tue, 10, 0));
        var active = Enumerable.Range(1, 8).Select(i =>
        {
            var m = Lesson(ai.Db, $"Active lesson number {i} about waiting.", AiMemoryStatus.Active, Reflected(ai.Db, Mon, $"replay:{i}").Id);
            Tested(ai.Db, m, i == 3 ? 900m : 9_000m);
            return m;
        }).ToList();
        Look(ai.Db, Tue, 10, 0, AiTraderPlan.None);
        Look(ai.Db, Tue, 9, 50, AiTraderPlan.None);   // asked second: the newest look first
        var stronger = Lesson(ai.Db, "Buy the at-the-money call when the trend is up.", AiMemoryStatus.Proposed, Reflected(ai.Db, Wed, "replay:20").Id);
        ai.Provider.On(Judge1, Answer(None), Answer(Buy(70, 150)), Answer(None), Answer(None));

        await check.StepAsync(At(Wed, 18, 0), startNew: true, default);
        await check.StepAsync(At(Wed, 18, 1), startNew: true, default);

        Assert.Equal(AiMemoryStatus.Active, stronger.Status);
        Assert.Equal(AiMemoryStatus.Retired, active[2].Status);
        Assert.Equal($"check: made way for M{stronger.Id}, which tested stronger", active[2].DecidedBy);
        Assert.Equal(8, await ai.Db.AiMemories.CountAsync(m => m.AgentKey == AiCatalog.AiTrader && m.Status == AiMemoryStatus.Active));

        var weaker = Lesson(ai.Db, "Buy calls on any green open.", AiMemoryStatus.Proposed, Reflected(ai.Db, Thu, "replay:21").Id);
        ai.Provider.On(Judge1, Answer(None), Answer(Buy(70, 150)), Answer(None), Answer(None));
        await check.StepAsync(At(Thu, 18, 0), startNew: true, default);
        await check.StepAsync(At(Thu, 18, 1), startNew: true, default);

        Assert.Equal(AiMemoryStatus.Rejected, weaker.Status);
        Assert.StartsWith("check: passed (+₹", weaker.DecidedBy);
        Assert.EndsWith("but no stronger than the 8 active lessons", weaker.DecidedBy);
    }

    [Fact]
    public async Task A_lesson_the_owner_decides_during_its_test_is_left_as_the_owner_decided()
    {
        var (check, ai, state) = Check(Tape.Rising(Tue, 10, 0));
        Look(ai.Db, Tue, 10, 0, AiTraderPlan.None);
        Look(ai.Db, Tue, 10, 30, AiTraderPlan.None);
        var lesson = Lesson(ai.Db, "Buy the at-the-money call when the trend is up.", AiMemoryStatus.Proposed, Reflected(ai.Db, Wed, "replay:7").Id);
        ai.Provider.On(Judge1, Answer(None), Answer(None));

        await check.StepAsync(At(Wed, 18, 0), startNew: true, default);
        Assert.NotNull(state.Current);
        await AiMemoryTests.Memory(ai).UpdateAsync(lesson.Id, null, AiMemoryStatus.Rejected, "upendra", default);

        Assert.False(await check.StepAsync(At(Wed, 18, 1), startNew: true, default));
        Assert.Null(state.Current);
        Assert.Equal("upendra", lesson.DecidedBy);
        Assert.Empty(await ai.Db.AiReports.Where(r => r.AgentKey == AiCatalog.AiTraderLessonCheck).ToListAsync());
    }

    [Fact]
    public async Task The_recorded_chain_is_read_only_from_a_capture_of_the_looks_own_day()
    {
        var ai = Build();
        // Nothing captured on the 30th by 10:00: the newest capture is the 29th's, of the series that expired then.
        MarketBriefBuilderTests.Chain(ai.Db, At(Tue, 15, 29), expiry: Tue);
        var market = new RecordedMarket(ai.Db, new OptionChainService(ai.Db, Sessions, new MarketBriefBuilderTests.Lots(65)));

        Assert.Null(await market.ChainAsync("NIFTY", At(Wed, 10, 0), default));

        MarketBriefBuilderTests.Chain(ai.Db, At(Wed, 9, 59), expiry: new DateOnly(2026, 10, 6));
        var chain = (await market.ChainAsync("NIFTY", At(Wed, 10, 0), default))!;
        Assert.Equal((22650m, 65, Call), (chain.Atm, chain.LotSize, chain.Strikes.Single(s => s.Strike == 22650m).Call));
    }

    // ---------- the agent, the switch and the page ----------

    [Fact]
    public async Task Nothing_runs_in_the_nse_session_or_while_a_replay_the_ai_trader_decides_in_plays()
    {
        var agent = LessonAgent(null);
        var replayAgent = LessonAgent(new ReplaySessionState(13, "2026-09-30", 2, "09:15", MarketReplayService.StatePlaying, [], DateTime.UtcNow, null, null, "admin", AiTrader: true));

        Assert.NotNull(await agent.WhyNotAsync(At(Thu, 9, 0), default));
        Assert.NotNull(await agent.WhyNotAsync(At(Thu, 15, 44), default));
        Assert.Null(await agent.WhyNotAsync(At(Thu, 8, 59), default));
        Assert.Null(await agent.WhyNotAsync(At(Thu, 15, 45), default));
        Assert.Null(await agent.WhyNotAsync(IstTime.FromIst(new DateTime(2026, 10, 3, 11, 0, 0)), default));   // a Saturday
        Assert.Contains("replay #13", await replayAgent.WhyNotAsync(At(Thu, 20, 0), default));
    }

    [Fact]
    public void The_lessons_run_while_the_ai_trader_is_on_and_they_are_enabled()
    {
        var ai = Build();
        var off = ai.Store.LoadAsync().GetAwaiter().GetResult();
        ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null).GetAwaiter().GetResult();
        var on = ai.Store.LoadAsync().GetAwaiter().GetResult();

        Assert.False(AiAgentScheduler.IsOn(AiCatalog.AiTraderReflect, off, Settings()));
        Assert.True(AiAgentScheduler.IsOn(AiCatalog.AiTraderReflect, on, Settings()));
        Assert.False(AiAgentScheduler.IsOn(AiCatalog.AiTraderReflect, on, Settings(s => s.AiTraderLessons = false)));
        Assert.Equal("AI Trader reflection", AiCatalog.AgentName(AiCatalog.AiTraderReflect));
        Assert.True(Settings().HasMemory(AiCatalog.AiTrader));
    }

    [Fact]
    public async Task The_page_lists_each_lesson_with_its_day_its_decisions_and_its_evidence()
    {
        var (check, ai, state) = Check(Tape.Rising(Tue, 10, 0));
        var reflection = Reflected(ai.Db, Wed, "replay:7", """{"lessons":[{"memoryId":0,"decisionIds":[41,42]}]}""");
        var lesson = Lesson(ai.Db, "Buy the at-the-money call when the trend is up.", AiMemoryStatus.Proposed, reflection.Id);
        reflection.DataJson = reflection.DataJson.Replace("\"memoryId\":0", $"\"memoryId\":{lesson.Id}");
        await ai.Db.SaveChangesAsync();
        Look(ai.Db, Tue, 10, 0, AiTraderPlan.None);
        ai.Provider.On(Judge1, Answer(None), Answer(Buy(70, 150)));
        await check.StepAsync(At(Wed, 18, 0), startNew: true, default);

        var view = (AiTraderLessonsView)((OkObjectResult)await new AiTraderController(ai.Db, ai.Store, ai.Options, null, state).Lessons(default)).Value!;

        Assert.True(view.Enabled);
        Assert.Equal((8, 16, 500m, 1), (view.MaxActive, view.TestPoints, view.MinGain, view.Counts.Active));
        var item = view.Lessons.Single();
        Assert.Equal((AiMemoryStatus.Active, "2026-09-30", "replay:7", (long?)7), (item.Status, item.SourceDay, item.Subject, item.ReplaySessionId));
        Assert.Equal(new long[] { 41, 42 }, item.DecisionIds);
        Assert.Equal(reflection.Id, item.ReflectionReportId);
        Assert.NotNull(item.CheckReportId);
        Assert.Equal((1, 1, 0), (item.Evidence!.Points, item.Evidence.Helped, item.Evidence.Hurt));
    }

    // ---------- helpers ----------

    private static JsonObject Proposal(string text, long decision) => new() { ["lesson"] = text, ["decisions"] = new JsonArray(decision) };

    private static string SystemOf(Seen request) => JsonNode.Parse(request.Body)!["messages"]![0]!["content"]!.GetValue<string>();

    private static string UserOf(Seen request) => JsonNode.Parse(request.Body)!["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();

    private static AiReport Reflected(TradingDbContext db, DateOnly day, string subject, string dataJson = "{}")
    {
        var r = new AiReport
        {
            AgentKey = AiCatalog.AiTraderReflect, SubjectType = AiReportSubject.Check, SubjectId = subject, SessionDate = day,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Status = AiReportStatus.Ok, Attempts = 1, DataJson = dataJson,
        };
        db.AiReports.Add(r);
        db.SaveChanges();
        return r;
    }

    private static void Tested(TradingDbContext db, AiMemory lesson, decimal gain)
    {
        db.AiReports.Add(new AiReport
        {
            AgentKey = AiCatalog.AiTraderLessonCheck, SubjectType = AiReportSubject.Check, SubjectId = $"M{lesson.Id}", CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow, Status = AiReportStatus.Ok, Attempts = 1, DataJson = $$"""{"gain":{{gain}}}""",
        });
        db.SaveChanges();
    }

    private static AiMemory Lesson(TradingDbContext db, string text, string status, long reportId)
    {
        var now = DateTime.UtcNow;
        var m = new AiMemory
        {
            AgentKey = AiCatalog.AiTrader, Kind = AiMemoryKind.Lesson, Status = status, Text = text, Source = AiMemorySource.Check, Via = "check",
            SourceReportId = reportId, CreatedBy = AiCatalog.AiTraderReflect, CreatedUtc = now, UpdatedUtc = now,
            ActivatedUtc = status == AiMemoryStatus.Active ? now : null,
        };
        db.AiMemories.Add(m);
        db.SaveChanges();
        return m;
    }

    private static AiMemory Note(TradingDbContext db, string text, DateTime createdUtc)
    {
        var m = new AiMemory
        {
            AgentKey = AiCatalog.AiTrader, Kind = AiMemoryKind.Note, Status = AiMemoryStatus.Active, Text = text, Source = AiMemorySource.Owner,
            CreatedBy = "upendra", CreatedUtc = createdUtc, UpdatedUtc = createdUtc, ActivatedUtc = createdUtc,
        };
        db.AiMemories.Add(m);
        db.SaveChanges();
        return m;
    }

    private static AiTraderDecision Look(TradingDbContext db, DateOnly day, int hour, int minute, string action, long? replay = null,
        string? plan = null, bool allowed = true, string rule = "ok")
    {
        var d = new AiTraderDecision
        {
            CreatedUtc = DateTime.UtcNow.AddHours(-1), ClockUtc = At(day, hour, minute), Day = day,
            Mode = replay is null ? AiTraderModes.Shadow : AiTraderModes.Replay, ReplaySessionId = replay, Action = action, Underlying = "NIFTY",
            Rule = rule, Allowed = allowed && rule == "ok", Why = "Nothing to do.", Reason = "From the brief.", PlanJson = plan ?? "{}",
        };
        db.AiTraderDecisions.Add(d);
        db.SaveChanges();
        d.Brief = $"MARKET BRIEF {d.Id}";
        db.SaveChanges();
        return d;
    }

    private static void Position(TradingDbContext db, AiTraderDecision opened, long? replay, decimal? exit, string reason)
    {
        var p = new AiTraderShadowPosition
        {
            CreatedUtc = DateTime.UtcNow, DecisionId = opened.Id, Mode = opened.Mode, ReplaySessionId = replay, Day = opened.Day, Symbol = Call,
            Underlying = "NIFTY", OptionType = "CE", Strike = 22650m, Expiry = new DateOnly(2026, 10, 6), Lots = 1, LotSize = 65,
            EntryUtc = opened.ClockUtc, EntryPrice = 100m, StopLoss = 70m, Target = 150m, MarkPrice = exit ?? 100m, MarkUtc = opened.ClockUtc,
        };
        if (exit is decimal x)
        {
            p.ExitUtc = opened.ClockUtc.AddMinutes(20);
            p.ExitPrice = x;
            p.ExitReason = reason;
            p.Charges = AiTraderShadowBook.Charges(Call, 100m, x, 65);
            p.NetPnl = Math.Round((x - 100m) * 65 - p.Charges, 2);
        }

        db.AiTraderShadowPositions.Add(p);
        db.SaveChanges();
        opened.ResultJson = $$"""{"shadowPositionId":{{p.Id}}}""";
        db.SaveChanges();
    }

    private static (AiTraderReflection Reflection, Services Ai) Reflection(ReplaySessionState? session = null, TimeProvider? clock = null)
    {
        var ai = Build(Settings());
        var scorer = new AiTraderBaselineScorer(ai.Db, new FlatMarket(), Sessions);
        return (new AiTraderReflection(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options, clock), AiMemoryTests.Memory(ai), scorer,
            new FakeReplays(session), NullLogger<AiTraderReflection>.Instance), ai);
    }

    private static (AiTraderLessonCheck Check, Services Ai, AiTraderLessonState State) Check(Tape tape)
    {
        var ai = Build(Settings());
        ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null).GetAwaiter().GetResult();
        var state = new AiTraderLessonState();
        return (new AiTraderLessonCheck(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), AiMemoryTests.Memory(ai), tape, Sessions, state,
            ai.Options, NullLogger<AiTraderLessonCheck>.Instance), ai, state);
    }

    private static AiTraderLessonAgent LessonAgent(ReplaySessionState? session)
    {
        var (reflection, ai) = Reflection(session);
        var state = new AiTraderLessonState();
        var check = new AiTraderLessonCheck(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), AiMemoryTests.Memory(ai), new Tape(), Sessions, state,
            ai.Options, NullLogger<AiTraderLessonCheck>.Instance);
        return new AiTraderLessonAgent(reflection, check, state, Sessions, new FakeReplays(session), ai.Options, NullLogger<AiTraderLessonAgent>.Instance);
    }

    /// <summary>A recorded NIFTY chain with ATM 22650 and the ticks a test plays buys on.</summary>
    private sealed class Tape : IRecordedMarket
    {
        private readonly List<(string Symbol, BaselineTick Tick)> _ticks = [];

        /// <summary>The ATM call bought at 100 just after the look and bid 151 twenty minutes later: a target at 150 is hit.</summary>
        public static Tape Rising(DateOnly day, int hour, int minute)
        {
            var tape = new Tape();
            var at = At(day, hour, minute);
            tape._ticks.Add((Call, new BaselineTick(at.AddSeconds(5), 99.8m, 99.5m, 100m)));
            tape._ticks.Add((Call, new BaselineTick(at.AddMinutes(20), 151.2m, 151m, 151.5m)));
            return tape;
        }

        public Task<RecordedChain?> ChainAsync(string underlying, DateTime asOfUtc, CancellationToken cancellationToken) =>
            Task.FromResult<RecordedChain?>(underlying != "NIFTY" ? null : new RecordedChain(22650m,
            [
                new RecordedStrike(22600m, "NSE:NIFTY26O0622600CE", "NSE:NIFTY26O0622600PE"),
                new RecordedStrike(22650m, Call, "NSE:NIFTY26O0622650PE"),
                new RecordedStrike(22700m, "NSE:NIFTY26O0622700CE", "NSE:NIFTY26O0622700PE"),
            ], 65, new DateOnly(2026, 10, 6)));

        public Task<IReadOnlyList<BaselineTick>> TicksAsync(string symbol, DateTime fromUtc, DateTime untilUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BaselineTick>>(_ticks.Where(t => t.Symbol == symbol && t.Tick.AtUtc >= fromUtc && t.Tick.AtUtc < untilUtc)
                .Select(t => t.Tick).OrderBy(t => t.AtUtc).ToList());
    }

    /// <summary>Too few bars before 11:00: the baseline rule does not trade.</summary>
    private sealed class FlatMarket : IBaselineMarket
    {
        public Task<IReadOnlyList<LiveBarResponse>> MinutesBeforeAsync(string symbol, DateTime beforeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LiveBarResponse>>([]);

        public Task<BaselineContract?> AtTheMoneyAsync(string underlying, string optionType, DateTime asOfUtc, CancellationToken cancellationToken) =>
            Task.FromResult<BaselineContract?>(null);
    }

    /// <summary>A clock the test moves.</summary>
    private sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    private sealed class FakeReplays(ReplaySessionState? session) : IReplaySessions
    {
        public Task<ReplaySessionState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(session);
    }
}
