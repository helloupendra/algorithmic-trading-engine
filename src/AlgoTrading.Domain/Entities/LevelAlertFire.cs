namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One time a <see cref="LevelAlertRule"/> fired: when, the value that crossed
/// and the one before it, and the rule as it stood.
/// </summary>
/// <remarks>
/// Its own table rather than <c>alert_events</c>: a trader's alerts are theirs,
/// and that stream is read by every signed-in account. The rule's words are
/// copied in, so the history still reads when the rule has since been edited
/// or deleted.
/// </remarks>
public class LevelAlertFire
{
    public long Id { get; set; }

    /// <summary>The rule that fired. Not a foreign key: deleting a rule keeps its history.</summary>
    public long RuleId { get; set; }

    /// <summary>The rule's owner.</summary>
    public long UserId { get; set; }

    public DateTime FiredUtc { get; set; }

    public string Kind { get; set; } = LevelAlertKinds.Price;

    public string Target { get; set; } = string.Empty;

    public string Condition { get; set; } = LevelAlertConditions.Above;

    public decimal Level { get; set; }

    /// <summary>The price or PCR that fired it.</summary>
    public decimal Value { get; set; }

    /// <summary>When <see cref="Value"/> was true: the quote's exchange stamp, or the chain's capture.</summary>
    public DateTime ValueAsOfUtc { get; set; }

    /// <summary>The reading before it, on the other side of the level (or on it, for a touch).</summary>
    public decimal PreviousValue { get; set; }

    public DateTime PreviousAsOfUtc { get; set; }

    /// <summary>A one-line description, e.g. "NIFTY crossed above 24,800".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The full line as the console shows it and Telegram sends it, the owner's note included.</summary>
    public string Message { get; set; } = string.Empty;

    public bool DeliveredToTelegram { get; set; }

    /// <summary>Why it was not sent to Telegram, when it was not: a trader's rule, the rule's own setting, the rate limit.</summary>
    public string? TelegramNote { get; set; }

    /// <summary>When its owner first saw it in the console; null while it is new.</summary>
    public DateTime? SeenUtc { get; set; }
}
