namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One question in the Desk Assistant's exam bank: about a finished trading
/// day, with the answer the code read from the desk when it was written.
/// </summary>
/// <remarks>
/// Frozen once written, so scores compare across exams. A question whose
/// answer later reads differently is set aside in that exam, not rewritten.
/// </remarks>
public class AiExamQuestion
{
    public long Id { get; set; }

    /// <summary>What kind of question, e.g. <c>day.net</c> or <c>run.charges</c>: the score is also given per template.</summary>
    public string Template { get; set; } = string.Empty;

    /// <summary>The IST trading day it is about; null for the fixed arithmetic questions.</summary>
    public DateOnly? Day { get; set; }

    /// <summary>What on that day: a run id or an account name; empty for whole-day questions.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiExamSet"/>.</summary>
    public string Set { get; set; } = AiExamSet.Practice;

    public string Text { get; set; } = string.Empty;

    /// <summary><c>number</c>, <c>id</c> or <c>word</c>, graded as the daily check grades them.</summary>
    public string Kind { get; set; } = "number";

    public string Expected { get; set; } = string.Empty;

    public double? Number { get; set; }

    public double Tolerance { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>When it stopped being asked, if it did.</summary>
    public DateTime? RetiredUtc { get; set; }
}

/// <summary>One sitting of the exam: which questions, how many asks each, how far it got.</summary>
public class AiExam
{
    public long Id { get; set; }

    public DateTime StartedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    /// <summary><c>schedule</c> or the user who started it.</summary>
    public string Trigger { get; set; } = "schedule";

    /// <summary>Asks per question; pass^k is measured on this k.</summary>
    public int Repeats { get; set; } = 3;

    /// <summary>The questions in this sitting, in the order they are asked, as a JSON array of ids.</summary>
    public string QuestionIdsJson { get; set; } = "[]";

    /// <summary>The report written when it finished.</summary>
    public long? ReportId { get; set; }
}

/// <summary>One ask of one question in one exam.</summary>
public class AiExamAnswer
{
    public long Id { get; set; }

    public long ExamId { get; set; }

    public long QuestionId { get; set; }

    /// <summary>1 to <see cref="AiExam.Repeats"/>.</summary>
    public int Attempt { get; set; }

    /// <summary>One of <see cref="AiExamOutcome"/>.</summary>
    public string Outcome { get; set; } = AiExamOutcome.Wrong;

    public long? CallId { get; set; }

    public string Model { get; set; } = string.Empty;

    public double Seconds { get; set; }

    /// <summary>The answer, on one line, cut short.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>The desk's answer when the question was asked, when it no longer matches the frozen one.</summary>
    public string Truth { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
}

public static class AiExamSet
{
    /// <summary>May be learnt from.</summary>
    public const string Practice = "practice";

    /// <summary>Never learnt from: lessons, memories and examples stay away from these days.</summary>
    public const string Holdout = "holdout";

    public static readonly IReadOnlyList<string> All = [Practice, Holdout];
}

public static class AiExamOutcome
{
    public const string Right = "right";

    public const string Wrong = "wrong";

    /// <summary>The provider gave no answer: not counted for or against the Assistant.</summary>
    public const string NoAnswer = "no-answer";

    /// <summary>The desk's answer changed since the question was written: set aside, not counted.</summary>
    public const string Moved = "moved";

    public static readonly IReadOnlyList<string> All = [Right, Wrong, NoAnswer, Moved];
}
