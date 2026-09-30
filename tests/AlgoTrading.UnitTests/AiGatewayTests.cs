using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The one door every model call goes through: the chain and its fallbacks,
/// the audit row, the refusals, and the key that never lands anywhere.
/// </summary>
/// <remarks>
/// The provider is scripted per model (<see cref="AiTestKit.FakeProvider"/>),
/// in the NVIDIA stream's real shape: reasoning_content deltas, content
/// deltas, a usage chunk, then [DONE] (captured from Nemotron 3 Ultra on 30 Sep).
/// </remarks>
public class AiGatewayTests
{
    [Fact]
    public async Task The_first_model_s_answer_is_streamed_and_recorded_with_its_usage()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("Max pain is the strike where option buyers lose most.", reasoning: "Define it."));
        var sink = new RecordingSink();

        var result = await ai.Gateway.AskAsync(Question(), sink, CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal(Judge1, result.Model);
        Assert.Equal("Max pain is the strike where option buyers lose most.", result.Text);
        Assert.Equal(new AiUsage(20, 10, 30), result.Usage);
        Assert.Equal(0, result.Fallbacks);
        Assert.Equal(
            [$"start {result.CallId}", $"attempt {Judge1} 1/3", "reasoning Define it.", "delta Max pain is the strike where option buyers lose most."],
            sink.Events);

        var row = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal(AiCallOutcome.Ok, row.Outcome);
        Assert.Equal(Judge1, row.Model);
        Assert.Equal("Define it.", row.Reasoning);
        Assert.Equal("stop", row.FinishReason);
        Assert.Equal((20, 10, 30), (row.PromptTokens, row.CompletionTokens, row.TotalTokens));
        Assert.Equal("What is max pain?", row.Summary);
        Assert.Equal(AiCatalog.Agent(AiCatalog.DeskAssistant)!.SystemPrompt, row.SystemPrompt);
        Assert.Equal("upendra", row.RequestedBy);
        Assert.NotNull(row.CompletedUtc);
        Assert.Equal(new[] { Judge1, Judge2, Judge3 }, JsonSerializer.Deserialize<string[]>(row.ChainJson));
    }

    [Fact]
    public async Task A_busy_model_hands_over_to_the_next_and_the_fallback_is_visible()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Script.Status(429, """{"detail":"Too many requests"}"""));
        ai.Provider.On(Judge2, Answer("From Kimi."));
        var sink = new RecordingSink();

        var result = await ai.Gateway.AskAsync(Question(), sink, CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, result.Outcome);
        Assert.Equal(Judge2, result.Model);
        Assert.Equal(1, result.Fallbacks);
        Assert.Equal("http 429: Too many requests", result.Attempts[0].Outcome);
        Assert.Equal(429, result.Attempts[0].HttpStatus);
        Assert.Contains($"fallback {Judge1} -> {Judge2}: http 429: Too many requests", sink.Events);

        var attempts = JsonNode.Parse((await ai.Db.AiCalls.SingleAsync()).AttemptsJson)!.AsArray();
        Assert.Equal(new[] { Judge1, Judge2 }, attempts.Select(a => a!["model"]!.GetValue<string>()));
        Assert.Equal("ok", attempts[1]!["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_stream_that_breaks_off_is_dropped_whole_so_an_answer_is_never_two_models_glued()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Script.Sse(Chunk(content: "Half an ans")));
        ai.Provider.On(Judge2, Answer("A whole answer."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal("A whole answer.", result.Text);
        Assert.Equal("stream broke off before the end", result.Attempts[0].Outcome);
        Assert.Equal("A whole answer.", (await ai.Db.AiCalls.SingleAsync()).Answer);
    }

    [Fact]
    public async Task A_model_that_never_starts_times_out_on_the_first_token_limit()
    {
        var ai = Build(Settings(s => s.FirstTokenTimeoutSeconds = 0.3));
        ai.Provider.On(Judge1, Script.SseThenHang());
        ai.Provider.On(Judge2, Answer("Kimi was quicker."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(Judge2, result.Model);
        Assert.StartsWith("timeout: no answer within", result.Attempts[0].Outcome);
    }

    [Fact]
    public async Task A_model_that_goes_silent_mid_answer_times_out_on_the_idle_limit()
    {
        var ai = Build(Settings(s => s.IdleTimeoutSeconds = 0.3));
        ai.Provider.On(Judge1, Script.SseThenHang(Chunk(reasoning: "Let me think")));
        ai.Provider.On(Judge2, Answer("Done."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(Judge2, result.Model);
        Assert.StartsWith("timeout: silent for", result.Attempts[0].Outcome);
    }

    [Fact]
    public async Task A_reasoning_model_that_spends_every_token_thinking_is_an_empty_answer_not_a_success()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Script.Sse(Chunk(reasoning: "Hmm, many thoughts"), Chunk(finish: "length"), "[DONE]"));
        ai.Provider.On(Judge2, Answer("Short answer."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal("empty answer: ran out of tokens while reasoning", result.Attempts[0].Outcome);
        Assert.Equal("Short answer.", result.Text);
    }

    [Fact]
    public async Task When_every_model_fails_the_error_names_each_one_and_no_answer_is_kept()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Script.Status(503));
        ai.Provider.On(Judge2, Script.Unreachable());
        ai.Provider.On(Judge3, Script.Sse(Chunk(content: "partial")));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Failed, result.Outcome);
        Assert.Equal(string.Empty, result.Model);
        Assert.Equal(
            $"Every model failed: {Judge1} http 503; {Judge2} unreachable (Unknown); {Judge3} stream broke off before the end",
            result.Error);
        var row = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal(AiCallOutcome.Failed, row.Outcome);
        Assert.Equal(string.Empty, row.Answer);
    }

    [Fact]
    public async Task A_tier_picked_for_one_question_walks_that_tier_s_chain()
    {
        var ai = Build();
        ai.Provider.On(Extract1, Answer("Fast."));

        var result = await ai.Gateway.AskAsync(Question(tier: "extract"), new RecordingSink(), CancellationToken.None);

        Assert.Equal(Extract1, result.Model);
        Assert.Equal(Extract1, ai.Provider.Requests.Single().Model);
        Assert.Equal("extract", (await ai.Db.AiCalls.SingleAsync()).Tier);
    }

    [Fact]
    public async Task A_switched_off_agent_is_refused_and_recorded_without_asking_any_model()
    {
        var ai = Build();
        await ai.Store.SetAgentEnabledAsync(AiCatalog.DeskAssistant, false, "upendra", "testing the switch");

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Refused, result.Outcome);
        Assert.Equal(409, result.RefusalStatus);
        Assert.Contains("switched off", result.Error);
        Assert.Contains("testing the switch", result.Error);
        Assert.Empty(ai.Provider.Requests);
        Assert.Equal(AiCallOutcome.Refused, (await ai.Db.AiCalls.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task A_planned_agent_cannot_call_a_model()
    {
        var ai = Build();

        var result = await ai.Gateway.AskAsync(Question(agent: "technical-analyst"), new RecordingSink(), CancellationToken.None);

        Assert.Equal(409, result.RefusalStatus);
        Assert.Contains("not built yet", result.Error);
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task Without_a_key_the_call_is_refused_with_where_the_key_goes()
    {
        var ai = Build(Settings(s => s.ApiKey = ""));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(503, result.RefusalStatus);
        Assert.Contains("NVIDIA_API_KEY", result.Error);
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task Past_the_per_user_limit_a_question_is_refused_with_when_to_ask_again()
    {
        var ai = Build(Settings(s => s.PerUserPer10Min = 1));
        ai.Provider.On(Judge1, Answer("One."));

        var first = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);
        var second = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(AiCallOutcome.Ok, first.Outcome);
        Assert.Equal(429, second.RefusalStatus);
        Assert.InRange(second.RetryAfterSeconds, 1, 600);
        Assert.Single(ai.Provider.Requests);
        Assert.Equal(0, ai.Limiter.InFlight);
    }

    [Fact]
    public async Task The_key_goes_in_the_header_only_and_is_scrubbed_if_the_provider_echoes_it()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Script.Status(401, $$"""{"detail":"Bad key {{Key}}"}"""));
        ai.Provider.On(Judge2, Script.Sse($$$"""{"error":{"message":"refused for {{{Key}}}"}}"""));
        ai.Provider.On(Judge3, Script.Status(500));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.All(ai.Provider.Requests, r => Assert.Equal($"Bearer {Key}", r.Authorization));
        Assert.All(ai.Provider.Requests, r => Assert.DoesNotContain(Key, r.Body));
        Assert.DoesNotContain(Key, result.Error);
        Assert.Contains("[key]", result.Error);

        var row = await ai.Db.AiCalls.SingleAsync();
        string everything = string.Join('\n', typeof(AiCall).GetProperties().Select(p => p.GetValue(row)?.ToString() ?? string.Empty));
        Assert.DoesNotContain(Key, everything);
    }

    [Fact]
    public async Task A_question_the_asker_stops_is_recorded_as_cancelled_with_what_had_arrived()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Script.SseThenHang(Chunk(content: "The first half")));
        using var stop = new CancellationTokenSource();
        var sink = new RecordingSink { OnDelta = async _ => await stop.CancelAsync() };

        var result = await ai.Gateway.AskAsync(Question(), sink, stop.Token);

        Assert.Equal(AiCallOutcome.Cancelled, result.Outcome);
        var row = await ai.Db.AiCalls.SingleAsync();
        Assert.Equal(AiCallOutcome.Cancelled, row.Outcome);
        Assert.Equal("The first half", row.Answer);
        Assert.Equal("cancelled", JsonNode.Parse(row.AttemptsJson)!.AsArray().Single()!["outcome"]!.GetValue<string>());
        Assert.Equal(0, ai.Limiter.InFlight);
    }

    [Fact]
    public async Task A_console_connection_that_breaks_mid_answer_is_not_blamed_on_the_model()
    {
        var ai = Build();
        ai.Provider.On(Judge1, Answer("Answer."));
        ai.Provider.On(Judge2, Answer("Should never be asked."));
        var sink = new RecordingSink { OnDelta = _ => throw new IOException("client went away") };

        var result = await ai.Gateway.AskAsync(Question(), sink, CancellationToken.None);

        Assert.Equal(AiCallOutcome.Cancelled, result.Outcome);
        Assert.Single(ai.Provider.Requests);
        Assert.DoesNotContain(sink.Events, e => e.StartsWith("fallback", StringComparison.Ordinal));
        Assert.Equal("The console's connection closed mid-answer.", (await ai.Db.AiCalls.SingleAsync()).Error);
        Assert.Equal(0, ai.Limiter.InFlight);
    }

    [Fact]
    public void The_request_streams_asks_for_usage_and_puts_the_system_prompt_first()
    {
        var body = JsonNode.Parse(NvidiaChatClient.RequestBody(Judge1, new AiChatRequest(
            [new AiMessage("user", "Hi"), new AiMessage("assistant", "Hello"), new AiMessage("user", "Again")], "Be brief.", 512, 0.2)))!;

        Assert.Equal(Judge1, body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.Equal(512, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{"reasoning_content":"why"},"finish_reason":null}]}""", null, "why", null)]
    [InlineData("""{"choices":[{"delta":{"reasoning":"why"}}]}""", null, "why", null)]
    [InlineData("""{"choices":[{"delta":{"content":"OK","role":"assistant"},"finish_reason":"stop"}]}""", "OK", null, "stop")]
    [InlineData("""{"choices":[],"usage":{"prompt_tokens":1}}""", null, null, null)]
    [InlineData("""not json""", null, null, null)]
    [InlineData("""{"choices":[{"delta":{"content":42}}]}""", null, null, null)]
    public void A_stream_line_is_read_leniently(string data, string? content, string? reasoning, string? finish)
    {
        var chunk = NvidiaChatClient.ParseChunk(data);

        Assert.Equal(content, chunk.Content);
        Assert.Equal(reasoning, chunk.Reasoning);
        Assert.Equal(finish, chunk.FinishReason);
    }

    [Fact]
    public void The_summary_is_the_last_question_on_one_line_and_cut()
    {
        var messages = new[] { new AiMessage("user", "old"), new AiMessage("assistant", "a"), new AiMessage("user", "new   question\nwith\tlines " + new string('x', 300)) };

        string summary = AiGateway.Summarise(messages);

        Assert.StartsWith("new question with lines x", summary);
        Assert.Equal(160, summary.Length);
        Assert.EndsWith("…", summary);
    }
}
