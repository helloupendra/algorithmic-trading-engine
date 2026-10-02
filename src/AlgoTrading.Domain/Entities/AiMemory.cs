namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One thing an agent reads before it answers: a note the owner wrote, a
/// correction to one of its answers, or a lesson it proposed that passed its
/// test.
/// </summary>
/// <remarks>
/// <para>
/// The models do not learn; their weights are the provider's. What an agent
/// "remembers" is these rows, put into its system prompt on every call
/// (<c>AiMemoryBook</c>), so what it has learnt is always readable, editable
/// and traceable to where it came from.
/// </para>
/// <para>
/// Only <see cref="AiMemoryStatus.Active"/> rows are read. The owner's own
/// notes and corrections are active from the start. A lesson waits as
/// <see cref="AiMemoryStatus.Proposed"/> until its test decides (owner, 1 Oct:
/// lessons are tested, not approved), so a wrong lesson cannot teach itself in:
/// the Desk Assistant's from a question it got wrong in the daily check, asked
/// again with it; the AI Trader's from a reflection on one of its finished days,
/// tested on its past looks with and without it (<c>AiTraderLessonCheck</c>).
/// </para>
/// <para>
/// Every answer records the memories it was given (<c>ai_calls.MemoryIdsJson</c>),
/// and the owner's 👍/👎 on that answer and the check's grade of it are
/// counted on each of them: a memory that keeps turning up in bad answers is
/// flagged for review. Nothing is retired automatically.
/// </para>
/// </remarks>
public class AiMemory
{
    public long Id { get; set; }

    /// <summary>The agent that reads it, e.g. <c>desk-assistant</c>.</summary>
    public string AgentKey { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiMemoryKind"/>.</summary>
    public string Kind { get; set; } = AiMemoryKind.Note;

    /// <summary>One of <see cref="AiMemoryStatus"/>.</summary>
    public string Status { get; set; } = AiMemoryStatus.Active;

    /// <summary>What the agent reads: one short rule or fact.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>The question it came from (a correction's or a lesson's); empty for a plain note.</summary>
    public string Context { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiMemorySource"/>.</summary>
    public string Source { get; set; } = AiMemorySource.Owner;

    /// <summary>Where it was written: <c>console</c>, <c>telegram</c> or <c>check</c>.</summary>
    public string Via { get; set; } = "console";

    /// <summary>The answer it corrects, or the failed check answer a lesson came from.</summary>
    public long? SourceCallId { get; set; }

    /// <summary>
    /// The report a lesson came from: the daily check's, or for an AI Trader lesson its reflection's, whose
    /// <c>SessionDate</c> is the day it was learned from.
    /// </summary>
    public long? SourceReportId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    /// <summary>Who last approved, rejected, retired or restored it; empty when nobody has.</summary>
    public string DecidedBy { get; set; } = string.Empty;

    public DateTime? DecidedUtc { get; set; }

    /// <summary>When it last became active.</summary>
    public DateTime? ActivatedUtc { get; set; }

    /// <summary>When it was last retired; null while it has not been since it was last active.</summary>
    public DateTime? RetiredUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>Answers it was given to.</summary>
    public int Uses { get; set; }

    public DateTime? LastUsedUtc { get; set; }

    /// <summary>👍 from the owner on answers it was part of.</summary>
    public int Ups { get; set; }

    /// <summary>👎 from the owner on answers it was part of.</summary>
    public int Downs { get; set; }

    /// <summary>Daily-check questions answered right while it was part of the answer.</summary>
    public int CheckPasses { get; set; }

    public int CheckFails { get; set; }

    /// <summary>The text's embedding, for picking the memories closest to a question once they outgrow the prompt budget.</summary>
    public float[] Embedding { get; set; } = [];

    /// <summary>The model that made <see cref="Embedding"/>; another model's vector is made again.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;
}

/// <summary>What an <see cref="AiMemory"/> is.</summary>
public static class AiMemoryKind
{
    /// <summary>Something the owner told it to remember (/remember, or Add a note).</summary>
    public const string Note = "note";

    /// <summary>What the owner said one of its answers should have been (👎 with a correction).</summary>
    public const string Correction = "correction";

    /// <summary>A lesson it proposed (from a question it got wrong, or from one of its trading days), used once its test passes.</summary>
    public const string Lesson = "lesson";

    public static readonly IReadOnlyList<string> All = [Note, Correction, Lesson];
}

/// <summary>Where an <see cref="AiMemory"/> stands.</summary>
public static class AiMemoryStatus
{
    /// <summary>Read by the agent on every call.</summary>
    public const string Active = "active";

    /// <summary>Proposed by an agent, waiting for its test (the owner may decide first).</summary>
    public const string Proposed = "proposed";

    /// <summary>A proposal its test or the owner turned down.</summary>
    public const string Rejected = "rejected";

    /// <summary>Was active; the owner took it out, or a lesson with stronger evidence took its place.</summary>
    public const string Retired = "retired";

    public static readonly IReadOnlyList<string> All = [Active, Proposed, Rejected, Retired];
}

/// <summary>Who an <see cref="AiMemory"/> came from.</summary>
public static class AiMemorySource
{
    public const string Owner = "owner";

    public const string Feedback = "feedback";

    /// <summary>
    /// A lesson written and tested by code: the Desk Assistant's daily check, or the AI Trader's reflection (told
    /// apart by the agent). A value of its own would need the column's check constraint changed, so a migration.
    /// </summary>
    public const string Check = "check";

    public static readonly IReadOnlyList<string> All = [Owner, Feedback, Check];
}
