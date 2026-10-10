using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Amazon.BedrockRuntime.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;
using Effort = Anthropic.Models.Messages.Effort;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Claude on Amazon Bedrock, the second provider: what goes over the wire, how the stream is read, how its failures
/// are classified, how the gateway routes and falls back across providers, the chains while Bedrock is on, and the
/// move of search and memory to Titan.
/// </summary>
/// <remarks>
/// Nothing here reaches AWS. Claude answers from <see cref="AiTestKit.FakeClaude"/> through Anthropic's own SDK client
/// with signing switched off, in the Messages API's real event shapes; Titan from <see cref="AiTestKit.FakeRuntime"/>.
/// </remarks>
public class AiBedrockTests
{
    private static Services Bedrock(Action<AiSettings>? tweak = null, TradingDbContext? db = null, params IAiTool[] tools) =>
        Build(Settings(s =>
        {
            s.Bedrock.Enabled = true;
            tweak?.Invoke(s);
        }), db, tools);

    private static AiChatRequest Request(params AiMessage[] messages) =>
        new(messages.Length > 0 ? messages : [new AiMessage("user", "What is max pain?")], "You are the desk assistant.", 2048, 0.2);

    private static async Task<AiAttemptEnd> Stream(Services ai, string model, AiChatRequest request, List<AiStreamPiece>? pieces = null) =>
        await ai.ClaudeClient.StreamAsync(model, request, p =>
        {
            pieces?.Add(p);
            return ValueTask.CompletedTask;
        }, CancellationToken.None);

    // ---------- what goes over the wire ----------

    [Fact]
    public async Task A_request_is_the_messages_api_s_shape_with_adaptive_thinking_the_tier_s_effort_and_no_temperature()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeAnswer("OK"));

        await Stream(ai, Opus, Request() with { Tier = "judge" });

        var (path, body) = Assert.Single(ai.Claude.Requests);
        Assert.Equal("/anthropic/v1/messages", path);
        Assert.Equal("anthropic.claude-opus-5-5", body["model"]!.GetValue<string>());
        // The caller asked 2,048; thinking counts against the limit, so Claude gets at least 16,000.
        Assert.Equal(16_000, body["max_tokens"]!.GetValue<int>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("adaptive", body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("summarized", body["thinking"]!["display"]!.GetValue<string>());
        Assert.Equal("high", body["output_config"]!["effort"]!.GetValue<string>());
        // The current models reject sampling settings: the desk's 0.2 never goes.
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_p"]);
        Assert.Null(body["top_k"]);
        Assert.Null(body["tools"]);
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("What is max pain?", body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void The_effort_follows_the_tier_and_a_health_test_asks_at_low()
    {
        var bedrock = new AiBedrockSettings();

        Assert.Equal(Effort.High, BedrockClaudeClient.Effort("judge", bedrock));
        Assert.Equal(Effort.High, BedrockClaudeClient.Effort("analyst", bedrock));
        Assert.Equal(Effort.Low, BedrockClaudeClient.Effort("extract", bedrock));
        Assert.Equal(Effort.Low, BedrockClaudeClient.Effort(null, bedrock));
        Assert.Equal(Effort.Medium, BedrockClaudeClient.Effort("embed", bedrock));

        bedrock.EffortByTier["judge"] = "max";
        Assert.Equal(Effort.Max, BedrockClaudeClient.Effort("judge", bedrock));
    }

    [Fact]
    public async Task Haiku_4_5_is_sent_neither_thinking_nor_effort()
    {
        var ai = Bedrock();
        ai.Claude.On(Haiku45, ClaudeAnswer("OK", thinking: null));

        await Stream(ai, Haiku45, Request() with { Tier = "extract" });

        var body = Assert.Single(ai.Claude.Requests).Body;
        Assert.Null(body["thinking"]);
        Assert.Null(body["output_config"]);
        Assert.Null(body["temperature"]);
    }

    [Fact]
    public async Task The_stable_system_prompt_and_the_tools_carry_cache_breakpoints_and_the_changing_part_does_not()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeAnswer("OK"), ClaudeAnswer("OK"));
        const string stable = "You are the desk assistant.";
        var tools = new List<AiToolSpec>
        {
            new("get_runs", "A day's runs.", AiToolSchema.Object(("day", AiToolSchema.Text("A day."), false))),
            new("get_quotes", "Quotes.", AiToolSchema.Object(("runId", AiToolSchema.Integer("A run."), true))),
        };
        var request = new AiChatRequest([new AiMessage("user", "Runs today?")], stable + "\n\nMemory: a note.\nNow: Friday.", 2048, 0.2, tools)
        {
            Tier = "analyst",
            StableSystemChars = stable.Length,
        };

        await Stream(ai, Opus, request);
        await Stream(ai, Opus, request with { ToolsClosed = true });

        var body = ai.Claude.Requests[0].Body;
        var system = body["system"]!.AsArray();
        Assert.Equal(2, system.Count);
        Assert.Equal(stable, system[0]!["text"]!.GetValue<string>());
        Assert.Equal("ephemeral", system[0]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Null(system[1]!["cache_control"]);
        Assert.StartsWith("\n\nMemory: a note.", system[1]!["text"]!.GetValue<string>());

        var sent = body["tools"]!.AsArray();
        Assert.Equal(new[] { "get_runs", "get_quotes" }, sent.Select(t => t!["name"]!.GetValue<string>()));
        Assert.Null(sent[0]!["cache_control"]);
        Assert.Equal("ephemeral", sent[1]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("object", sent[1]!["input_schema"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", sent[1]!["input_schema"]!["properties"]!["runId"]!["type"]!.GetValue<string>());
        Assert.Equal(new[] { "runId" }, sent[1]!["input_schema"]!["required"]!.AsArray().Select(r => r!.GetValue<string>()));

        // Forced tool choice is refused by the current models: auto while the tools are open, none once closed.
        Assert.Equal("auto", body["tool_choice"]!["type"]!.GetValue<string>());
        Assert.Equal("none", ai.Claude.Requests[1].Body["tool_choice"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tool_calls_and_their_results_become_tool_use_and_tool_result_blocks_with_same_role_turns_merged()
    {
        var ai = Bedrock();
        ai.Claude.On(Sonnet, ClaudeAnswer("OK"));

        await Stream(ai, Sonnet, Request(
            new AiMessage("user", "Runs today?"),
            new AiMessage("assistant", "Looking.", [new AiToolCall("call-1", "get_runs", """{"day":"2026-10-09"}"""), new AiToolCall("call.2:x", "get_quotes", "")]),
            new AiMessage("tool", """{"rows":3}""", null, "call-1"),
            new AiMessage("tool", "", null, "call.2:x"),
            new AiMessage("user", "And the worst one?")));

        var messages = Assert.Single(ai.Claude.Requests).Body["messages"]!.AsArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, messages.Select(m => m!["role"]!.GetValue<string>()));

        var assistant = messages[1]!["content"]!.AsArray();
        Assert.Equal(new[] { "text", "tool_use", "tool_use" }, assistant.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("call-1", assistant[1]!["id"]!.GetValue<string>());
        Assert.Equal("2026-10-09", assistant[1]!["input"]!["day"]!.GetValue<string>());
        // An NVIDIA call id with characters Anthropic refuses is made safe, the same way on its result.
        Assert.Equal("call_2_x", assistant[2]!["id"]!.GetValue<string>());
        Assert.Empty(assistant[2]!["input"]!.AsObject());

        var user = messages[2]!["content"]!.AsArray();
        Assert.Equal(new[] { "tool_result", "tool_result", "text" }, user.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("call-1", user[0]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("""{"rows":3}""", user[0]!["content"]!.GetValue<string>());
        Assert.Equal("call_2_x", user[1]!["tool_use_id"]!.GetValue<string>());
        Assert.Equal("(no output)", user[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_turn_goes_back_to_the_model_that_wrote_it_with_its_thinking_unchanged_and_to_another_without_it()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeAnswer("OK")).On(Sonnet, ClaudeAnswer("OK"));
        var turn = new AiMessage("assistant", "Checking.", [new AiToolCall("toolu_1", "get_runs", "{}")])
        {
            Replay = new AiReplay(Opus,
            [
                new AiReplayBlock.Thinking("Plan the lookup.", "sig-opus-1"),
                new AiReplayBlock.Text("Checking."),
                new AiReplayBlock.ToolUse("toolu_1", "get_runs", "{}"),
            ]),
        };
        var request = Request(new AiMessage("user", "Runs?"), turn, new AiMessage("tool", "[]", null, "toolu_1"));

        await Stream(ai, Opus, request);
        await Stream(ai, Sonnet, request);

        var same = ai.Claude.Requests[0].Body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal(new[] { "thinking", "text", "tool_use" }, same.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("Plan the lookup.", same[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("sig-opus-1", same[0]!["signature"]!.GetValue<string>());

        // Thinking blocks are bound to the model that made them: Sonnet gets the turn's words and calls only.
        var other = ai.Claude.Requests[1].Body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal(new[] { "text", "tool_use" }, other.Select(b => b!["type"]!.GetValue<string>()));
    }

    // ---------- reading the stream ----------

    [Fact]
    public async Task A_stream_becomes_reasoning_and_answer_pieces_with_usage_that_counts_the_cache()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeAnswer("Max pain is 25,000.", thinking: "Find the strike.", input: 100, cacheRead: 800, cacheWrite: 50, output: 40));
        var pieces = new List<AiStreamPiece>();

        var end = await Stream(ai, Opus, Request(), pieces);

        Assert.Equal(new AiStreamPiece[]
        {
            new AiStreamPiece.Reasoning("Find the strike."),
            new AiStreamPiece.Content("Max pain "),
            new AiStreamPiece.Content("is 25,000."),
        }, pieces);
        Assert.Equal("stop", end.FinishReason);
        Assert.Empty(end.ToolCalls);
        Assert.Equal((950, 40, 990), (end.Usage!.PromptTokens, end.Usage.CompletionTokens, end.Usage.TotalTokens));
        Assert.Equal((800, 50), (end.Usage.CacheReadTokens, end.Usage.CacheWriteTokens));
        Assert.Equal(new AiReplayBlock[]
        {
            new AiReplayBlock.Thinking("Find the strike.", "sig-1"),
            new AiReplayBlock.Text("Max pain is 25,000."),
        }, end.Replay);
    }

    [Fact]
    public async Task A_tool_use_block_becomes_a_tool_call_with_its_arguments_whole()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeScript.Stream(
        [
            MessageStart(),
            .. ThinkingBlock(0, "Need the runs.", "sig-t"),
            .. ToolUseBlock(1, "toolu_9", "get_runs", """{"day":"2026-10-09"}"""),
            MessageDelta("tool_use"),
            MessageStop(),
        ]));

        var end = await Stream(ai, Opus, Request());

        var call = Assert.Single(end.ToolCalls);
        Assert.Equal(new AiToolCall("toolu_9", "get_runs", """{"day":"2026-10-09"}"""), call);
        Assert.Equal("tool_calls", end.FinishReason);
    }

    [Fact]
    public async Task Thinking_that_spends_every_token_is_an_empty_answer()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeAnswer("", thinking: "Long thought.", stop: "max_tokens"));

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Stream(ai, Opus, Request()));

        Assert.Equal("empty answer: ran out of tokens while reasoning", ex.Outcome);
    }

    [Fact]
    public async Task A_stream_cut_before_its_end_is_a_failure()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeScript.Stream([MessageStart(), .. TextBlock(0, "Half an answer")]));

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Stream(ai, Opus, Request()));

        Assert.Equal("stream broke off before the end", ex.Outcome);
    }

    [Fact]
    public async Task A_refusal_stop_is_a_failure_with_its_category()
    {
        var ai = Bedrock();
        ai.Claude.On(Fable, ClaudeScript.Stream(
        [
            MessageStart(),
            MessageDelta("refusal", stopDetails: new JsonObject { ["type"] = "refusal", ["category"] = "cyber", ["explanation"] = "Declined." }),
            MessageStop(),
        ]));

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Stream(ai, Fable, Request()));

        Assert.Equal("refused: the model's safeguards declined (cyber: Declined.)", ex.Outcome);
        Assert.False(AiGateway.IsCapacityRefusal(ex));
    }

    // ---------- Bedrock's refusals ----------

    [Fact]
    public async Task Throttling_from_the_endpoint_is_a_capacity_refusal()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeScript.Status(429, """{"type":"error","error":{"type":"rate_limit_error","message":"Too many requests, please wait."}}"""));

        var ex = await Assert.ThrowsAsync<AiAttemptFailedException>(() => Stream(ai, Opus, Request()));

        Assert.Equal(429, ex.HttpStatus);
        Assert.StartsWith("http 429: throttled by Bedrock", ex.Outcome);
        Assert.True(AiGateway.IsCapacityRefusal(ex));
    }

    [Theory]
    [InlineData(429, """{"message":"ThrottlingException: Too many tokens"}""", 429, true, "throttled by Bedrock")]
    [InlineData(503, """{"message":"ModelNotReadyException: the model is not ready"}""", 503, true, "model not ready")]
    [InlineData(503, """{"message":"ServiceUnavailableException"}""", 503, true, "overloaded")]
    [InlineData(529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", 503, true, "overloaded")]
    [InlineData(403, """{"message":"You don't have access to the model with the specified model ID."}""", 403, false,
        "Bedrock model access is not enabled for anthropic.claude-opus-5-5 in us-east-1")]
    [InlineData(404, """{"message":"The model does not exist."}""", 404, false,
        "Bedrock model access is not enabled for anthropic.claude-opus-5-5 in us-east-1")]
    [InlineData(403, """{"message":"The security token included in the request is invalid."}""", 403, false,
        "Bedrock refused the server's AWS credentials")]
    [InlineData(400, """{"type":"error","error":{"type":"invalid_request_error","message":"max_tokens: too large"}}""", 400, false,
        "http 400: max_tokens: too large")]
    public void Each_bedrock_refusal_says_what_it_means(int status, string body, int expectedStatus, bool capacity, string says)
    {
        var ex = BedrockClaudeClient.Failed(Opus, "us-east-1", status, body);

        Assert.Equal(expectedStatus, ex.HttpStatus);
        Assert.Equal(capacity, AiGateway.IsCapacityRefusal(ex));
        Assert.Contains(says, ex.Outcome);
    }

    [Fact]
    public void An_aws_account_number_in_a_refusal_is_masked()
    {
        var ex = BedrockClaudeClient.Failed(Opus, "us-east-1", 403,
            """{"message":"Account 123456789012 is not authorized to perform: bedrock-mantle:CreateInference"}""");

        Assert.DoesNotContain("123456789012", ex.Outcome);
        Assert.Contains("[account]", ex.Outcome);
    }

    // ---------- the gateway across providers ----------

    [Fact]
    public async Task A_refusal_moves_the_chain_to_its_next_model_without_cooling_the_one_that_refused()
    {
        var ai = Bedrock();
        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, [Fable, Opus], "test", null);
        ai.Claude.On(Fable, ClaudeScript.Stream([MessageStart(), MessageDelta("refusal"), MessageStop()]));
        ai.Claude.On(Opus, ClaudeAnswer("Max pain is 25,000."));
        var sink = new RecordingSink();

        var result = await ai.Gateway.AskAsync(Question(), sink, CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal(Opus, result.Model);
        Assert.Equal("Max pain is 25,000.", result.Text);
        Assert.StartsWith("refused:", result.Attempts[0].Outcome);
        Assert.Equal(1, result.Fallbacks);
        Assert.Contains(sink.Events, e => e.StartsWith($"fallback {Fable} -> {Opus}: refused:", StringComparison.Ordinal));
        Assert.Equal("unknown", ai.Health.State(Fable, DateTime.UtcNow).State);
    }

    [Fact]
    public async Task A_throttled_claude_model_is_asked_once_more_before_the_chain_moves_on()
    {
        var ai = Bedrock(s => s.CapacityRetrySeconds = 0.01);
        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, [Opus, Sonnet], "test", null);
        const string throttled = """{"type":"error","error":{"type":"rate_limit_error","message":"Too many requests"}}""";
        ai.Claude.On(Opus, ClaudeScript.Status(429, throttled), ClaudeScript.Status(429, throttled));
        ai.Claude.On(Sonnet, ClaudeAnswer("From Sonnet."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(Sonnet, result.Model);
        Assert.Equal(new[] { Opus, Opus, Sonnet }, result.Attempts.Select(a => a.Model));
        Assert.Equal(new int?[] { 429, 429, 200 }, result.Attempts.Select(a => a.HttpStatus));
    }

    [Fact]
    public async Task While_bedrock_is_off_its_models_are_skipped_with_the_reason_and_nothing_is_sent_to_aws()
    {
        var ai = Build();
        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, [Opus, Judge2], "test", null);
        ai.Provider.On(Judge2, Answer("From Nemotron Super."));
        var sink = new RecordingSink();

        var result = await ai.Gateway.AskAsync(Question(), sink, CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal(Judge2, result.Model);
        Assert.Equal(new AiAttempt(Opus, "skipped: Amazon Bedrock is off (Ai:Bedrock:Enabled is false)", 0, null, 1), result.Attempts[0]);
        Assert.Equal(0, result.Fallbacks);
        Assert.Contains($"attempt {Judge2} 1/1", sink.Events);
        Assert.Empty(ai.Claude.Requests);
        Assert.Equal("unknown", ai.Health.State(Opus, DateTime.UtcNow).State);
    }

    [Fact]
    public async Task A_chain_of_only_bedrock_models_is_refused_while_bedrock_is_off()
    {
        var ai = Build();
        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, [Opus, Sonnet], "test", null);

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Refused, result.Outcome);
        Assert.Equal(503, result.RefusalStatus);
        Assert.Contains($"{Opus}: Amazon Bedrock is off (Ai:Bedrock:Enabled is false)", result.Error);
        Assert.Empty(ai.Claude.Requests);
    }

    [Fact]
    public async Task A_chain_can_fall_back_from_claude_on_bedrock_to_nvidia()
    {
        var ai = Bedrock();
        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, [Opus, Judge2], "test", null);
        ai.Claude.On(Opus, ClaudeScript.Status(500, """{"type":"error","error":{"type":"api_error","message":"Internal error"}}"""));
        ai.Provider.On(Judge2, Answer("From Nemotron Super."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(Judge2, result.Model);
        Assert.Equal("From Nemotron Super.", result.Text);
        Assert.Equal(1, result.Fallbacks);
        Assert.Equal("http 500: Internal error", result.Attempts[0].Outcome);
        Assert.Null(result.CostUsd);
    }

    [Fact]
    public async Task In_a_tool_loop_claude_gets_its_own_turn_back_with_its_thinking_and_the_answer_is_priced()
    {
        var tool = new FakeTool(AiToolNames.Runs, _ => new { runs = 3 });
        var ai = Bedrock(tools: tool);
        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, [Opus], "test", null);
        ai.Claude.On(Opus,
            ClaudeScript.Stream(
            [
                MessageStart(input: 1000, cacheWrite: 3000),
                .. ThinkingBlock(0, "Need the runs.", "sig-round-1"),
                .. ToolUseBlock(1, "toolu_1", AiToolNames.Runs, "{}"),
                MessageDelta("tool_use", output: 100),
                MessageStop(),
            ]),
            ClaudeAnswer("Three runs today.", thinking: null, input: 1200, cacheRead: 3000, output: 50));

        var result = await ai.Gateway.AskAsync(Question("How many runs today?"), new RecordingSink(), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal("Three runs today.", result.Text);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(2, result.Rounds);

        // Round two: the assistant turn exactly as Opus wrote it, its thinking signed, then the tool's result.
        var messages = ai.Claude.Requests[1].Body["messages"]!.AsArray();
        var turn = messages[1]!["content"]!.AsArray();
        Assert.Equal(new[] { "thinking", "tool_use" }, turn.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("sig-round-1", turn[0]!["signature"]!.GetValue<string>());
        Assert.Equal("tool_result", messages[2]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("toolu_1", messages[2]!["content"]![0]!["tool_use_id"]!.GetValue<string>());

        // The Desk Assistant's prompt is the stable, cached part; the time after it is not.
        var system = ai.Claude.Requests[0].Body["system"]!.AsArray();
        Assert.Equal(AiCatalog.Agent(AiCatalog.DeskAssistant)!.SystemPrompt, system[0]!["text"]!.GetValue<string>());
        Assert.Contains("Now:", system[1]!["text"]!.GetValue<string>());

        // Usage over both rounds, cache included; cost at Opus 5.5's list price:
        // round 1: 1,000 in × $4 + 3,000 written × $5 + 100 out × $20; round 2: 1,200 × $4 + 3,000 read × $0.20 + 50 × $20.
        Assert.Equal((8200, 150, 3000, 3000),
            (result.Usage!.PromptTokens, result.Usage.CompletionTokens, result.Usage.CacheReadTokens, result.Usage.CacheWriteTokens));
        Assert.Equal(0.0274m, result.CostUsd);
    }

    [Fact]
    public async Task A_model_test_of_a_claude_model_asks_at_low_effort_and_the_probe_never_asks_one()
    {
        var ai = Bedrock();
        ai.Claude.On(Opus, ClaudeAnswer("OK"));

        var result = await ai.Gateway.AskAsync(new AiAskInput(AiCatalog.ModelTest, null, [new AiMessage("user", "Reply with OK.")], string.Empty,
            256, 0, string.Empty, "console", "upendra", 1, [Opus]), NullAiStreamSink.Instance, CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal("low", Assert.Single(ai.Claude.Requests).Body["output_config"]!["effort"]!.GetValue<string>());

        // A cooled Claude model is not probed: Bedrock bills every call.
        ai.Health.Record(Sonnet, false, 95, slow: true, "timeout", DateTime.UtcNow.AddMinutes(-11));
        var services = new ServiceCollection();
        services.AddScoped(_ => ai.Gateway);
        services.AddScoped(_ => ai.Db);
        var probe = new AiHealthProbe(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), ai.Health, ai.Options,
            NullLogger<AiHealthProbe>.Instance);

        await probe.ProbeAsync(CancellationToken.None);

        Assert.Single(ai.Claude.Requests);
    }

    // ---------- the console ----------

    [Fact]
    public async Task The_models_page_lists_the_claude_models_with_their_provider_list_price_and_whether_they_can_be_asked()
    {
        var off = await Build().Controller().Models(false, CancellationToken.None);
        var on = await Bedrock().Controller().Models(false, CancellationToken.None);

        var opusOff = Body<AiModelsDto>(off).Models.Single(m => m.Id == Opus);
        Assert.Equal(("Amazon Bedrock", "Claude Opus 5.5", "anthropic", false), (opusOff.Provider, opusOff.Name, opusOff.OwnedBy, opusOff.Available));
        Assert.Equal((4m, 20m, 0.20m), (opusOff.Price!.Input, opusOff.Price.Output, opusOff.Price.CacheRead));
        Assert.Equal("list price; Bedrock bills per AWS's page", opusOff.Price.Note);
        Assert.False(opusOff.InUse);

        var nemotron = Body<AiModelsDto>(off).Models.Single(m => m.Id == Judge1);
        Assert.Equal((AiCatalog.ProviderName, true), (nemotron.Provider, nemotron.Available));
        Assert.Null(nemotron.Price);

        var fable = Body<AiModelsDto>(on).Models.Single(m => m.Id == Fable);
        Assert.True(fable.Available);
        Assert.Equal((10m, 50m), (fable.Price!.Input, fable.Price.Output));
    }

    [Fact]
    public async Task While_bedrock_is_off_a_claude_model_test_and_the_re_embedding_are_refused_and_on_an_nvidia_chain_is()
    {
        var off = Build().Controller();
        var test = await off.TestModel(new AiModelTestRequest(Opus), CancellationToken.None);
        Assert.Equal(503, Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(test).StatusCode);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(off.Reembed());

        var on = Bedrock().Controller();
        var bad = await on.UpdateTier("judge", new AiTierUpdate([Judge1], null), CancellationToken.None);
        Assert.Contains("Amazon Bedrock is on", System.Text.Json.JsonSerializer.Serialize(
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(bad).Value));
        var good = await on.UpdateTier("judge", new AiTierUpdate([Opus, Sonnet], null), CancellationToken.None);
        Assert.Equal(new[] { Opus, Sonnet }, Body<AiTierDto>(good).Chain);
    }

    private static T Body<T>(Microsoft.AspNetCore.Mvc.IActionResult result) =>
        Assert.IsType<T>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result).Value);

    // ---------- the chains while Bedrock is on ----------

    [Fact]
    public async Task With_bedrock_on_every_chat_tier_is_claude_and_off_the_nvidia_chains_come_back()
    {
        var db = NewDb();
        var on = new AiTestKit.Monitor(Settings(s => s.Bedrock.Enabled = true));

        var state = await new AiSettingsStore(db, null, on).LoadAsync();

        Assert.Equal(new[] { Fable, Opus }, state.Tier("judge").Chain);
        Assert.Equal(new[] { Opus, Sonnet }, state.Tier("analyst").Chain);
        Assert.Equal(new[] { Sonnet, "bedrock:anthropic.claude-haiku-5-5" }, state.Tier("extract").Chain);
        Assert.All(state.Tiers.Where(t => t.Def.Chat), t => Assert.False(t.Overridden));
        Assert.All(state.Tiers.Where(t => t.Def.Chat).SelectMany(t => t.Chain), m => Assert.Contains(m, AiCatalog.BedrockModelIds));

        on.CurrentValue = Settings();
        var off = await new AiSettingsStore(db, null, on).LoadAsync();
        Assert.All(off.Tiers, t => Assert.Equal(t.Def.DefaultChain, t.Chain));
    }

    [Fact]
    public async Task With_bedrock_on_an_owner_s_chain_keeps_only_its_claude_models_and_its_rows_are_untouched()
    {
        var db = NewDb();
        var plain = new AiSettingsStore(db);
        await plain.SetTierChainAsync("judge", [Judge1, Sonnet], "test", null);
        await plain.SetTierChainAsync("analyst", [Judge2], "test", null);
        await plain.SetAgentChainAsync(AiCatalog.DeskAssistant, [Judge3], "test", null);

        var state = await new AiSettingsStore(db, null, new AiTestKit.Monitor(Settings(s => s.Bedrock.Enabled = true))).LoadAsync();

        Assert.Equal(new[] { Sonnet }, state.Tier("judge").Chain);
        Assert.True(state.Tier("judge").Overridden);
        Assert.Equal(new[] { Opus, Sonnet }, state.Tier("analyst").Chain);
        Assert.False(state.Tier("analyst").Overridden);
        var assistant = state.Agent(AiCatalog.DeskAssistant)!;
        Assert.False(assistant.ChainOverridden);
        Assert.Equal(state.Tier(assistant.Def.Tier).Chain, assistant.Chain);

        Assert.Equal(new[] { Judge1, Sonnet }, (await plain.LoadAsync()).Tier("judge").Chain);
    }

    [Fact]
    public void A_bedrock_id_is_a_valid_chain_member_and_while_bedrock_is_on_an_nvidia_one_is_not()
    {
        var known = new AiModelList([], "unavailable", null, null).KnownIds();

        Assert.Null(AiSettingsStore.ChainProblem([Opus, Judge1], known));
        Assert.Contains("Amazon Bedrock is on", AiSettingsStore.ChainProblem([Opus, Judge1], known, bedrockOnly: true));
        Assert.Null(AiSettingsStore.ChainProblem([Fable, Opus], known, bedrockOnly: true));
        Assert.Contains("not in the provider's model list", AiSettingsStore.ChainProblem(["bedrock:anthropic.claude-opus-9"], known));
        Assert.Contains("not a model id", AiSettingsStore.ChainProblem(["bedrock:Bad Id"], known));
    }

    [Fact]
    public void A_price_counts_uncached_input_cache_reads_and_writes_and_output()
    {
        var opus = new AiBedrockSettings().Prices["anthropic.claude-opus-5-5"];
        var usage = new AiUsage(1_000_000, 100_000, 1_100_000) { CacheReadTokens = 600_000, CacheWriteTokens = 100_000 };

        // 300k uncached × $4 + 600k read × $0.20 + 100k written × $5 + 100k out × $20.
        Assert.Equal(1.2m + 0.12m + 0.5m + 2m, opus.Cost(usage));

        var fable = new AiBedrockSettings().Prices["anthropic.claude-fable-5-1"];
        Assert.Equal((10m, 50m, 0.25m), (fable.Input, fable.Output, fable.CacheRead));
    }

    [Fact]
    public void The_container_builds_the_bedrock_services_off_and_no_aws_credential_is_a_setting()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ai:ApiKey"] = Key })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => NewDb());
        services.AddAi(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        var settings = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AiSettings>>().CurrentValue;
        Assert.False(settings.Bedrock.Enabled);
        Assert.IsType<BedrockClaudeClient>(scope.ServiceProvider.GetRequiredService<AiChatRouter>().For(Opus));
        Assert.IsType<NvidiaChatClient>(scope.ServiceProvider.GetRequiredService<AiChatRouter>().For(Judge1));
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AiGateway>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AiReembedder>());

        // The settings have nowhere to put an AWS key, profile or role: the SDKs use the machine's default chain alone.
        Assert.Equal(
            new[] { "EffortByTier", "EmbeddingConcurrency", "EmbeddingDimensions", "EmbeddingModel", "Enabled", "MinMaxTokens", "Prices", "Region" },
            typeof(AiBedrockSettings).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Bedrock_is_off_by_default_in_us_east_1()
    {
        var s = new AiSettings();

        Assert.False(s.Bedrock.Enabled);
        Assert.Equal("us-east-1", s.Bedrock.Region);
        Assert.False(s.ProviderConfigured);
        Assert.Equal("https://bedrock-mantle.us-east-1.api.aws/anthropic/v1/messages", AiCatalog.BedrockEndpoint(s.Bedrock.Region));
    }

    // ---------- embeddings: Titan, and the move to it ----------

    [Fact]
    public async Task Titan_is_asked_one_text_at_a_time_and_the_vectors_come_back_in_order()
    {
        var ai = Bedrock();
        var client = new BedrockEmbeddingClient(ai.Options, ai.Runtime);

        var (vectors, tokens) = await client.EmbedAsync("amazon.titan-embed-text-v2:0", ["max pain", "", "open interest"], CancellationToken.None);

        Assert.Equal(3, vectors.Count);
        Assert.All(vectors, v => Assert.Equal(32, v.Length));
        Assert.Equal(21, tokens);
        Assert.Equal(3, ai.Runtime.Requests.Count);
        var first = ai.Runtime.Requests.Single(r => r.Body["inputText"]!.GetValue<string>() == "max pain");
        Assert.Equal(("us-east-1", "amazon.titan-embed-text-v2:0"), (first.Region, first.Model));
        Assert.Equal(1024, first.Body["dimensions"]!.GetValue<int>());
        Assert.True(first.Body["normalize"]!.GetValue<bool>());
        // Titan takes no empty text.
        Assert.Contains(ai.Runtime.Requests, r => r.Body["inputText"]!.GetValue<string>() == "(empty)");
        Assert.Equal(FakeWords("open interest"), vectors[2]);
    }

    [Fact]
    public void An_aws_failure_says_what_it_means()
    {
        Assert.Equal(429, AwsBedrockRuntimeInvoker.Failed(new ThrottlingException("slow down"), "amazon.titan-embed-text-v2:0", "us-east-1").HttpStatus);
        Assert.Equal(503, AwsBedrockRuntimeInvoker.Failed(new ModelNotReadyException("warming"), "m", "us-east-1").HttpStatus);
        Assert.Equal("http 403: Bedrock model access is not enabled for amazon.titan-embed-text-v2:0 in us-east-1",
            AwsBedrockRuntimeInvoker.Failed(new AccessDeniedException("no"), "amazon.titan-embed-text-v2:0", "us-east-1").Outcome);
    }

    [Fact]
    public void Until_the_re_embedding_the_active_model_is_nvidia_s_and_it_comes_back_whenever_bedrock_is_off()
    {
        string titan = "bedrock:amazon.titan-embed-text-v2:0";

        Assert.Equal(titan, AiEmbedder.Target(Settings()));
        Assert.Equal(AiCatalog.EmbeddingModel, AiEmbedder.Resolve(null, Settings(s => s.Bedrock.Enabled = true)));
        Assert.Equal(AiCatalog.EmbeddingModel, AiEmbedder.Resolve(titan, Settings()));
        Assert.Equal(titan, AiEmbedder.Resolve(titan, Settings(s => s.Bedrock.Enabled = true)));
    }

    [Fact]
    public async Task The_re_embedding_makes_every_vector_with_titan_then_search_and_memory_use_only_titan_s()
    {
        string name = Guid.NewGuid().ToString("N");
        var ai = Bedrock(s => s.MemoryMaxItems = 1, NewDb(name));
        Seed(ai.Db);
        await ai.Db.SaveChangesAsync();
        var index = DocIndex(ai, name);

        var result = await new AiReembedder(ai.Db, index, ai.Options).RunAsync("upendra", CancellationToken.None);

        string titan = "bedrock:amazon.titan-embed-text-v2:0";
        Assert.True(result.Ok, result.Error);
        Assert.Equal((2, 3), (result.Memories, result.Passages));
        Assert.All(ai.Db.AiMemories, m => Assert.Equal((titan, 32), (m.EmbeddingModel, m.Embedding.Length)));
        Assert.All(ai.Db.AiDocChunks, c => Assert.Equal((titan, 32), (c.Model, c.Embedding.Length)));
        var row = await ai.Db.SystemSettings.SingleAsync(x => x.Key == AiEmbedder.ActiveModelKey);
        Assert.Equal((titan, "upendra"), (row.Value, row.UpdatedBy));
        var call = await ai.Db.AiCalls.SingleAsync(c => c.AgentKey == AiCatalog.Reembed);
        Assert.Equal(AiCallOutcome.Ok, call.Outcome);
        Assert.Equal(5, ai.Runtime.Requests.Count);
        Assert.Empty(ai.Provider.Requests);

        // Search: Titan's passages, the query embedded by Titan.
        var hits = await index.SearchAsync("option chain walls", 2, CancellationToken.None);
        Assert.Equal("modules/option_chain.md", hits[0].Path);
        Assert.Equal(titan, (await index.StatusAsync(CancellationToken.None)).Model);

        // Memory: past the budget the question is embedded by Titan too, and NVIDIA is never asked.
        var book = new AiMemoryBook(ai.Db, ai.Client, ai.Options, embedder: ai.Embedder);
        var recalled = await book.RecallAsync(AiCatalog.DeskAssistant, "What are the option chain walls?", CancellationToken.None);
        Assert.Equal("Walls are the strikes with the most open interest.", Assert.Single(recalled).Text);
        Assert.Empty(ai.Provider.Requests);
        Assert.Equal(7, ai.Runtime.Requests.Count);
    }

    [Fact]
    public async Task A_re_embedding_that_fails_part_way_changes_nothing()
    {
        string name = Guid.NewGuid().ToString("N");
        var ai = Bedrock(db: NewDb(name));
        Seed(ai.Db);
        await ai.Db.SaveChangesAsync();
        ai.Runtime.FailWith = new AiAttemptFailedException("http 429: throttled by Bedrock", 429);
        ai.Runtime.FailFrom = 3;

        var result = await new AiReembedder(ai.Db, DocIndex(ai, name), ai.Options).RunAsync("upendra", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("nothing was changed", result.Error);
        await using var fresh = NewDb(name);
        Assert.All(fresh.AiMemories, m => Assert.Equal(AiCatalog.EmbeddingModel, m.EmbeddingModel));
        Assert.All(fresh.AiDocChunks, c => Assert.Equal(AiCatalog.EmbeddingModel, c.Model));
        Assert.False(await fresh.SystemSettings.AnyAsync(x => x.Key == AiEmbedder.ActiveModelKey));
        Assert.Equal(AiCallOutcome.Failed, (await fresh.AiCalls.SingleAsync(c => c.AgentKey == AiCatalog.Reembed)).Outcome);
    }

    [Fact]
    public async Task A_re_embedding_is_refused_while_bedrock_is_off()
    {
        string name = Guid.NewGuid().ToString("N");
        var ai = Build(db: NewDb(name));

        var result = await new AiReembedder(ai.Db, DocIndex(ai, name), ai.Options).RunAsync("upendra", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Amazon Bedrock is off", result.Error);
        Assert.Empty(ai.Runtime.Requests);
        Assert.Empty(ai.Db.AiCalls);
    }

    private static AiDocIndex DocIndex(Services ai, string dbName)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(dbName));
        return new AiDocIndex(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), ai.Client, ai.Embedder);
    }

    /// <summary>Two memories and three doc passages, embedded by NVIDIA's model as the desk has them today.</summary>
    private static void Seed(TradingDbContext db)
    {
        var now = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);
        db.AiMemories.AddRange(
            new AiMemory
            {
                AgentKey = AiCatalog.DeskAssistant, Text = "Walls are the strikes with the most open interest.", CreatedUtc = now, UpdatedUtc = now,
                Embedding = FakeProvider.WordVector("walls"), EmbeddingModel = AiCatalog.EmbeddingModel,
            },
            new AiMemory
            {
                AgentKey = AiCatalog.DeskAssistant, Text = "Net P&L is after charges.", CreatedUtc = now, UpdatedUtc = now,
                Embedding = FakeProvider.WordVector("charges"), EmbeddingModel = AiCatalog.EmbeddingModel,
            });
        db.AiDocChunks.AddRange(
            Chunk("modules/option_chain.md", "Option chain › Walls", "The option chain walls are the strikes with the most open interest."),
            Chunk("modules/risk.md", "Risk › Overall stop", "Measured per day, net of charges."),
            Chunk("modules/ai.md", "AI workspace › Tiers", "A tier is an ordered chain of models."));

        AiDocChunk Chunk(string path, string heading, string text) => new()
        {
            Path = path, Heading = heading, Text = text, Hash = path, Embedding = FakeProvider.WordVector(text),
            Model = AiCatalog.EmbeddingModel, IndexedUtc = now,
        };
    }

    /// <summary>The fake Titan's vector for <paramref name="text"/>.</summary>
    private static float[] FakeWords(string text)
    {
        var v = new float[32];
        foreach (var word in text.ToLowerInvariant().Split((char[])[' ', '\n', '.', ',', '?', '›'], StringSplitOptions.RemoveEmptyEntries))
        {
            v[(int)(AiTestKit.StableHash(word) % 32)] += 1;
        }

        return v;
    }
}
