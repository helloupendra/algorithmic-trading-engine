using System.Security.Claims;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The agents' memory: what reaches an agent's prompt, how the owner's
/// notes, verdicts and corrections are kept, how a lesson waits for the
/// owner, and how outcomes are counted on the memories behind an answer.
/// </summary>
public sealed class AiMemoryTests
{
    // ---------- what reaches the prompt ----------

    [Fact]
    public async Task Only_the_agents_active_memories_reach_its_prompt_and_the_call_keeps_which()
    {
        var ai = Build();
        var note = Seed(ai.Db, "Weekly NIFTY options expire on Tuesday.");
        var correction = Seed(ai.Db, "Use net after charges, not gross.", kind: AiMemoryKind.Correction, context: "How much did the desk make today?");
        Seed(ai.Db, "A lesson not approved yet.", AiMemoryStatus.Proposed, kind: AiMemoryKind.Lesson);
        Seed(ai.Db, "An old note taken out.", AiMemoryStatus.Retired);
        Seed(ai.Db, "The reviewer's own note.", agent: AiCatalog.TradeReviewer);
        ai.Provider.On(Judge1, Answer("Tuesday."));

        var result = await ai.Gateway.AskAsync(Question("When do weekly options expire?"), new RecordingSink(), CancellationToken.None);

        string system = SystemOf(ai.Provider.Requests.Single());
        Assert.Contains($"[M{note.Id}] note: Weekly NIFTY options expire on Tuesday.", system);
        Assert.Contains($"[M{correction.Id}] correction (asked: \"How much did the desk make today?\"): Use net after charges, not gross.", system);
        Assert.Contains("trust the tool", system);
        Assert.DoesNotContain("not approved yet", system);
        Assert.DoesNotContain("taken out", system);
        Assert.DoesNotContain("reviewer's own note", system);
        Assert.Equal(new[] { correction.Id, note.Id }, result.MemoryIds); // corrections first
        var row = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal(new[] { correction.Id, note.Id }, AiGateway.MemoryIds(row.MemoryIdsJson));
        Assert.Equal((1, 1), (note.Uses, correction.Uses));
    }

    [Fact]
    public async Task A_caller_with_its_own_prompt_a_model_test_or_memory_switched_off_gets_none()
    {
        var own = Build();
        var test = Build();
        var off = Build(Settings(s => s.MemoryEnabled = false));
        var elsewhere = Build(Settings(s => s.MemoryAgents = AiCatalog.TradeReviewer));
        foreach (var ai in new[] { own, test, off, elsewhere })
        {
            Seed(ai.Db, "A note the Assistant would read.");
            ai.Provider.On(Judge1, Answer("ok"));
        }

        var results = new[]
        {
            await own.Gateway.AskAsync(Question() with { SystemPrompt = "You are terse." }, new RecordingSink(), CancellationToken.None),
            await test.Gateway.AskAsync(Question() with { Chain = [Judge1] }, new RecordingSink(), CancellationToken.None),
            await off.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None),
            await elsewhere.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None),
        };

        Assert.All(results, r => Assert.Empty(r.MemoryIds));
        Assert.All(new[] { own, test, off, elsewhere }, ai => Assert.DoesNotContain("would read", ai.Provider.Requests.Single().Body));
    }

    [Fact]
    public async Task Past_the_budget_the_memory_closest_to_the_question_is_read_and_the_ranking_is_a_call()
    {
        var ai = Build(Settings(s => s.MemoryBudgetChars = 120));
        var risk = Seed(ai.Db, "The overall stop loss is measured per day, net of charges.");
        var walls = Seed(ai.Db, "Option chain walls: the put wall and the call wall come from open interest.");
        Seed(ai.Db, "Weekly options expire on Tuesday.");
        ai.Provider.On(Judge1, Answer("Per day."));

        var result = await ai.Gateway.AskAsync(Question("How is the overall stop loss measured?"), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { risk.Id }, result.MemoryIds);
        Assert.NotEmpty(risk.Embedding);
        Assert.Equal(AiCatalog.EmbeddingModel, walls.EmbeddingModel);
        var ranking = await ai.Db.AiCalls.SingleAsync(c => c.AgentKey == AiCatalog.Memory);
        Assert.Equal(("memory", AiCallOutcome.Ok, AiCatalog.DeskAssistant), (ranking.Source, ranking.Outcome, ranking.RequestedBy));
    }

    [Fact]
    public async Task When_the_ranking_fails_the_newest_memories_are_read_and_the_question_is_still_answered()
    {
        var ai = Build(Settings(s => s.MemoryBudgetChars = 120));
        Seed(ai.Db, "The overall stop loss is measured per day, net of charges.");
        Seed(ai.Db, "Option chain walls: the put wall and the call wall come from open interest.");
        var newest = Seed(ai.Db, "Weekly options expire on Tuesday.");
        ai.Provider.EmbeddingsFail = true;
        ai.Provider.On(Judge1, Answer("Per day."));

        var result = await ai.Gateway.AskAsync(Question("How is the overall stop loss measured?"), new RecordingSink(), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal(new[] { newest.Id }, result.MemoryIds);
        Assert.Contains(await ai.Db.AiCalls.ToListAsync(), c => c.AgentKey == AiCatalog.Memory && c.Outcome == AiCallOutcome.Failed);
    }

    // ---------- the owner's notes and verdicts ----------

    [Fact]
    public async Task A_note_is_active_at_once_masked_and_refused_when_empty_or_too_long()
    {
        var ai = Build();
        var memory = Memory(ai);

        var note = await memory.RememberAsync(null, "  The desk runs on ip-172-31-20-148,   reachable at 10.0.0.5.  ", "upendra", "telegram", CancellationToken.None);

        Assert.Equal((AiMemoryStatus.Active, AiMemoryKind.Note, AiMemorySource.Owner, "telegram"), (note.Status, note.Kind, note.Source, note.Via));
        Assert.Equal("The desk runs on [host], reachable at [ip].", note.Text);
        Assert.NotEmpty(note.Embedding);
        await Assert.ThrowsAsync<AiMemoryException>(() => memory.RememberAsync(null, "   ", "upendra", "console", CancellationToken.None));
        await Assert.ThrowsAsync<AiMemoryException>(() => memory.RememberAsync(null, new string('x', 601), "upendra", "console", CancellationToken.None));
        await Assert.ThrowsAsync<AiMemoryException>(() => memory.RememberAsync("technical-analyst", "A note.", "upendra", "console", CancellationToken.None));
    }

    [Fact]
    public async Task A_thumbs_down_with_a_correction_becomes_an_active_memory_on_that_question()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("The desk made ₹2,22,414 today."));
        var answer = await ai.Gateway.AskAsync(Question("How much did the desk make today?"), new RecordingSink(), CancellationToken.None);

        var (call, correction) = await Memory(ai).FeedbackAsync(answer.CallId, -1, "Always give net after charges, not gross.", "upendra", "console", CancellationToken.None);

        Assert.Equal((-1, "Always give net after charges, not gross.", "upendra"), (call.FeedbackScore, call.FeedbackNote, call.FeedbackBy));
        Assert.Equal((AiMemoryKind.Correction, AiMemoryStatus.Active, answer.CallId), (correction!.Kind, correction.Status, correction.SourceCallId));
        Assert.Equal("How much did the desk make today?", correction.Context);

        // A second correction of the same answer replaces the first.
        var (_, again) = await Memory(ai).FeedbackAsync(answer.CallId, -1, "Net after charges.", "upendra", "console", CancellationToken.None);
        Assert.Equal(correction.Id, again!.Id);
        Assert.Single(await ai.Db.AiMemories.ToListAsync());
    }

    [Fact]
    public async Task A_verdict_counts_on_the_memories_the_answer_read_and_can_be_changed_or_taken_back()
    {
        var ai = Build();
        var note = Seed(ai.Db, "Weekly options expire on Tuesday.");
        ai.Provider.On(Judge1, Answer("Tuesday."));
        var answer = await ai.Gateway.AskAsync(Question("When do weekly options expire?"), new RecordingSink(), CancellationToken.None);
        var memory = Memory(ai);

        await memory.FeedbackAsync(answer.CallId, 1, null, "upendra", "console", CancellationToken.None);
        Assert.Equal((1, 0), (note.Ups, note.Downs));
        await memory.FeedbackAsync(answer.CallId, -1, null, "upendra", "console", CancellationToken.None);
        Assert.Equal((0, 1), (note.Ups, note.Downs));
        var (call, _) = await memory.FeedbackAsync(answer.CallId, 0, null, "upendra", "console", CancellationToken.None);
        Assert.Equal((0, 0), (note.Ups, note.Downs));
        Assert.Null(call.FeedbackScore);
    }

    [Fact]
    public async Task A_verdict_needs_an_answered_call_and_a_correction_needs_a_thumbs_down()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("ok"));
        var answer = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);
        await ai.Store.SetAgentEnabledAsync(AiCatalog.DeskAssistant, false, "upendra", "quiet");
        var refused = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);
        var memory = Memory(ai);

        var notFound = await Assert.ThrowsAsync<AiMemoryException>(() => memory.FeedbackAsync(99_999, 1, null, "u", "console", CancellationToken.None));
        var notAnswered = await Assert.ThrowsAsync<AiMemoryException>(() => memory.FeedbackAsync(refused.CallId, 1, null, "u", "console", CancellationToken.None));
        var upWithText = await Assert.ThrowsAsync<AiMemoryException>(() => memory.FeedbackAsync(answer.CallId, 1, "fine", "u", "console", CancellationToken.None));

        Assert.Equal((404, 409, 400), (notFound.Status, notAnswered.Status, upWithText.Status));
    }

    // ---------- lessons wait for the owner ----------

    [Fact]
    public async Task A_proposed_lesson_is_not_read_until_approved_and_one_kind_of_question_gets_one_lesson()
    {
        var ai = Build();
        var memory = Memory(ai);

        var lesson = await memory.ProposeAsync(AiCatalog.DeskAssistant, "Read the day's total from get_runs totals.", "What is run 339's net P&L today?", 7, null, CancellationToken.None);
        var same = await memory.ProposeAsync(AiCatalog.DeskAssistant, "Another wording.", "What is run 341's net P&L today?", 8, null, CancellationToken.None);
        ai.Provider.On(Judge1, Answer("ok"), Answer("ok"));
        var before = await ai.Gateway.AskAsync(Question("What is run 341's net?"), new RecordingSink(), CancellationToken.None);
        await memory.UpdateAsync(lesson!.Id, null, AiMemoryStatus.Active, "upendra", CancellationToken.None);
        var after = await ai.Gateway.AskAsync(Question("What is run 341's net?"), new RecordingSink(), CancellationToken.None);

        Assert.Equal((AiMemoryStatus.Active, AiMemoryKind.Lesson, AiMemorySource.Check), (lesson.Status, lesson.Kind, lesson.Source));
        Assert.Null(same);
        Assert.Empty(before.MemoryIds);
        Assert.Equal(new[] { lesson.Id }, after.MemoryIds);
        Assert.Equal("upendra", lesson.DecidedBy);
    }

    [Fact]
    public async Task A_rejected_lesson_keeps_its_kind_of_question_quiet_for_two_weeks()
    {
        var ai = Build();
        var memory = Memory(ai);
        var lesson = await memory.ProposeAsync(AiCatalog.DeskAssistant, "A lesson the owner does not want.", "How many open legs are there?", 1, null, CancellationToken.None);

        await memory.UpdateAsync(lesson!.Id, null, AiMemoryStatus.Rejected, "upendra", CancellationToken.None);

        Assert.True(await memory.LessonBlockedAsync(AiCatalog.DeskAssistant, "How many open legs are there?", CancellationToken.None));
        lesson.DecidedUtc = DateTime.UtcNow.AddDays(-15);
        await ai.Db.SaveChangesAsync();
        Assert.False(await memory.LessonBlockedAsync(AiCatalog.DeskAssistant, "How many open legs are there?", CancellationToken.None));
    }

    [Fact]
    public async Task Only_a_proposed_memory_is_rejected_only_an_active_one_retired_and_either_restored()
    {
        var ai = Build();
        var memory = Memory(ai);
        var note = await memory.RememberAsync(null, "A note.", "upendra", "console", CancellationToken.None);

        var reject = await Assert.ThrowsAsync<AiMemoryException>(() => memory.UpdateAsync(note.Id, null, AiMemoryStatus.Rejected, "upendra", CancellationToken.None));
        await memory.UpdateAsync(note.Id, null, AiMemoryStatus.Retired, "upendra", CancellationToken.None);
        Assert.NotNull(note.RetiredUtc);
        await memory.UpdateAsync(note.Id, "A better note.", AiMemoryStatus.Active, "upendra", CancellationToken.None);

        Assert.Equal(400, reject.Status);
        Assert.Equal((AiMemoryStatus.Active, "A better note.", (DateTime?)null), (note.Status, note.Text, note.RetiredUtc));
        Assert.Equal(404, (await Assert.ThrowsAsync<AiMemoryException>(() => memory.UpdateAsync(999, "x", null, "u", CancellationToken.None))).Status);
    }

    [Theory]
    [InlineData(2, 0, 0, 0, true)]
    [InlineData(2, 2, 0, 0, false)]
    [InlineData(0, 0, 3, 1, true)]
    [InlineData(0, 0, 3, 3, false)]
    public void Outcomes_flag_a_memory_for_review_but_never_retire_it(int downs, int ups, int fails, int passes, bool review)
    {
        var m = new AiMemory { Status = AiMemoryStatus.Active, Downs = downs, Ups = ups, CheckFails = fails, CheckPasses = passes };
        var verdict = AiMemoryService.Review(m);
        Assert.Equal(review, verdict.Review);
        Assert.Equal(review, verdict.Reason is not null);
        Assert.Equal(AiMemoryStatus.Active, m.Status);
    }

    // ---------- the daily check: outcomes and lessons ----------

    [Fact]
    public async Task The_check_counts_its_grades_on_the_memories_each_answer_read()
    {
        var ai = Build(Settings(s => s.LessonsFromCheck = false), tools: DeskTools());
        var note = Seed(ai.Db, "Money is net of charges unless a tool says gross.");
        ai.Provider.On(Judge1, CheckAnswers());

        await Check(ai, out _).RunForAsync(null, CancellationToken.None);

        Assert.Equal((5, 1), (note.CheckPasses, note.CheckFails)); // 6 questions, the total wrong
        Assert.Equal(6, note.Uses);
    }

    [Fact]
    public async Task A_lesson_that_fixes_its_question_and_breaks_nothing_is_used_without_asking_the_owner()
    {
        var ai = Build(tools: DeskTools());
        ai.Provider.On(Judge1, CheckAnswers()
            .Append(Answer("Lesson: For the day's total, read totals.netPnl from get_runs; it is already net of charges."))
            .Append(Answer("Together: −₹1,05,000.25 after charges."))   // the failed question, with the lesson on trial
            .Append(Answer("There are 2 runs today."))                    // two it got right, still right
            .Append(Answer("Run #339 has the lowest net P&L."))
            .ToArray());

        var report = await Check(ai, out var told).RunForAsync(null, CancellationToken.None);

        var lesson = await ai.Db.AiMemories.SingleAsync();
        var total = JsonNode.Parse(report!.DataJson)!["questions"]!.AsArray()[1]!;
        Assert.Equal((AiMemoryStatus.Active, AssistantCheckAgent.Verified), (lesson.Status, lesson.DecidedBy));
        Assert.Equal("For the day's total, read totals.netPnl from get_runs; it is already net of charges.", lesson.Text);
        Assert.Equal("What is the net P&L of all of today's runs together, after charges?", lesson.Context);
        Assert.Equal((total["callId"]!.GetValue<long>(), report.Id), (lesson.SourceCallId!.Value, lesson.SourceReportId!.Value));
        Assert.Equal(new[] { lesson.Id }, told.Lessons.Select(l => l.Id));
        Assert.Contains($"M{lesson.Id} used (fixed its question)", report.Body);

        // The re-asked question carried the lesson; the day's own answers did not.
        var retry = await ai.Db.AiCalls.SingleAsync(c => c.ConversationId.EndsWith("-verify-1-0"));
        Assert.Contains($"[M{lesson.Id}] lesson", retry.SystemPrompt);
        var dayAnswers = await ai.Db.AiCalls.Where(c => c.AgentKey == AiCatalog.DeskAssistant && !c.ConversationId.Contains("verify")).ToListAsync();
        Assert.Equal(6, dayAnswers.Count);
        Assert.DoesNotContain(dayAnswers, c => c.SystemPrompt.Contains("[M"));
        var judge = await ai.Db.AiCalls.SingleAsync(c => c.SystemPrompt == AssistantCheckAgent.LessonPrompt);
        Assert.Contains("Right answer: -105,000.25", judge.MessagesJson);
    }

    [Theory]
    [InlineData(false, AssistantCheckAgent.DidNotFix)]
    [InlineData(true, AssistantCheckAgent.BrokeAnother)]
    public async Task A_lesson_that_does_not_fix_its_question_or_breaks_another_is_dropped(bool fixes, string verdict)
    {
        var ai = Build(tools: DeskTools());
        var after = fixes
            ? new[] { Answer("Together: −₹1,05,000.25."), Answer("There are 3 runs today.") }   // fixed, but a right answer went wrong
            : new[] { Answer("Together: −₹1,00,000.") };                                       // still wrong
        ai.Provider.On(Judge1, CheckAnswers().Append(Answer("Lesson: Read totals.netPnl for the day's total.")).Concat(after).ToArray());

        await Check(ai, out var told).RunForAsync(null, CancellationToken.None);

        var lesson = await ai.Db.AiMemories.SingleAsync();
        Assert.Equal((AiMemoryStatus.Rejected, verdict), (lesson.Status, lesson.DecidedBy));
        Assert.Equal(new[] { lesson.Id }, told.Lessons.Select(l => l.Id));
    }

    [Fact]
    public async Task A_lesson_on_trial_is_read_on_that_call_alone()
    {
        var ai = Build();
        var lesson = Seed(ai.Db, "A lesson being tested.", AiMemoryStatus.Proposed, kind: AiMemoryKind.Lesson);
        ai.Provider.On(Judge1, Answer("ok"), Answer("ok"));

        var trial = await ai.Gateway.AskAsync(Question() with { TrialMemoryIds = [lesson.Id] }, new RecordingSink(), CancellationToken.None);
        var normal = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { lesson.Id }, trial.MemoryIds);
        Assert.Empty(normal.MemoryIds);
    }

    [Fact]
    public async Task No_lesson_for_NONE_and_the_same_kind_of_question_is_not_sent_to_the_judge_twice()
    {
        var ai = Build(tools: DeskTools());
        ai.Provider.On(Judge1, CheckAnswers().Append(Answer("NONE"))
            .Concat(CheckAnswers()).Append(Answer("Lesson: read the totals.")).Append(Answer("Together: −₹1,00,000.")).ToArray());
        var check = Check(ai, out _);
        await check.RunForAsync(null, CancellationToken.None);
        Assert.Empty(await ai.Db.AiMemories.ToListAsync());

        // Second day: the Judge is asked again (NONE blocks nothing), and a lesson is proposed.
        await check.RunForAsync(null, CancellationToken.None);
        var lesson = await ai.Db.AiMemories.SingleAsync();

        // Third day: that lesson was tried and dropped lately, so the Judge is not asked.
        ai.Provider.On(Judge1, CheckAnswers());
        await check.RunForAsync(null, CancellationToken.None);
        Assert.Equal(2, await ai.Db.AiCalls.CountAsync(c => c.SystemPrompt == AssistantCheckAgent.LessonPrompt));
        Assert.Equal(lesson.Id, (await ai.Db.AiMemories.SingleAsync()).Id);
    }

    [Theory]
    [InlineData("NONE", null)]
    [InlineData("none, it was a slip", null)]
    [InlineData("\"Lesson: Read totals.netPnl for the day's total.\"", "Read totals.netPnl for the day's total.")]
    [InlineData("  Read   the run's   charges from get_run. ", "Read the run's charges from get_run.")]
    [InlineData("ok", null)]
    public void The_lesson_is_read_out_of_the_judges_reply(string reply, string? lesson)
    {
        Assert.Equal(lesson, AssistantCheckAgent.LessonFrom(reply));
    }

    // ---------- progress ----------

    [Fact]
    public async Task Progress_shows_by_day_the_check_score_the_memories_active_and_the_verdicts()
    {
        var ai = Build(tools: DeskTools());
        Seed(ai.Db, "Money is net of charges unless a tool says gross.");
        ai.Provider.On(Judge1, CheckAnswers().Append(Answer("NONE")).Append(Answer("Tuesday.")).ToArray());
        await Check(ai, out _).RunForAsync(null, CancellationToken.None);
        var answer = await ai.Gateway.AskAsync(Question("When do weekly options expire?"), new RecordingSink(), CancellationToken.None);
        await Memory(ai).FeedbackAsync(answer.CallId, 1, null, "upendra", "console", CancellationToken.None);

        var day = Assert.Single(await Memory(ai).ProgressAsync(7, CancellationToken.None));

        Assert.Equal((5, 6, 0.833, 1, 1, 0), (day.CheckPassed, day.CheckTotal, day.Score, day.ActiveMemories, day.Ups, day.Downs));
    }

    // ---------- the console's endpoints ----------

    [Fact]
    public void The_memory_endpoints_are_admin_only()
    {
        var attribute = Assert.Single(typeof(AiMemoryController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>());
        Assert.Equal(AuthorizationPolicies.AdminOnly, attribute.Policy);
    }

    [Fact]
    public async Task A_note_added_approved_and_listed_through_the_endpoints()
    {
        var ai = Build();
        var controller = Controller(ai);
        await Memory(ai).ProposeAsync(AiCatalog.DeskAssistant, "A lesson to approve.", "How many open legs are there?", null, null, CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(await controller.Add(new AiMemoryAdd(null, "Weekly options expire on Tuesday."), CancellationToken.None));
        var bad = Assert.IsType<ObjectResult>(await controller.Add(new AiMemoryAdd(null, ""), CancellationToken.None));
        var waiting = (AiMemoryList)Assert.IsType<OkObjectResult>(await controller.List(null, AiMemoryStatus.Proposed, CancellationToken.None)).Value!;
        var approved = (AiMemoryDto)Assert.IsType<OkObjectResult>(await controller.Update(waiting.Memories[0].Id, new AiMemoryUpdate(null, "active"), CancellationToken.None)).Value!;
        var all = (AiMemoryList)Assert.IsType<OkObjectResult>(await controller.List(null, null, CancellationToken.None)).Value!;

        Assert.Equal((201, "note", "upendra"), (created.StatusCode, ((AiMemoryDto)created.Value!).Kind, ((AiMemoryDto)created.Value!).CreatedBy));
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal(("active", "upendra"), (approved.Status, approved.DecidedBy));
        Assert.Equal((2, 0), (all.Counts.Active, all.Counts.Proposed));
        Assert.Equal(new AiMemoryAgent(AiCatalog.DeskAssistant, "Desk Assistant", true), all.Agents[0]);
        // The AI Trader too, for its lessons (2 Oct): they are listed, edited and retired here like any memory.
        Assert.Equal(new[] { AiCatalog.TradeReviewer, AiCatalog.NewsAnalyst, AiCatalog.IncidentExplainer, AiCatalog.AiTrader }, all.Agents.Skip(1).Select(a => a.Key));
        Assert.Equal("Weekly options expire on Tuesday.".Length + "A lesson to approve.".Length + "How many open legs are there?".Length, all.ActiveChars);
    }

    [Fact]
    public async Task A_verdict_through_the_endpoint_shows_on_the_call()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("₹2,22,414."));
        var answer = await ai.Gateway.AskAsync(Question("How much today?"), new RecordingSink(), CancellationToken.None);

        var result = (AiFeedbackResult)Assert.IsType<OkObjectResult>(
            await Controller(ai).Feedback(answer.CallId, new AiFeedbackRequest(-1, "Net after charges."), CancellationToken.None)).Value!;
        var detail = (AiCallDetail)Assert.IsType<OkObjectResult>(await ai.Controller().Call(answer.CallId, CancellationToken.None)).Value!;
        var list = (AiCallPage)Assert.IsType<OkObjectResult>(await ai.Controller().Calls(cancellationToken: CancellationToken.None)).Value!;

        Assert.Equal((-1, "Net after charges."), (result.Score, result.Note));
        Assert.Equal((-1, "Net after charges."), (detail.Feedback!.Score, detail.Feedback.Note));
        Assert.Equal(-1, list.Calls.Single(c => c.Id == answer.CallId).Feedback);
    }

    // ---------- helpers ----------

    private static AiMemory Seed(TradingDbContext db, string text, string status = AiMemoryStatus.Active, string agent = AiCatalog.DeskAssistant,
        string kind = AiMemoryKind.Note, string context = "")
    {
        var now = DateTime.UtcNow;
        var m = new AiMemory
        {
            AgentKey = agent, Kind = kind, Status = status, Text = text, Context = context, Source = AiMemorySource.Owner,
            CreatedBy = "upendra", CreatedUtc = now, UpdatedUtc = now, ActivatedUtc = status == AiMemoryStatus.Active ? now : null,
        };
        db.AiMemories.Add(m);
        db.SaveChanges();
        return m;
    }

    private static string SystemOf(Seen request) => JsonNode.Parse(request.Body)!["messages"]![0]!["content"]!.GetValue<string>();

    internal static AiMemoryService Memory(Services ai) => new(ai.Db, new AiMemoryBook(ai.Db, ai.Client, ai.Options), ai.Options);

    private static AiMemoryController Controller(Services ai) => new(ai.Db, Memory(ai), ai.Store, ai.Options)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "upendra"), new Claim(ClaimTypes.Role, "Admin")], "test")),
            },
        },
    };

    private static AssistantCheckAgent Check(Services ai, out Told told)
    {
        told = new Told();
        return new AssistantCheckAgent(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), ai.Toolbox, new AiSchedulerState(), ai.Options,
            NullLogger<AssistantCheckAgent>.Instance, null, Memory(ai), [told]);
    }

    /// <summary>The check's six questions over <see cref="DeskTools"/>, answered in order, the day's total wrong.</summary>
    private static Script[] CheckAnswers() =>
    [
        Answer("There are 2 runs today."),
        Answer("Together they made −₹1,00,000."),
        Answer("Run #339 has the lowest net P&L."),
        Answer("Run 339's net is −₹1,10,132.75 after charges."),
        Answer("It paid ₹59,774.75 in charges."),
        Answer("₹5,525 profit."),
    ];

    private static IAiTool[] DeskTools() =>
    [
        new FakeTool(AiToolNames.Runs, _ => new
        {
            runs = new object[]
            {
                new { runId = 339, netPnl = -110132.75, charges = 59774.75 },
                new { runId = 341, netPnl = 5132.50, charges = 1200.00 },
            },
            totals = new { runs = 2, netPnl = -105000.25 },
        }),
    ];

    private sealed class Told : IAiLessonNotifier
    {
        public List<AiMemory> Lessons { get; } = [];

        public Task LessonsProposedAsync(IReadOnlyList<AiMemory> lessons, CancellationToken cancellationToken)
        {
            Lessons.AddRange(lessons);
            return Task.CompletedTask;
        }
    }
}
