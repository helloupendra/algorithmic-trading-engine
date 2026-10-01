namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One look the AI Trader took at the market: the brief it read, what the model proposed, whether the rules
/// allowed it, and what was done. A "none" is a row too: why it did nothing is as much a record as a trade.
/// </summary>
/// <remarks>
/// These rows are its evaluation set. The Trade Reviewer judges its trades against the brief it had, not
/// against hindsight, and a lesson is tested by replaying past briefs.
/// </remarks>
public class AiTraderDecision
{
    public long Id { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>The market's time the decision was made at: now, or a replay's clock.</summary>
    public DateTime ClockUtc { get; set; }

    /// <summary>The IST day of <see cref="ClockUtc"/>.</summary>
    public DateOnly Day { get; set; }

    /// <summary><c>shadow</c> (decides, places nothing), <c>live</c> (places paper orders) or <c>replay</c>.</summary>
    public string Mode { get; set; } = AiTraderModes.Shadow;

    /// <summary>The market replay it ran in, if any.</summary>
    public long? ReplaySessionId { get; set; }

    /// <summary>A hash of the brief, so identical briefs are seen as such.</summary>
    public string BriefHash { get; set; } = string.Empty;

    /// <summary>The brief exactly as the model read it.</summary>
    public string Brief { get; set; } = string.Empty;

    public long? CallId { get; set; }

    public string Model { get; set; } = string.Empty;

    /// <summary>none, buy, exit, start_strategy, stop_strategy; empty when the answer could not be read.</summary>
    public string Action { get; set; } = string.Empty;

    public string Underlying { get; set; } = string.Empty;

    /// <summary>The plan as read from the model's answer (JSON).</summary>
    public string PlanJson { get; set; } = "{}";

    /// <summary>The model's reason, in its words.</summary>
    public string Reason { get; set; } = string.Empty;

    public double? Confidence { get; set; }

    public bool Allowed { get; set; }

    /// <summary>The rule that decided: <c>ok</c>, or the one it broke (hours, daily-loss, size...).</summary>
    public string Rule { get; set; } = string.Empty;

    public string Why { get; set; } = string.Empty;

    public bool Executed { get; set; }

    /// <summary>What was done (order, position or run ids), as JSON.</summary>
    public string ResultJson { get; set; } = "{}";

    public string Error { get; set; } = string.Empty;
}

public static class AiTraderModes
{
    public const string Shadow = "shadow";
    public const string Live = "live";
    public const string Replay = "replay";

    public static readonly IReadOnlyList<string> All = [Shadow, Live, Replay];
}
