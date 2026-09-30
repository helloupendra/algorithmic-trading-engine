using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>Where a call's progress goes as it happens: the console's event stream, or nowhere.</summary>
public interface IAiStreamSink
{
    ValueTask StartAsync(long callId, IReadOnlyList<string> chain);

    ValueTask AttemptAsync(string model, int number, int of);

    ValueTask ReasoningAsync(string text);

    ValueTask DeltaAsync(string text);

    /// <summary>A model failed and the next is being asked: what was streamed so far is void.</summary>
    ValueTask FallbackAsync(string model, string reason, string? next);
}

/// <summary>A sink for a caller that only wants the end result.</summary>
public sealed class NullAiStreamSink : IAiStreamSink
{
    public static readonly NullAiStreamSink Instance = new();

    public ValueTask StartAsync(long callId, IReadOnlyList<string> chain) => ValueTask.CompletedTask;

    public ValueTask AttemptAsync(string model, int number, int of) => ValueTask.CompletedTask;

    public ValueTask ReasoningAsync(string text) => ValueTask.CompletedTask;

    public ValueTask DeltaAsync(string text) => ValueTask.CompletedTask;

    public ValueTask FallbackAsync(string model, string reason, string? next) => ValueTask.CompletedTask;
}

/// <summary>A question for the gateway, already checked for shape by the caller.</summary>
/// <param name="Tier">Walk this tier's chain rather than the agent's own; null for the agent's.</param>
/// <param name="SystemPrompt">Null for the agent's own system prompt.</param>
/// <param name="Chain">Walk exactly these models (a model's health test); null otherwise.</param>
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
    IReadOnlyList<string>? Chain = null);

/// <summary>One model tried, as the attempts table shows it.</summary>
public sealed record AiAttempt(string Model, string Outcome, double Seconds, int? HttpStatus);

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
    public int Fallbacks => Math.Max(0, Attempts.Count - 1);
}

/// <summary>
/// Every model call the desk makes goes through here: the agent's switch,
/// the rate limit, the chain with its fallbacks, and the audit row.
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
/// half another's.
/// </para>
/// <para>
/// The key is scrubbed from every stored text as a second layer: the client
/// never puts it anywhere but the Authorization header.
/// </para>
/// </remarks>
public sealed class AiGateway
{
    private const int MaxStoredAnswer = 200_000;
    private const int MaxStoredReasoning = 100_000;
    private const int SummaryLength = 160;

    private readonly TradingDbContext _db;
    private readonly AiSettingsStore _store;
    private readonly NvidiaChatClient _client;
    private readonly AiRateLimiter _limiter;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly ILogger<AiGateway> _logger;
    private readonly TimeProvider _time;

    public AiGateway(
        TradingDbContext db,
        AiSettingsStore store,
        NvidiaChatClient client,
        AiRateLimiter limiter,
        IOptionsMonitor<AiSettings> settings,
        ILogger<AiGateway> logger,
        TimeProvider? time = null)
    {
        _db = db;
        _store = store;
        _client = client;
        _limiter = limiter;
        _settings = settings;
        _logger = logger;
        _time = time ?? TimeProvider.System;
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
            return await RunChainAsync(input, agent, chain, sink, cancellationToken);
        }
    }

    private async Task<AiAskResult> RunChainAsync(
        AiAskInput input,
        AiAgentDef agent,
        IReadOnlyList<string> chain,
        IAiStreamSink sink,
        CancellationToken cancellationToken)
    {
        string systemPrompt = input.SystemPrompt ?? agent.SystemPrompt;
        var row = NewRow(input, chain, systemPrompt);
        row.Outcome = AiCallOutcome.Running;
        _db.AiCalls.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        var request = new AiChatRequest(input.Messages, systemPrompt, input.MaxTokens, input.Temperature);
        var attempts = new List<AiAttempt>();
        var total = Stopwatch.StartNew();
        var answer = new StringBuilder();
        var reasoning = new StringBuilder();
        bool finished = false;

        try
        {
            await sink.StartAsync(row.Id, chain);

            for (int i = 0; i < chain.Count; i++)
            {
                string model = chain[i];
                answer.Clear();
                reasoning.Clear();
                await sink.AttemptAsync(model, i + 1, chain.Count);
                var clock = Stopwatch.StartNew();

                try
                {
                    var end = await _client.StreamAsync(model, request, async piece =>
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

                    attempts.Add(new AiAttempt(model, "ok", Round(clock.Elapsed.TotalSeconds), 200));
                    Complete(row, AiCallOutcome.Ok, model, string.Empty, answer, reasoning, end.FinishReason, end.Usage, attempts, total);
                    await SaveFinalAsync(row);
                    finished = true;
                    return Result(row, attempts, end.Usage);
                }
                catch (AiAttemptFailedException ex)
                {
                    string outcome = Redact(ex.Outcome);
                    attempts.Add(new AiAttempt(model, outcome, Round(clock.Elapsed.TotalSeconds), ex.HttpStatus));
                    _logger.LogWarning("AI call {CallId} ({Agent}): {Model} failed: {Outcome}", row.Id, agent.Key, model, outcome);
                    string? next = i + 1 < chain.Count ? chain[i + 1] : null;
                    if (next is not null) await sink.FallbackAsync(model, outcome, next);
                }
            }

            string error = "Every model failed: " + string.Join("; ", attempts.Select(a => $"{a.Model} {a.Outcome}"));
            answer.Clear();
            reasoning.Clear();
            Complete(row, AiCallOutcome.Failed, string.Empty, error, answer, reasoning, string.Empty, null, attempts, total);
            await SaveFinalAsync(row);
            finished = true;
            return Result(row, attempts, null);
        }
        catch (Exception ex) when ((ex is OperationCanceledException && cancellationToken.IsCancellationRequested) || ex is AiSinkFailedException)
        {
            // The asker stopped it, or their connection went mid-answer: either
            // way nobody is reading, and no other model should be asked.
            if (attempts.Count < chain.Count && chain.Count > 0)
            {
                string model = chain[Math.Min(attempts.Count, chain.Count - 1)];
                attempts.Add(new AiAttempt(model, "cancelled", Round(total.Elapsed.TotalSeconds - attempts.Sum(a => a.Seconds)), null));
            }

            string why = ex is AiSinkFailedException ? "The console's connection closed mid-answer." : "Stopped by the asker.";
            Complete(row, AiCallOutcome.Cancelled, string.Empty, why, answer, reasoning, string.Empty, null, attempts, total);
            await SaveFinalAsync(row);
            finished = true;
            return Result(row, attempts, null);
        }
        finally
        {
            if (!finished)
            {
                // Something outside the model failed (the console's connection,
                // the database): never leave the row saying "running".
                Complete(row, AiCallOutcome.Failed, string.Empty, "The call stopped unexpectedly on the server; see api.log.",
                    answer, reasoning, string.Empty, null, attempts, total);
                await SaveFinalAsync(row);
            }
        }
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
        StringBuilder reasoning,
        string finishReason,
        AiUsage? usage,
        List<AiAttempt> attempts,
        Stopwatch total)
    {
        row.Outcome = outcome;
        row.Model = model;
        row.Error = Redact(error);
        row.Answer = Cut(answer, MaxStoredAnswer);
        row.Reasoning = Cut(reasoning, MaxStoredReasoning);
        row.FinishReason = finishReason;
        row.PromptTokens = usage?.PromptTokens;
        row.CompletionTokens = usage?.CompletionTokens;
        row.TotalTokens = usage?.TotalTokens;
        row.AttemptsJson = JsonSerializer.Serialize(attempts.Select(a => new { model = a.Model, outcome = a.Outcome, seconds = a.Seconds, httpStatus = a.HttpStatus }));
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

    private static AiAskResult Result(AiCall row, List<AiAttempt> attempts, AiUsage? usage) =>
        new(row.Id, row.Outcome, row.Model, row.Answer, row.Reasoning, row.Seconds, row.FinishReason, usage, attempts, row.Error);

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

    private static string Cut(StringBuilder text, int max) =>
        text.Length <= max ? text.ToString() : text.ToString(0, max) + "\n[cut: longer than the audit log keeps]";

    private static double Round(double seconds) => Math.Round(seconds, 2);
}
