using System.Text;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI workspace's endpoints: what the pages read (agents, models, calls,
/// the day's numbers), what the owner may change, the shape a question must
/// have, and the event stream the Assistant reads.
/// </summary>
public class AiControllerTests
{
    // ---------- agents ----------

    [Fact]
    public async Task The_agent_list_says_which_agent_is_on_its_chain_and_its_last_call()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("Yes."));
        await ai.Gateway.AskAsync(Question(), NullAiStreamSink.Instance, CancellationToken.None);

        var list = Body<AiAgentList>(await ai.Controller().Agents(CancellationToken.None));

        var assistant = list.Agents.Single(a => a.Key == AiCatalog.DeskAssistant);
        Assert.Equal("on", assistant.Status);
        Assert.Equal("Judge", assistant.TierLabel);
        Assert.Equal(new[] { Judge1, Judge2, Judge3 }, assistant.Chain);
        Assert.Equal(AiCallOutcome.Ok, assistant.LastCall!.Outcome);
        Assert.Equal(Judge1, assistant.LastCall.Model);
        Assert.Equal(1, assistant.Today.Calls);
        Assert.Equal(30, assistant.Today.TotalTokens);
        Assert.Equal(10, list.Agents.Count(a => a.Status == "planned"));
        Assert.Null(list.Agents.Single(a => a.Key == "news-analyst").LastCall);
        Assert.NotEmpty(list.RuleBased);
    }

    [Fact]
    public async Task A_planned_agent_cannot_be_switched_on_and_an_unknown_one_is_not_found()
    {
        var controller = Build().Controller();

        Assert.IsType<ConflictObjectResult>(await controller.UpdateAgent("technical-analyst", new AiAgentUpdate(true, null, false, null), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateAgent("nobody", new AiAgentUpdate(true, null, false, null), CancellationToken.None));
    }

    [Fact]
    public async Task Switching_the_assistant_off_answers_the_agent_as_it_now_stands()
    {
        var controller = Build().Controller();

        var agent = Body<AiAgentDto>(await controller.UpdateAgent(AiCatalog.DeskAssistant, new AiAgentUpdate(false, null, false, "quiet day"), CancellationToken.None));

        Assert.Equal("off", agent.Status);
        Assert.Equal("upendra", agent.UpdatedBy);
        Assert.Equal("quiet day", agent.Reason);
    }

    [Fact]
    public async Task An_agent_s_own_chain_must_be_models_the_provider_lists()
    {
        var ai = Build();
        var controller = ai.Controller();

        var bad = await controller.UpdateAgent(AiCatalog.DeskAssistant, new AiAgentUpdate(null, ["vendor/made-up"], false, null), CancellationToken.None);
        var good = Body<AiAgentDto>(await controller.UpdateAgent(AiCatalog.DeskAssistant, new AiAgentUpdate(null, ["meta/llama-4-maverick", Judge1], false, null), CancellationToken.None));

        Assert.IsType<BadRequestObjectResult>(bad);
        Assert.Equal(new[] { "meta/llama-4-maverick", Judge1 }, good.Chain);
        Assert.True(good.ChainOverridden);
    }

    [Fact]
    public async Task The_embedding_tier_is_not_editable_yet()
    {
        var controller = Build().Controller();

        Assert.IsType<BadRequestObjectResult>(await controller.UpdateTier("embed", new AiTierUpdate(["nvidia/nemotron-3-embed-1b"], null), CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateTier("nope", new AiTierUpdate(null, null), CancellationToken.None));
    }

    // ---------- models ----------

    [Fact]
    public async Task The_models_in_use_come_first_with_their_tiers_and_the_rest_follow()
    {
        var ai = Build();

        var models = Body<AiModelsDto>(await ai.Controller().Models(false, CancellationToken.None));

        Assert.Equal("provider", models.Source);
        Assert.Equal(Judge1, models.Models[0].Id);
        Assert.Equal(new[] { "judge" }, models.Models[0].Tiers);
        Assert.Equal(new[] { AiCatalog.DeskAssistant, AiCatalog.TradeReviewer }, models.Models[0].Agents);
        Assert.Equal("First model of the Judge tier.", models.Models[0].Note);
        var super = models.Models.Single(m => m.Id == Judge2);
        Assert.Equal("Fallback 1 of the Judge tier; fallback 1 of the Analyst tier; fallback 1 of the Extract tier.", super.Note);
        var kimi = models.Models.Single(m => m.Id == Judge3);
        Assert.Equal("Fallback 2 of the Judge tier; first model of the Analyst tier.", kimi.Note);
        var extra = models.Models.Single(m => m.Id == "meta/llama-4-maverick");
        Assert.False(extra.InUse);
        var order = models.Models.ToList();
        Assert.True(order.IndexOf(extra) > order.FindLastIndex(m => m.InUse));
        // Named by a default chain but not in this provider's list: flagged, not hidden.
        Assert.False(models.Models.Single(m => m.Id == Extract1).Listed);
        Assert.True(models.Models.Single(m => m.Id == "nvidia/nemotron-3-embed-1b").Embedding);
        Assert.Contains(models.Local, m => m.Id == "ProsusAI/finbert");
    }

    [Fact]
    public async Task Without_a_key_the_model_list_says_so_and_still_names_the_models_in_use()
    {
        var ai = Build(Settings(s => s.ApiKey = ""));

        var models = Body<AiModelsDto>(await ai.Controller().Models(false, CancellationToken.None));

        Assert.Equal("unavailable", models.Source);
        Assert.Contains("NVIDIA_API_KEY", models.Error);
        Assert.Contains(models.Models, m => m.Id == Judge1 && m.InUse);
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task A_model_test_asks_that_model_alone_and_shows_as_its_last_test()
    {
        var ai = Build();
        ai.Provider.On(Judge3, Answer("OK"));
        var controller = ai.Controller();

        var test = Body<AiModelTestResult>(await controller.TestModel(new AiModelTestRequest(Judge3), CancellationToken.None));
        var models = Body<AiModelsDto>(await controller.Models(false, CancellationToken.None));

        Assert.True(test.Ok);
        Assert.Equal("OK", test.Answer);
        Assert.Equal(Judge3, ai.Provider.Requests.Last().Model);
        Assert.Equal(AiCatalog.ModelTest, (await ai.Db.AiCalls.SingleAsync()).AgentKey);
        Assert.True(models.Models.Single(m => m.Id == Judge3).LastTest!.Ok);
    }

    [Fact]
    public async Task An_embedding_model_or_an_unknown_one_cannot_be_chat_tested()
    {
        var controller = Build().Controller();

        Assert.IsType<BadRequestObjectResult>(await controller.TestModel(new AiModelTestRequest("nvidia/nemotron-3-embed-1b"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.TestModel(new AiModelTestRequest("vendor/made-up"), CancellationToken.None));
    }

    // ---------- calls and the day's numbers ----------

    [Fact]
    public async Task The_call_log_pages_newest_first_and_filters()
    {
        var ai = Build();
        var now = DateTime.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            ai.Db.AiCalls.Add(Row(now.AddMinutes(-i), i % 2 == 0 ? AiCallOutcome.Ok : AiCallOutcome.Failed));
        }

        await ai.Db.SaveChangesAsync();
        var controller = ai.Controller();

        var first = Body<AiCallPage>(await controller.Calls(take: 2));
        var second = Body<AiCallPage>(await controller.Calls(take: 2, beforeId: first.NextBeforeId));
        var failed = Body<AiCallPage>(await controller.Calls(outcome: AiCallOutcome.Failed));

        Assert.Equal(new long[] { 5, 4 }, first.Calls.Select(c => c.Id));
        Assert.Equal(4, first.NextBeforeId);
        Assert.Equal(new long[] { 3, 2 }, second.Calls.Select(c => c.Id));
        Assert.Equal(2, failed.Calls.Count);
        Assert.Null(failed.NextBeforeId);
        Assert.Equal("Desk Assistant", first.Calls[0].AgentName);
    }

    [Fact]
    public async Task A_call_stuck_running_long_after_any_model_could_answer_reads_as_failed()
    {
        var ai = Build();
        ai.Db.AiCalls.Add(Row(DateTime.UtcNow.AddHours(-2), AiCallOutcome.Running));
        ai.Db.AiCalls.Add(Row(DateTime.UtcNow.AddSeconds(-5), AiCallOutcome.Running));
        await ai.Db.SaveChangesAsync();

        var page = Body<AiCallPage>(await ai.Controller().Calls());

        Assert.Equal(AiCallOutcome.Running, page.Calls[0].Outcome);
        Assert.Equal(AiCallOutcome.Failed, page.Calls[1].Outcome);
        Assert.Contains("Never finished", page.Calls[1].Error);
    }

    [Fact]
    public async Task One_call_in_full_shows_every_model_tried_and_what_was_sent()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Script.Status(503));
        ai.Provider.On(Judge2, Answer("Answer.", reasoning: "Because."));
        var asked = await ai.Gateway.AskAsync(Question("Explain PCR"), NullAiStreamSink.Instance, CancellationToken.None);

        var call = Body<AiCallDetail>(await ai.Controller().Call(asked.CallId, CancellationToken.None));

        Assert.Equal(new[] { Judge1, Judge2 }, call.Attempts.Select(a => a.Model));
        Assert.Equal(503, call.Attempts[0].HttpStatus);
        Assert.Equal(1, call.Fallbacks);
        Assert.Equal("Explain PCR", call.Messages.Single().Content);
        Assert.Equal("Because.", call.Reasoning);
        Assert.Equal("Answer.", call.Answer);
        Assert.Equal("POST https://integrate.api.nvidia.com/v1/chat/completions", call.Request.Endpoint);
        Assert.IsType<NotFoundObjectResult>(await ai.Controller().Call(999, CancellationToken.None));
    }

    [Fact]
    public async Task Today_is_the_IST_day_and_counts_every_outcome()
    {
        var ai = Build();
        var istMidnight = AlgoTrading.Infrastructure.Services.IstTime.StartOfDayUtc(AlgoTrading.Infrastructure.Services.IstTime.DateOf(DateTime.UtcNow));
        ai.Db.AiCalls.Add(Row(istMidnight.AddMinutes(-1), AiCallOutcome.Ok));  // yesterday in IST
        ai.Db.AiCalls.Add(Row(istMidnight.AddMinutes(1), AiCallOutcome.Ok, seconds: 2, tokens: 100));
        ai.Db.AiCalls.Add(Row(istMidnight.AddMinutes(2), AiCallOutcome.Ok, seconds: 10, tokens: 50));
        ai.Db.AiCalls.Add(Row(istMidnight.AddMinutes(3), AiCallOutcome.Refused));
        await ai.Db.SaveChangesAsync();

        var overview = Body<AiOverview>(await ai.Controller().Overview(CancellationToken.None));

        Assert.Equal(3, overview.Today.Calls);
        Assert.Equal(2, overview.Today.Ok);
        Assert.Equal(1, overview.Today.Refused);
        Assert.Equal(150, overview.Today.TotalTokens);
        Assert.Equal(6, overview.Today.AvgSeconds);
        Assert.Equal(10, overview.Today.P95Seconds);
        Assert.True(overview.Provider.KeyConfigured);
        Assert.Equal(new AiAgentCounts(14, 4, 1, 3, 10), overview.Agents);
        Assert.Equal(4, overview.Tiers.Count);
    }

    // ---------- asking ----------

    [Theory]
    [InlineData("[]", "empty")]
    [InlineData("""[{"role":"system","content":"x"}]""", "user or assistant")]
    [InlineData("""[{"role":"user","content":"q"},{"role":"assistant","content":"a"}]""", "last message")]
    [InlineData("""[{"role":"user","content":"  "}]""", "empty")]
    public async Task A_question_of_the_wrong_shape_is_refused_before_any_model_is_asked(string messagesJson, string expected)
    {
        var ai = Build();
        var messages = System.Text.Json.JsonSerializer.Deserialize<List<AiAskMessage>>(messagesJson, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var result = await ai.Controller().Ask(new AiAskRequest(messages, null, null, null, null, null, null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains(expected, bad.Value!.ToString());
        Assert.Empty(ai.Provider.Requests);
        Assert.Empty(ai.Db.AiCalls);
    }

    [Theory]
    [InlineData("embed", null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, null, 2.0)]
    public async Task A_tier_or_limits_out_of_range_are_refused(string? tier, int? maxTokens, double? temperature)
    {
        var result = await Build().Controller().Ask(
            new AiAskRequest([new AiAskMessage("user", "q")], tier, null, maxTokens, temperature, null, null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task A_refusal_on_the_stream_is_plain_json_with_its_status_and_no_event_is_sent()
    {
        var ai = Build(Settings(s => s.ApiKey = ""));
        var controller = ai.Controller();
        var body = new MemoryStream();
        controller.HttpContext.Response.Body = body;

        var result = await controller.AskStream(new AiAskRequest([new AiAskMessage("user", "q")], null, null, null, null, "c-1", null), CancellationToken.None);

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(503, refused.StatusCode);
        Assert.Equal(0, body.Length);
    }

    [Fact]
    public async Task The_stream_sends_start_attempt_reasoning_delta_then_done()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("Forty.", reasoning: "Count."));
        var controller = ai.Controller();
        var body = new MemoryStream();
        controller.HttpContext.Response.Body = body;

        await controller.AskStream(new AiAskRequest([new AiAskMessage("user", "How many?")], null, null, null, null, "c-1", null), CancellationToken.None);

        string text = Encoding.UTF8.GetString(body.ToArray());
        var events = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e.Split('\n')[0]).ToList();
        Assert.Equal(new[] { "event: start", "event: attempt", "event: reasoning", "event: delta", "event: done" }, events);
        Assert.Contains("\"text\":\"Forty.\"", text);
        Assert.Contains($"\"model\":\"{Judge1}\"", text);
        Assert.Contains("\"totalTokens\":30", text);
        Assert.Equal("text/event-stream; charset=utf-8", controller.HttpContext.Response.ContentType);
    }

    [Fact]
    public async Task When_every_model_fails_the_stream_ends_with_an_error_event()
    {
        var ai = Build();
        var controller = ai.Controller();
        var body = new MemoryStream();
        controller.HttpContext.Response.Body = body;

        await controller.AskStream(new AiAskRequest([new AiAskMessage("user", "q")], null, null, null, null, null, null), CancellationToken.None);

        string text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Equal(2, text.Split("event: fallback").Length - 1);
        Assert.Contains("event: error", text);
        Assert.Contains("Every model failed", text);
    }

    private static AiCall Row(DateTime createdUtc, string outcome, double seconds = 1, int? tokens = null) => new()
    {
        CreatedUtc = createdUtc,
        CompletedUtc = outcome == AiCallOutcome.Running ? null : createdUtc.AddSeconds(seconds),
        AgentKey = AiCatalog.DeskAssistant,
        Source = "console",
        RequestedBy = "upendra",
        Outcome = outcome,
        Model = outcome == AiCallOutcome.Ok ? Judge1 : string.Empty,
        Seconds = seconds,
        TotalTokens = tokens,
        AttemptsJson = outcome == AiCallOutcome.Ok ? $$"""[{"model":"{{Judge1}}","outcome":"ok","seconds":{{seconds}},"httpStatus":200}]""" : "[]",
        Summary = "q",
    };

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);
}
