using System.Text.Json.Nodes;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Desk Assistant reading the desk: a model asks for tools, the gateway
/// runs them and asks again with what they found, until it answers.
/// </summary>
/// <remarks>
/// The provider's tool-call chunks are in the shape Nemotron 3 Ultra and
/// Super streamed on 30 Sep (one delta per call with id, name and the whole
/// arguments text, finish_reason tool_calls). What is pinned: the model is
/// given back exactly what the tool found; every way a tool call can go wrong
/// is answered to the model in words, not by failing the question; the limits
/// close the tools rather than loop; a model that failed is not asked again
/// in the same question; and the whole exchange is on the audit row.
/// </remarks>
public class AiToolLoopTests
{
    private static FakeTool Runs(Func<AiToolArgs, object>? answer = null) =>
        new(AiToolNames.Runs, answer ?? (_ => new { runs = new[] { new { runId = 412, strategy = "Short straddle", netPnl = -3672.5 } } }), rows: 1);

    [Fact]
    public async Task A_model_that_asks_for_a_tool_gets_its_result_and_answers_from_it()
    {
        var runs = Runs();
        var ai = Build(tools: runs);
        ai.Provider.On(Judge1, Tools((AiToolNames.Runs, "{}")), Answer("Run 412 lost ₹3,672.50 (get_runs, 15:30 IST)."));
        var sink = new RecordingSink();

        var result = await ai.Gateway.AskAsync(Question("How did my runs do today?"), sink, CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal("Run 412 lost ₹3,672.50 (get_runs, 15:30 IST).", result.Text);
        Assert.Equal(2, result.Rounds);
        Assert.Equal(1, runs.Calls);
        var step = Assert.Single(result.Tools);
        Assert.True(step.Ok);
        Assert.Equal(AiToolNames.Runs, step.Name);
        Assert.Equal(new AiUsage(120, 15, 135), result.Usage);
        Assert.Contains($"tool {AiToolNames.Runs} ok", sink.Events);
        Assert.Contains($"attempt {Judge1} 1/3 round 2", sink.Events);

        // The second request carries the model's own tool call and the tool's answer, verbatim.
        var second = JsonNode.Parse(ai.Provider.Requests[1].Body)!["messages"]!.AsArray();
        var asked = second[^2]!;
        var answered = second[^1]!;
        Assert.Equal("assistant", asked["role"]!.GetValue<string>());
        Assert.Equal(AiToolNames.Runs, asked["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("tool", answered["role"]!.GetValue<string>());
        Assert.Equal(asked["tool_calls"]![0]!["id"]!.GetValue<string>(), answered["tool_call_id"]!.GetValue<string>());
        var payload = JsonNode.Parse(answered["content"]!.GetValue<string>())!;
        Assert.Equal(-3672.5, payload["data"]!["runs"]![0]!["netPnl"]!.GetValue<double>());
        Assert.Equal("2026-09-30 15:30:00 IST", payload["asOf"]!.GetValue<string>());

        var row = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal(2, row.Rounds);
        Assert.Equal(1, row.ToolCalls);
        var stored = JsonNode.Parse(row.ToolsJson)!.AsArray().Single()!;
        Assert.Equal(AiToolNames.Runs, stored["name"]!.GetValue<string>());
        Assert.Contains("-3672.5", stored["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_tools_are_offered_with_their_schema_and_the_model_is_told_the_time()
    {
        var ai = Build(tools: Runs());
        ai.Provider.On(Judge1, Answer("Nothing to read."));

        await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        var body = JsonNode.Parse(ai.Provider.Requests.Single().Body)!;
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.Equal(AiToolNames.Runs, body["tools"]![0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("object", body["tools"]![0]!["function"]!["parameters"]!["type"]!.GetValue<string>());
        Assert.Contains("IST.", body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Contains("\nNow: ", (await ai.Db.AiCalls.SingleAsync()).SystemPrompt);
    }

    [Fact]
    public async Task A_model_test_is_offered_no_tools()
    {
        var ai = Build(tools: Runs());
        ai.Provider.On(Judge3, Answer("OK"));

        await ai.Controller().TestModel(new AlgoTrading.Api.Controllers.AiModelTestRequest(Judge3), CancellationToken.None);

        Assert.Null(JsonNode.Parse(ai.Provider.Requests.Last().Body)!["tools"]);
    }

    [Theory]
    [InlineData("get_everything", "{}", "No tool named get_everything")]
    [InlineData(AiToolNames.Runs, "{not json", "not valid JSON")]
    [InlineData(AiToolNames.Runs, """{"runId":"abc"}""", "runId must be a whole number")]
    public async Task A_tool_call_gone_wrong_is_answered_to_the_model_in_words(string name, string args, string expected)
    {
        var runs = Runs(a => new { runId = a.Long("runId") });
        var ai = Build(tools: runs);
        ai.Provider.On(Judge1, Tools((name, args)), Answer("I could not read that."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        var step = Assert.Single(result.Tools);
        Assert.False(step.Ok);
        Assert.Contains(expected, step.Error);
        var told = JsonNode.Parse(ai.Provider.Requests[1].Body)!["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
        Assert.Contains(expected, told);
    }

    [Fact]
    public async Task A_tool_that_throws_is_reported_to_the_model_without_its_internals()
    {
        var broken = Runs(_ => throw new InvalidOperationException("connection string Host=db;Password=secret"));
        var ai = Build(tools: broken);
        ai.Provider.On(Judge1, Tools((AiToolNames.Runs, "{}")), Answer("The runs could not be read."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        var step = Assert.Single(result.Tools);
        Assert.Equal("The tool failed on the server (InvalidOperationException).", step.Error);
        Assert.DoesNotContain("secret", ai.Provider.Requests[1].Body);
    }

    [Fact]
    public async Task Past_the_call_limit_a_tool_is_refused_and_the_tools_close_for_the_last_round()
    {
        var runs = Runs(a => new { runId = a.Long("runId") });
        var ai = Build(Settings(s => s.MaxToolCalls = 2), tools: runs);
        ai.Provider.On(Judge1,
            Tools((AiToolNames.Runs, """{"runId":1}"""), (AiToolNames.Runs, """{"runId":2}"""), (AiToolNames.Runs, """{"runId":3}""")),
            Answer("Two of three runs read."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(2, runs.Calls);
        Assert.Equal(new[] { true, true, false }, result.Tools.Select(t => t.Ok));
        Assert.Contains("limit of 2 tool calls", result.Tools[2].Error);
        Assert.Equal("none", JsonNode.Parse(ai.Provider.Requests[1].Body)!["tool_choice"]!.GetValue<string>());
        Assert.Equal("Two of three runs read.", result.Text);
    }

    [Fact]
    public async Task A_model_that_keeps_asking_after_the_tools_close_hands_over_to_the_next()
    {
        var ai = Build(Settings(s => s.MaxToolRounds = 1), tools: Runs());
        ai.Provider.On(Judge1, Tools((AiToolNames.Runs, "{}")), Tools((AiToolNames.Runs, """{"runId":9}""")));
        ai.Provider.On(Judge2, Answer("From what was read: one run, down ₹3,672.50."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(Judge2, result.Model);
        Assert.Contains(result.Attempts, a => a.Model == Judge1 && a.Round == 2 && a.Outcome == "asked for a tool after the tools were closed");
        Assert.Single(result.Tools);
    }

    [Fact]
    public async Task A_model_whose_failure_cost_a_long_wait_is_not_asked_again_in_a_later_round()
    {
        var ai = Build(Settings(s => { s.FirstTokenTimeoutSeconds = 0.3; s.SlowFailureSeconds = 0.2; }), tools: Runs());
        ai.Provider.On(Judge1, Script.SseThenHang());
        ai.Provider.On(Judge2, Tools((AiToolNames.Runs, "{}")), Answer("Done."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { Judge1, Judge2, Judge2 }, ai.Provider.Requests.Select(r => r.Model));
        Assert.Equal(1, result.Fallbacks);
        Assert.Equal(new[] { 1, 1, 2 }, result.Attempts.Select(a => a.Round));
    }

    [Fact]
    public async Task A_model_that_refused_at_once_is_asked_again_in_a_later_round()
    {
        // 30 Sep: Ultra refused "overloaded" in 0.6 s; skipping it for the rest of the
        // question left only a model queueing for 90 s when Super refused too.
        var ai = Build(tools: Runs());
        ai.Provider.On(Judge1, Script.Status(429), Answer("Ultra again, with the runs read."));
        ai.Provider.On(Judge2, Tools((AiToolNames.Runs, "{}")));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { Judge1, Judge2, Judge1 }, ai.Provider.Requests.Select(r => r.Model));
        Assert.Equal(Judge1, result.Model);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(429)]
    public async Task A_capacity_refusal_is_asked_once_more_after_a_pause_before_the_chain_moves_on(int status)
    {
        var ai = Build(Settings(s => s.CapacityRetrySeconds = 0.05));
        ai.Provider.On(Judge1, Script.Status(status), Answer("Room now."));
        var sink = new RecordingSink();

        var result = await ai.Gateway.AskAsync(Question(), sink, CancellationToken.None);

        Assert.Equal(Judge1, result.Model);
        Assert.Equal(new[] { Judge1, Judge1 }, ai.Provider.Requests.Select(r => r.Model));
        Assert.Contains(sink.Events, e => e.StartsWith($"fallback {Judge1} -> {Judge1}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_overloaded_stream_counts_as_a_capacity_refusal_and_is_asked_again_only_once()
    {
        var ai = Build(Settings(s => s.CapacityRetrySeconds = 0.05));
        ai.Provider.On(Judge1,
            Script.Sse("""{"error":{"message":"Service temporarily overloaded"}}"""),
            Script.Sse("""{"error":{"message":"Service temporarily overloaded"}}"""));
        ai.Provider.On(Judge2, Answer("Super answered."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { Judge1, Judge1, Judge2 }, ai.Provider.Requests.Select(r => r.Model));
        Assert.Equal(Judge2, result.Model);
    }

    [Fact]
    public async Task A_refusal_that_is_not_about_capacity_moves_straight_on()
    {
        var ai = Build(Settings(s => s.CapacityRetrySeconds = 0.05));
        ai.Provider.On(Judge1, Script.Status(404, """{"detail":"Not found for account"}"""));
        ai.Provider.On(Judge2, Answer("Next model."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { Judge1, Judge2 }, ai.Provider.Requests.Select(r => r.Model));
    }

    [Fact]
    public async Task The_same_call_twice_in_one_question_is_read_once()
    {
        var runs = Runs();
        var ai = Build(tools: runs);
        ai.Provider.On(Judge1, Tools((AiToolNames.Runs, "{}")), Tools((AiToolNames.Runs, "{}")), Answer("Same data."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(1, runs.Calls);
        Assert.Equal(2, result.Tools.Count);
        Assert.StartsWith("Same call as before", result.Tools[1].Summary);
        Assert.Equal(result.Tools[0].Result, result.Tools[1].Result);
    }

    [Fact]
    public async Task A_result_too_long_to_send_asks_the_model_to_narrow_down()
    {
        var ai = Build(Settings(s => s.MaxToolResultChars = 200), tools: Runs(_ => new { text = new string('x', 500) }));
        ai.Provider.On(Judge1, Tools((AiToolNames.Runs, "{}")), Answer("Too much to read."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        var step = Assert.Single(result.Tools);
        Assert.False(step.Ok);
        Assert.Contains("over the 200 limit", step.Error);
        Assert.DoesNotContain(new string('x', 500), ai.Provider.Requests[1].Body);
    }

    [Fact]
    public async Task What_a_model_says_before_asking_for_tools_is_kept_as_working_not_the_answer()
    {
        var ai = Build(tools: Runs());
        ai.Provider.On(Judge1,
            Script.Sse(
                Chunk(reasoning: "Need the runs."),
                """{"choices":[{"index":0,"delta":{"content":"Let me look.","tool_calls":[{"index":0,"id":"c1","type":"function","function":{"name":"get_runs","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""",
                "[DONE]"),
            Answer("One run, down ₹3,672.50.", reasoning: "Read it."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal("One run, down ₹3,672.50.", result.Text);
        Assert.Contains("[round 1] Need the runs.", result.Reasoning);
        Assert.Contains("Let me look.", result.Reasoning);
        Assert.Contains("[answer] Read it.", result.Reasoning);
    }

    [Fact]
    public async Task The_call_log_shows_each_tool_call_and_the_agent_its_tools()
    {
        var ai = Build(tools: Runs());
        ai.Provider.On(Judge1, Tools((AiToolNames.Runs, "{}")), Answer("Read."));
        var asked = await ai.Gateway.AskAsync(Question(), NullAiStreamSink.Instance, CancellationToken.None);
        var controller = ai.Controller();

        var detail = Assert.IsType<AlgoTrading.Api.Controllers.AiCallDetail>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Call(asked.CallId, CancellationToken.None)).Value);
        var agents = Assert.IsType<AlgoTrading.Api.Controllers.AiAgentList>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Agents(CancellationToken.None)).Value);

        Assert.Equal(1, detail.ToolCalls);
        Assert.Equal(2, detail.Rounds);
        var tool = Assert.Single(detail.Tools);
        Assert.Equal(AiToolNames.Runs, tool.Name);
        Assert.Contains("netPnl", tool.Result);
        Assert.Equal(new[] { 1, 2 }, detail.Attempts.Select(a => a.Round));
        Assert.Equal(new[] { AiToolNames.Runs }, agents.Agents.Single(a => a.Key == AiCatalog.DeskAssistant).Tools.Select(t => t.Name));
    }
}
