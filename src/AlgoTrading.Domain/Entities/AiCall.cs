namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One question put to a hosted language model, and what came back: the audit
/// row behind the console's AI workspace.
/// </summary>
/// <remarks>
/// <para>
/// A row is written when the call starts (<see cref="AiCallOutcome.Running"/>),
/// so the console shows a slow model as in flight, and completed in place when
/// it ends. A refusal before any model was asked (the agent switched off, a
/// rate limit, no key on the server) is recorded too, with no attempts.
/// </para>
/// <para>
/// A call walks its model chain in order; every model it tried is one entry of
/// <see cref="AttemptsJson"/>, so a fallback is visible after the fact.
/// <see cref="Model"/> is the one that answered, if any did.
/// </para>
/// <para>
/// The provider key is never stored, in any column: the request is kept as the
/// messages and parameters sent, not as the HTTP request.
/// </para>
/// </remarks>
public class AiCall
{
    public long Id { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>Which agent asked (for example <c>desk-assistant</c>, or <c>model-test</c> for a model's health test).</summary>
    public string AgentKey { get; set; } = string.Empty;

    /// <summary>The tier whose chain was walked, or empty when the agent's own chain or one model was.</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>Where the call came from: <c>console</c>, <c>api</c> or <c>schedule</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The user name of whoever asked; the agent's name for a scheduled call.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public long? UserId { get; set; }

    /// <summary>The console's conversation id, so the turns of one chat can be read together.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The chain of model ids the call would walk, in order, as a JSON array.</summary>
    public string ChainJson { get; set; } = "[]";

    /// <summary>The model that answered; empty when none did.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiCallOutcome"/>.</summary>
    public string Outcome { get; set; } = AiCallOutcome.Running;

    /// <summary>Why it failed or was refused, naming every model tried; empty on success.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>Wall-clock seconds from the start to the end of the call, fallbacks included.</summary>
    public double Seconds { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int? TotalTokens { get; set; }

    /// <summary>
    /// Every model tried, in order: a JSON array of
    /// <c>{ model, outcome, seconds, httpStatus }</c>, where outcome is <c>ok</c>
    /// or the reason it moved on (<c>timeout</c>, <c>http 429</c>, <c>empty answer</c>...).
    /// </summary>
    public string AttemptsJson { get; set; } = "[]";

    /// <summary>
    /// Every desk tool the model asked for, in order: a JSON array of
    /// <c>{ round, id, name, arguments, ok, error, seconds, rows, asOfUtc, summary, resultChars, result }</c>,
    /// where <c>result</c> is the text the model was given back (cut past 20,000 characters).
    /// </summary>
    public string ToolsJson { get; set; } = "[]";

    /// <summary>Model rounds the answer took: one without tools, one more per round of tool calls.</summary>
    public int Rounds { get; set; }

    /// <summary>How many tool calls <see cref="ToolsJson"/> holds, for the list view.</summary>
    public int ToolCalls { get; set; }

    /// <summary>The system prompt sent, if any.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>The conversation sent, as a JSON array of <c>{ role, content }</c> (the system prompt apart).</summary>
    public string MessagesJson { get; set; } = "[]";

    /// <summary>The start of the last question, whitespace folded, for the list view.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The answer; for a cancelled or broken call, what had arrived.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>The model's reasoning text, when it streams one (Nemotron does).</summary>
    public string Reasoning { get; set; } = string.Empty;

    /// <summary>The provider's finish reason (<c>stop</c>, <c>length</c>...), empty when there was none.</summary>
    public string FinishReason { get; set; } = string.Empty;

    public int MaxTokens { get; set; }

    public double Temperature { get; set; }

    /// <summary>The <see cref="AiMemory"/> ids put into the system prompt, as a JSON array; empty for an agent without memory.</summary>
    public string MemoryIdsJson { get; set; } = "[]";

    /// <summary>The owner's verdict on the answer: 1 (👍), -1 (👎), or null when none was given.</summary>
    public int? FeedbackScore { get; set; }

    /// <summary>What the owner said it should have been, with a 👎.</summary>
    public string FeedbackNote { get; set; } = string.Empty;

    public string FeedbackBy { get; set; } = string.Empty;

    public DateTime? FeedbackUtc { get; set; }
}

/// <summary>What became of an <see cref="AiCall"/>.</summary>
public static class AiCallOutcome
{
    /// <summary>Still in flight.</summary>
    public const string Running = "running";

    /// <summary>A model answered.</summary>
    public const string Ok = "ok";

    /// <summary>Every model in the chain failed.</summary>
    public const string Failed = "failed";

    /// <summary>Not sent: the agent is switched off, a rate limit, or no key on the server.</summary>
    public const string Refused = "refused";

    /// <summary>The asker stopped it before it finished.</summary>
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Running, Ok, Failed, Refused, Cancelled];
}
