namespace AlgoTrading.Domain.Entities;

/// <summary>
/// What an AI agent wrote about one thing on the desk: a run's review, the
/// events extracted from a headline or a filing, the explanation of an
/// incident.
/// </summary>
/// <remarks>
/// <para>
/// One report per agent per subject (<see cref="AgentKey"/>,
/// <see cref="SubjectType"/>, <see cref="SubjectId"/>), so a scheduled agent
/// that runs again finds its work done. A report that failed keeps its row
/// with <see cref="Attempts"/> counted, and is tried again a limited number of
/// times; one that succeeded is never rewritten by the schedule.
/// </para>
/// <para>
/// Advisory only: nothing reads a report to act. The model call behind it is
/// the <see cref="AiCall"/> named by <see cref="CallId"/>, where the prompt,
/// the tools it read and the full answer are kept.
/// </para>
/// </remarks>
public class AiReport
{
    public long Id { get; set; }

    /// <summary>The agent that wrote it, e.g. <c>trade-reviewer</c>.</summary>
    public string AgentKey { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiReportSubject"/>.</summary>
    public string SubjectType { get; set; } = string.Empty;

    /// <summary>The subject's id in its own table (a run id, a news item id...), as text.</summary>
    public string SubjectId { get; set; } = string.Empty;

    /// <summary>The IST day the subject belongs to: the run's session, the headline's day.</summary>
    public DateOnly? SessionDate { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    /// <summary>One of <see cref="AiReportStatus"/>.</summary>
    public string Status { get; set; } = AiReportStatus.Ok;

    /// <summary>Tries so far, failures included.</summary>
    public int Attempts { get; set; }

    /// <summary>The model call that produced the latest version, if any.</summary>
    public long? CallId { get; set; }

    /// <summary>The model that answered; empty when none did.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>One line for lists, e.g. "Run 412 lost ₹3,672: the call leg ran against a short straddle".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The report itself, as Markdown.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>The structured part the agent returned (a verdict, extracted events), as JSON.</summary>
    public string DataJson { get; set; } = "{}";

    /// <summary>Why it failed or was judged invalid; empty when it is fine.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>What a report is about.</summary>
public static class AiReportSubject
{
    public const string Run = "run";
    public const string News = "news";
    public const string Filing = "filing";
    public const string Incident = "incident";

    public static readonly IReadOnlyList<string> All = [Run, News, Filing, Incident];
}

/// <summary>How a report came out.</summary>
public static class AiReportStatus
{
    /// <summary>Written and, where there is a check, it passed.</summary>
    public const string Ok = "ok";

    /// <summary>The model answered, but the answer failed its check (not JSON, a quote not in the source).</summary>
    public const string Invalid = "invalid";

    /// <summary>No model answered; tried again later up to a limit.</summary>
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Ok, Invalid, Failed];
}
