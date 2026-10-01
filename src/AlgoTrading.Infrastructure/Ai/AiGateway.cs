using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>Where a call's progress goes as it happens: the console's event stream, or nowhere.</summary>
public interface IAiStreamSink
{
    ValueTask StartAsync(long callId, IReadOnlyList<string> chain);

    /// <summary>A model is being asked: the <paramref name="number"/>th of <paramref name="of"/> left in the chain, in round <paramref name="round"/>.</summary>
    ValueTask AttemptAsync(string model, int number, int of, int round);

    ValueTask ReasoningAsync(string text);

    ValueTask DeltaAsync(string text);

    /// <summary>A model failed and the next is being asked: what it streamed since its attempt began is void.</summary>
    ValueTask FallbackAsync(string model, string reason, string? next);

    /// <summary>A tool the model asked for has run: what streamed in this round was the model's working, not the answer.</summary>
    ValueTask ToolAsync(AiToolStep step);
}

/// <summary>A sink for a caller that only wants the end result.</summary>
public sealed class NullAiStreamSink : IAiStreamSink
{
    public static readonly NullAiStreamSink Instance = new();

    public ValueTask StartAsync(long callId, IReadOnlyList<string> chain) => ValueTask.CompletedTask;

    public ValueTask AttemptAsync(string model, int number, int of, int round) => ValueTask.CompletedTask;

    public ValueTask ReasoningAsync(string text) => ValueTask.CompletedTask;

    public ValueTask DeltaAsync(string text) => ValueTask.CompletedTask;

    public ValueTask FallbackAsync(string model, string reason, string? next) => ValueTask.CompletedTask;

    public ValueTask ToolAsync(AiToolStep step) => ValueTask.CompletedTask;
}

/// <summary>A question for the gateway, already checked for shape by the caller.</summary>
/// <param name="Tier">Walk this tier's chain rather than the agent's own; null for the agent's.</param>
/// <param name="SystemPrompt">Null for the agent's own system prompt.</param>
/// <param name="Chain">Walk exactly these models, with no tools (a model's health test); null otherwise.</param>
/// <param name="TrialMemoryIds">Memories read on this call alone, whatever their status: a lesson being tested before it is used.</param>
public sealed record AiAskInput(
    string AgentKey,
    string? Tier,
    IReadOnlyList<AiMessage> Messages,
    string? SystemPrompt,
    int MaxTokens,
    double Temperature,
    string ConversationId,
    string Source,
    string RequestedBy,
    long? UserId,
    IReadOnlyList<string>? Chain = null,
    IReadOnlyList<long>? TrialMemoryIds = null);

/// <summary>One model tried, as the attempts table shows it.</summary>
public sealed record AiAttempt(string Model, string Outcome, double Seconds, int? HttpStatus, int Round = 1);

/// <summary>
/// One tool the model asked for, as it ran: what it was asked, what it found,
/// and the exact text the model was given back.
/// </summary>
public sealed record AiToolStep(
    int Round,
    string Id,
    string Name,
    string Arguments,
    bool Ok,
    string? Error,
    double Seconds,
    int Rows,
    DateTime? AsOfUtc,
    string Summary,
    int ResultChars,
    string Result);

/// <summary>How a call ended.</summary>
/// <param name="RefusalStatus">For a refused call, the HTTP status that fits (409 switched off, 429 limit, 503 no key); else null.</param>
public sealed record AiAskResult(
    long CallId,
    string Outcome,
    string Model,
    string Text,
    string Reasoning,
    double Seconds,
    string FinishReason,
    AiUsage? Usage,
    IReadOnlyList<AiAttempt> Attempts,
    string Error,
    int? RefusalStatus = null,
    int RetryAfterSeconds = 0)
{
    public IReadOnlyList<AiToolStep> Tools { get; init; } = [];

    public int Rounds { get; init; }

    /// <summary>The memories the answer was given (<see cref="AiMemoryBook"/>).</summary>
    public IReadOnlyList<long> MemoryIds { get; init; } = [];

    public int Fallbacks => AiGateway.CountFallbacks(Attempts.Select(a => a.Outcome));
}

/// <summary>
/// Every model call the desk makes goes through here: the agent's switch,
/// the rate limit, the chain with its fallbacks, the desk tools, and the
/// audit row.
/// </summary>
/// <remarks>
/// <para>
/// The row is written before the first model is asked, so a slow call shows
/// as running on the Calls tab, and completed in place however the call ends:
/// answered, every model failed, or the asker went away. A refusal (agent
/// off, rate limit, no key) is a row too, with no attempts, so "why did
/// nothing happen" has an answer on the page.
/// </para>
/// <para>
/// A model that fails in any way hands over to the next, as <c>core/llm.py</c>
/// does: a timeout, a 429 or 5xx, a 4xx (a model withdrawn from the free
/// tier answers 404), a broken stream, an empty answer. What it had streamed
/// is dropped (the sink is told), so an answer is never half one model's and
/// half another's. A model refused at once for capacity is asked once more
/// after a short pause before the chain moves on. A model whose failure cost
/// a long wait (a timeout, a queue) is not asked again within the same
/// question; one that failed quickly is, in a later round. Across questions,
/// a model that keeps failing or queueing cools and is asked after the
/// chain's healthy models (<see cref="AiModelHealth"/>).
/// </para>
/// <para>
/// An agent with tools answers in rounds. In each round the model either
/// answers or asks for tools; the gateway runs them (read-only, server side,
/// see <see cref="IAiTool"/>) and asks again with their results. Past
/// <see cref="AiSettings.MaxToolRounds"/> rounds or
/// <see cref="AiSettings.MaxToolCalls"/> calls the tools close and the model
/// must answer with what it has. Every tool call is kept on the row with the
/// exact text the model was given, so an answer can be checked against what
/// it read.
/// </para>
/// <para>
/// The key is scrubbed from every stored text as a second layer: the client
/// never puts it anywhere but the Authorization header.
/// </para>
/// <para>
/// An agent with memory gets its active memories after its own prompt
/// (<see cref="AiMemoryBook"/>), and the row keeps which ones, so the owner's
/// verdict on the answer and the daily check's grade can be counted on them.
/// A caller that brings its own system prompt, and a model's health test, get
/// none.
/// </para>
/// </remarks>
public sealed class AiGateway
{
    private const int MaxStoredAnswer = 200_000;
    private const int MaxStoredReasoning = 100_000;
    private const int MaxStoredToolResult = 20_000;
    private const int SummaryLength = 160;

    /// <summary>How a tool's answer is written for the model: compact, rupee signs as they are.</summary>
    private static readonly JsonSerializerOptions ToolJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TradingDbContext _db;
    private readonly AiSettingsStore _store;
    private readonly NvidiaChatClient _client;
    private readonly AiRateLimiter _limiter;
    private readonly AiToolbox _toolbox;
    private readonly AiModelHealth _health;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILogger<AiGateway> _logger;
    private readonly TimeProvider _time;
    private readonly AiMemoryBook _memory;

    public AiGateway(
        TradingDbContext db,
        AiSettingsStore store,
        NvidiaChatClient client,
        AiRateLimiter limiter,
        AiToolbox toolbox,
        AiModelHealth health,
        IOptionsMonitor<AiSettings> settings,
        ILogger<AiGateway> logger,
        TimeProvider? time = null,
        AiMemoryBook? memory = null)
    {
        _db = db;
        _store = store;
        _client = client;
        _limiter = limiter;
        _toolbox = toolbox;
        _health = health;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _memory = memory ?? new AiMemoryBook(db, client, settings, logger, _time);
    }

    /// <summary>Failed attempts that handed over to another: every failure but a last one.</summary>
    public static int CountFallbacks(IEnumerable<string> outcomes)
    {
        var list = outcomes.ToList();
        int fallbacks = 0;
        for (int i = 0; i < list.Count - 1; i++)
        {
            if (list[i] is not ("ok" or "cancelled")) fallbacks++;
        }

        return fallbacks;
    }

    public async Task<AiAskResult> AskAsync(AiAskInput input, IAiStreamSink sink, CancellationToken cancellationToken)
    {
        var agent = AiCatalog.Agent(input.AgentKey)
            ?? throw new ArgumentException($"Unknown agent {input.AgentKey}.", nameof(input));

        var state = await _store.LoadAsync(cancellationToken);
        IReadOnlyList<string> chain;
        if (input.Chain is { Count: > 0 } explicitChain)
        {
            chain = explicitChain;
        }
        else
        {
            var agentState = state.Agent(agent.Key);
            if (agentState is null || !agent.Built)
            {
                return await RefuseAsync(input, [], $"{agent.Name} is not built yet (planned for phase {agent.Phase}).", 409, 0);
            }

            if (!agentState.Enabled)
            {
                string why = agentState.Reason is { Length: > 0 } r ? $" ({r})" : string.Empty;
                return await RefuseAsync(input, agentState.Chain, $"{agent.Name} is switched off on the AI page{why}.", 409, 0);
            }

            chain = input.Tier is { Length: > 0 } tier ? state.Tier(tier).Chain : agentState.Chain;
        }

        if (!_settings.CurrentValue.KeyConfigured)
        {
            return await RefuseAsync(input, chain,
                "No NVIDIA_API_KEY on the server: add it to the server's .env; the desk writes it into the API's settings on the next deploy.",
                503, 0);
        }

        var (lease, refusal) = _limiter.TryAcquire(input.RequestedBy);
        if (lease is null)
        {
            return await RefuseAsync(input, chain, refusal!.Error, 429, refusal.RetryAfterSeconds);
        }

        using (lease)
        {
            return await RunAsync(input, agent, chain, sink, cancellationToken);
        }
    }

    private async Task<AiAskResult> RunAsync(
        AiAskInput input,
        AiAgentDef agent,
        IReadOnlyList<string> chain,
        IAiStreamSink sink,
        CancellationToken cancellationToken)
    {
        var s = _settings.CurrentValue;

        // A health test asks one model one thing, with nothing to read.
        var tools = input.Chain is null ? _toolbox.For(agent) : [];
        var specs = tools.Select(t => new AiToolSpec(t.Name, t.Description, t.Parameters)).ToList();

        string systemPrompt = input.SystemPrompt ?? agent.SystemPrompt;
        var recalled = input.SystemPrompt is null && input.Chain is null
            ? await RecallAsync(agent.Key, input.Messages, input.TrialMemoryIds, cancellationToken)
            : [];
        if (recalled.Count > 0) systemPrompt += "\n\n" + AiMemoryBook.Block(recalled);

        if (input.SystemPrompt is null && specs.Count > 0)
        {
            // The model has no clock, and "today" is the question more often than not.
            var ist = IstTime.ToIst(_time.GetUtcNow().UtcDateTime);
            systemPrompt += $"\nNow: {ist:dddd d MMMM yyyy, HH:mm} IST.";
        }

        var row = NewRow(input, chain, systemPrompt);
        row.Outcome = AiCallOutcome.Running;
        row.MemoryIdsJson = JsonSerializer.Serialize(recalled.Select(r => r.Id));
        _db.AiCalls.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        var conversation = new List<AiMessage>(input.Messages);
        var attempts = new List<AiAttempt>();
        var steps = new List<AiToolStep>();
        var seen = new Dictionary<string, AiToolStep>(StringComparer.Ordinal);
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew();
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        var working = new StringBuilder();
        AiUsage? usage = null;
        int round = 0;
        string inFlight = string.Empty;
        bool finished = false;

        try
        {
            await sink.StartAsync(row.Id, chain);

            while (true)
            {
                round++;
                bool open = specs.Count > 0 && round <= s.MaxToolRounds && steps.Count < s.MaxToolCalls;
                var request = new AiChatRequest(conversation, systemPrompt, input.MaxTokens, input.Temperature,
                    specs.Count > 0 ? specs : null, ToolsClosed: specs.Count > 0 && !open);

                // A model queueing on the provider waits at the back of the chain for a while (AiModelHealth).
                var remaining = chain.Where(m => !failed.Contains(m));
                var live = input.Chain is null ? _health.Order(remaining, _time.GetUtcNow().UtcDateTime) : remaining.ToList();
                AiAttemptEnd? end = null;
                string model = string.Empty;

                for (int i = 0; i < live.Count && end is null; i++)
                {
                    string candidate = live[i];
                    bool askedAgain = false;
                    while (true)
                    {
                        answer.Clear();
                        reasoning.Clear();
                        inFlight = candidate;
                        await sink.AttemptAsync(candidate, i + 1, live.Count, round);
                        var clock = Stopwatch.StartNew();

                        try
                        {
                            var got = await _client.StreamAsync(candidate, request, async piece =>
                            {
                                switch (piece)
                                {
                                    case AiStreamPiece.Reasoning r:
                                        reasoning.Append(r.Text);
                                        await sink.ReasoningAsync(r.Text);
                                        break;
                                    case AiStreamPiece.Content c:
                                        answer.Append(c.Text);
                                        await sink.DeltaAsync(c.Text);
                                        break;
                                }
                            }, cancellationToken);

                            // Told the tools are closed, it asked for one and said nothing else.
                            if (!open && got.ToolCalls.Count > 0 && string.IsNullOrWhiteSpace(answer.ToString()))
                            {
                                throw new AiAttemptFailedException("asked for a tool after the tools were closed");
                            }

                            attempts.Add(new AiAttempt(candidate, "ok", Round(clock.Elapsed.TotalSeconds), 200, round));
                            _health.Record(candidate, true, clock.Elapsed.TotalSeconds, false, "ok", _time.GetUtcNow().UtcDateTime);
                            end = got;
                            model = candidate;
                            break;
                        }
                        catch (AiAttemptFailedException ex)
                        {
                            string outcome = Redact(ex.Outcome);
                            double took = clock.Elapsed.TotalSeconds;
                            attempts.Add(new AiAttempt(candidate, outcome, Round(took), ex.HttpStatus, round));
                            _health.Record(candidate, false, took, took >= s.SlowFailureSeconds, outcome, _time.GetUtcNow().UtcDateTime);
                            // Information, not a warning: a model handing over is the chain working, and the
                            // attempt is on the call's row. As a warning, Sentinel's log agent opened an
                            // incident per fallback (30 Sep, #197-#201).
                            _logger.LogInformation("AI call {CallId} ({Agent}) round {Round}: {Model} failed: {Outcome}", row.Id, agent.Key, round, candidate, outcome);

                            // Refused at once for capacity: a moment later it often has room, and the next
                            // model in the chain may be a 90-second queue.
                            if (!askedAgain && s.CapacityRetrySeconds > 0 && took < s.SlowFailureSeconds && IsCapacityRefusal(ex))
                            {
                                askedAgain = true;
                                await sink.FallbackAsync(candidate, outcome, candidate);
                                await Task.Delay(TimeSpan.FromSeconds(s.CapacityRetrySeconds), _time, cancellationToken);
                                continue;
                            }

                            // Only a failure that cost a long wait keeps the model out of later rounds.
                            if (took >= s.SlowFailureSeconds) failed.Add(candidate);
                            string? next = i + 1 < live.Count ? live[i + 1] : null;
                            if (next is not null) await sink.FallbackAsync(candidate, outcome, next);
                            break;
                        }
                    }
                }

                if (end is null)
                {
                    string error = "Every model failed: " + string.Join("; ", attempts.Where(a => a.Outcome != "ok").Select(a => $"{a.Model} {a.Outcome}"));
                    answer.Clear();
                    Complete(row, AiCallOutcome.Failed, string.Empty, error, answer, Working(working, reasoning), string.Empty, usage, attempts, steps, round, total);
                    await SaveFinalAsync(row);
                    finished = true;
                    return Result(row, attempts, usage, steps, round);
                }

                usage = Add(usage, end.Usage);

                if (open && end.ToolCalls.Count > 0)
                {
                    // This round was working, not the answer: keep it as reasoning.
                    Commit(working, round, reasoning, answer);
                    conversation.Add(new AiMessage("assistant", answer.ToString().Trim(), end.ToolCalls));

                    foreach (var call in end.ToolCalls)
                    {
                        var step = await RunToolAsync(call, round, steps.Count, seen, cancellationToken);
                        steps.Add(step);
                        conversation.Add(new AiMessage("tool", step.Result, null, call.Id));
                        await sink.ToolAsync(step);
                    }

                    continue;
                }

                Complete(row, AiCallOutcome.Ok, model, string.Empty, answer, Working(working, reasoning), end.FinishReason, usage, attempts, steps, round, total);
                await SaveFinalAsync(row);
                finished = true;
                return Result(row, attempts, usage, steps, round);
            }
        }
        catch (Exception ex) when ((ex is OperationCanceledException && cancellationToken.IsCancellationRequested) || ex is AiSinkFailedException)
        {
            // The asker stopped it, or their connection went mid-answer: either
            // way nobody is reading, and no other model should be asked.
            // The model being asked when it stopped, unless its attempt is already recorded
            // (it failed, or it answered and a tool was running).
            if (inFlight.Length > 0 && (attempts.Count == 0 || attempts[^1].Model != inFlight || attempts[^1].Round < round))
            {
                double spent = total.Elapsed.TotalSeconds - attempts.Sum(a => a.Seconds);
                attempts.Add(new AiAttempt(inFlight, "cancelled", Round(Math.Max(0, spent)), null, round));
            }

            string why = ex is AiSinkFailedException ? "The console's connection closed mid-answer." : "Stopped by the asker.";
            Complete(row, AiCallOutcome.Cancelled, string.Empty, why, answer, Working(working, reasoning), string.Empty, usage, attempts, steps, round, total);
            await SaveFinalAsync(row);
            finished = true;
            return Result(row, attempts, usage, steps, round);
        }
        finally
        {
            if (!finished)
            {
                // Something outside the model failed (the console's connection,
                // the database, a tool that threw past its guard): never leave
                // the row saying "running".
                Complete(row, AiCallOutcome.Failed, string.Empty, "The call stopped unexpectedly on the server; see api.log.",
                    answer, Working(working, reasoning), string.Empty, usage, attempts, steps, round, total);
                await SaveFinalAsync(row);
            }
        }
    }

    /// <summary>
    /// The call was turned away for want of room, not answered badly: the desk's own rate limit refused it (429), or
    /// every model that failed in it refused for capacity. A caller that counts tries per subject (the Trade
    /// Reviewer) does not count such a call as one: in a busy hour it would otherwise give up on its work.
    /// </summary>
    public static bool IsCapacityRefusal(AiAskResult result)
    {
        if (result.Outcome == AiCallOutcome.Refused) return result.RefusalStatus == 429;
        if (result.Outcome != AiCallOutcome.Failed) return false;

        var failed = result.Attempts.Where(a => a.Outcome != "ok").ToList();
        return failed.Count > 0 && failed.All(a => IsCapacityRefusal(a.HttpStatus, a.Outcome));
    }

    /// <summary>The provider said it has no room right now, rather than that the request was wrong.</summary>
    public static bool IsCapacityRefusal(AiAttemptFailedException ex) => IsCapacityRefusal(ex.HttpStatus, ex.Outcome);

    private static bool IsCapacityRefusal(int? httpStatus, string outcome) =>
        httpStatus is 429 or 502 or 503
        || outcome.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
        || outcome.Contains("temporarily", StringComparison.OrdinalIgnoreCase)
        || outcome.Contains("capacity", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs one tool call and writes what the model is given back. A tool that
    /// is unknown, badly called, slow or failing answers the model in words,
    /// so it can correct itself; only the asker's cancellation escapes.
    /// </summary>
    private async Task<AiToolStep> RunToolAsync(
        AiToolCall call,
        int round,
        int callsSoFar,
        Dictionary<string, AiToolStep> seen,
        CancellationToken cancellationToken)
    {
        var s = _settings.CurrentValue;
        var clock = Stopwatch.StartNew();
        string args = string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments.Trim();
        string fetchedAt = $"{IstTime.ToIst(_time.GetUtcNow().UtcDateTime):yyyy-MM-dd HH:mm:ss} IST";

        AiToolStep Refused(string error) => new(round, call.Id, call.Name, args, false, error, Round(clock.Elapsed.TotalSeconds), 0, null, error,
            0, JsonSerializer.Serialize(new { tool = call.Name, error }, ToolJson));

        if (callsSoFar >= s.MaxToolCalls)
        {
            return Refused($"The limit of {s.MaxToolCalls} tool calls for one question is reached: answer with what you have.");
        }

        // The same question twice in one answer gets the same answer, without reading again.
        string key = $"{call.Name} {args}";
        if (seen.TryGetValue(key, out var earlier))
        {
            return earlier with { Round = round, Id = call.Id, Seconds = 0, Summary = "Same call as before: " + earlier.Summary };
        }

        var tool = _toolbox.Find(call.Name);
        if (tool is null)
        {
            return Refused($"No tool named {call.Name}. The tools are: {string.Join(", ", _toolbox.All.Select(t => t.Name))}.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(s.ToolTimeoutSeconds));
        AiToolStep step;
        try
        {
            var output = await tool.RunAsync(AiToolArgs.Parse(args), timeout.Token);
            string? asOf = output.AsOfUtc is DateTime at ? $"{IstTime.ToIst(DateTime.SpecifyKind(at, DateTimeKind.Utc)):yyyy-MM-dd HH:mm:ss} IST" : null;
            string json = JsonSerializer.Serialize(new { tool = call.Name, asOf, fetchedAt, rows = output.Rows, data = output.Data }, ToolJson);

            if (json.Length > s.MaxToolResultChars)
            {
                string error = $"The answer is {json.Length:N0} characters, over the {s.MaxToolResultChars:N0} limit: ask for less (one run, a shorter range, fewer rows).";
                step = new AiToolStep(round, call.Id, call.Name, args, false, error, Round(clock.Elapsed.TotalSeconds), output.Rows, output.AsOfUtc,
                    "Too long to send", json.Length, JsonSerializer.Serialize(new { tool = call.Name, error }, ToolJson));
            }
            else
            {
                step = new AiToolStep(round, call.Id, call.Name, args, true, null, Round(clock.Elapsed.TotalSeconds), output.Rows, output.AsOfUtc,
                    output.Summary, json.Length, json);
            }
        }
        catch (AiToolArgumentException ex)
        {
            step = Refused(ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            step = Refused($"The tool took longer than {s.ToolTimeoutSeconds:0} s and was stopped.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "AI tool {Tool} failed with arguments {Arguments}", call.Name, args);
            step = Refused($"The tool failed on the server ({ex.GetType().Name}).");
        }

        seen[key] = step;
        return step;
    }

    private async Task<AiAskResult> RefuseAsync(AiAskInput input, IReadOnlyList<string> chain, string error, int status, int retryAfter)
    {
        var agent = AiCatalog.Agent(input.AgentKey);
        var row = NewRow(input, chain, input.SystemPrompt ?? agent?.SystemPrompt ?? string.Empty);
        row.Outcome = AiCallOutcome.Refused;
        row.Error = error;
        row.CompletedUtc = row.CreatedUtc;
        _db.AiCalls.Add(row);
        await _db.SaveChangesAsync(CancellationToken.None);
        return new AiAskResult(row.Id, AiCallOutcome.Refused, string.Empty, string.Empty, string.Empty, 0, string.Empty, null, [], error, status, retryAfter);
    }

    private AiCall NewRow(AiAskInput input, IReadOnlyList<string> chain, string systemPrompt) => new()
    {
        CreatedUtc = _time.GetUtcNow().UtcDateTime,
        AgentKey = input.AgentKey,
        Tier = input.Tier ?? string.Empty,
        Source = input.Source,
        RequestedBy = input.RequestedBy,
        UserId = input.UserId,
        ConversationId = input.ConversationId,
        ChainJson = JsonSerializer.Serialize(chain),
        SystemPrompt = systemPrompt,
        MessagesJson = JsonSerializer.Serialize(input.Messages.Select(m => new { role = m.Role, content = m.Content })),
        Summary = Summarise(input.Messages),
        MaxTokens = input.MaxTokens,
        Temperature = input.Temperature,
    };

    private void Complete(
        AiCall row,
        string outcome,
        string model,
        string error,
        StringBuilder answer,
        string reasoning,
        string finishReason,
        AiUsage? usage,
        List<AiAttempt> attempts,
        List<AiToolStep> steps,
        int rounds,
        Stopwatch total)
    {
        row.Outcome = outcome;
        row.Model = model;
        row.Error = Redact(error);
        row.Answer = Cut(answer.ToString(), MaxStoredAnswer);
        row.Reasoning = Cut(reasoning, MaxStoredReasoning);
        row.FinishReason = finishReason;
        row.PromptTokens = usage?.PromptTokens;
        row.CompletionTokens = usage?.CompletionTokens;
        row.TotalTokens = usage?.TotalTokens;
        row.AttemptsJson = JsonSerializer.Serialize(attempts.Select(a => new
        {
            model = a.Model, outcome = a.Outcome, seconds = a.Seconds, httpStatus = a.HttpStatus, round = a.Round,
        }));
        row.ToolsJson = JsonSerializer.Serialize(steps.Select(t => new
        {
            round = t.Round, id = t.Id, name = t.Name, arguments = t.Arguments, ok = t.Ok, error = t.Error, seconds = t.Seconds,
            rows = t.Rows, asOfUtc = t.AsOfUtc, summary = t.Summary, resultChars = t.ResultChars,
            result = Cut(t.Result, MaxStoredToolResult),
        }), ToolJson);
        row.Rounds = rounds;
        row.ToolCalls = steps.Count;
        row.Seconds = Round(total.Elapsed.TotalSeconds);
        row.CompletedUtc = _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>Saved whatever the asker did: a call they stopped is still recorded.</summary>
    private async Task SaveFinalAsync(AiCall row)
    {
        try
        {
            await _db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            _logger.LogError(ex, "AI call {CallId}: could not record how it ended", row.Id);
        }
    }

    private static AiAskResult Result(AiCall row, List<AiAttempt> attempts, AiUsage? usage, List<AiToolStep> steps, int rounds) =>
        new(row.Id, row.Outcome, row.Model, row.Answer, row.Reasoning, row.Seconds, row.FinishReason, usage, attempts, row.Error)
        {
            Tools = steps,
            Rounds = rounds,
            MemoryIds = MemoryIds(row.MemoryIdsJson),
        };

    /// <summary>The memory ids a call row carries; none when the column cannot be read.</summary>
    public static IReadOnlyList<long> MemoryIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The agent's memories for this question. Memory is help, not a
    /// dependency: a failure to read it is logged and the call goes on without.
    /// </summary>
    private async Task<IReadOnlyList<AiRecalled>> RecallAsync(
        string agentKey, IReadOnlyList<AiMessage> messages, IReadOnlyList<long>? trial, CancellationToken cancellationToken)
    {
        try
        {
            string question = messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
            return await _memory.RecallAsync(agentKey, question, cancellationToken, trial);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "AI memory for {Agent} could not be read; answering without it", agentKey);
            return [];
        }
    }

    /// <summary>A tool round's reasoning, and anything it said before asking, kept as the model's working.</summary>
    private static void Commit(StringBuilder working, int round, StringBuilder reasoning, StringBuilder said)
    {
        string text = reasoning.ToString().Trim();
        string note = said.ToString().Trim();
        if (text.Length == 0 && note.Length == 0) return;
        if (working.Length > 0) working.Append("\n\n");
        working.Append($"[round {round}] ").Append(text);
        if (note.Length > 0) working.Append(text.Length > 0 ? "\n" : string.Empty).Append(note);
    }

    private static string Working(StringBuilder working, StringBuilder lastRound)
    {
        string last = lastRound.ToString();
        if (working.Length == 0) return last;
        return last.Trim().Length == 0 ? working.ToString() : $"{working}\n\n[answer] {last}";
    }

    private static AiUsage? Add(AiUsage? a, AiUsage? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new AiUsage(Sum(a.PromptTokens, b.PromptTokens), Sum(a.CompletionTokens, b.CompletionTokens), Sum(a.TotalTokens, b.TotalTokens));

        static int? Sum(int? x, int? y) => x is null && y is null ? null : (x ?? 0) + (y ?? 0);
    }

    /// <summary>The start of the last question, on one line, for the list view.</summary>
    public static string Summarise(IReadOnlyList<AiMessage> messages)
    {
        string last = messages.LastOrDefault(m => m.Role == "user")?.Content ?? string.Empty;
        string flat = string.Join(' ', last.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= SummaryLength ? flat : flat[..(SummaryLength - 1)] + "…";
    }

    private string Redact(string text)
    {
        string key = _settings.CurrentValue.ApiKey;
        return key.Length >= 8 && text.Contains(key, StringComparison.Ordinal) ? text.Replace(key, "[key]", StringComparison.Ordinal) : text;
    }

    private static string Cut(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n[cut: longer than the audit log keeps]";

    private static double Round(double seconds) => Math.Round(seconds, 2);
}
