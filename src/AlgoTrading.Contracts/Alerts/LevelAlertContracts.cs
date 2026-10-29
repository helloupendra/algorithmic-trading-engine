namespace AlgoTrading.Contracts.Alerts;

/// <summary>A level alert as the page lists it.</summary>
public sealed class LevelAlertRuleDto
{
    public long Id { get; set; }
    public long UserId { get; set; }
    /// <summary>The owner's user name, for an admin reading everyone's.</summary>
    public string? OwnerUserName { get; set; }
    /// <summary>"price" or "pcr".</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>The canonical symbol for a price, the underlying for a PCR.</summary>
    public string Target { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>"above", "below" or "touch".</summary>
    public string Condition { get; set; } = string.Empty;
    public decimal Level { get; set; }
    /// <summary>"NIFTY crosses above 24,800".</summary>
    public string Description { get; set; } = string.Empty;
    public bool Repeat { get; set; }
    /// <summary>The re-arm distance in <see cref="RearmUnit"/>; null on a one-shot rule.</summary>
    public decimal? RearmBy { get; set; }
    /// <summary>"percent" for a price, "points" for a PCR.</summary>
    public string RearmUnit { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    public bool Notify { get; set; }
    /// <summary>Whether a fire reaches Telegram at all: only an admin's rules go there.</summary>
    public bool TelegramApplies { get; set; }
    /// <summary>"active", "paused", "fired" (a one-shot that has gone off) or "expired".</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>False on a repeating rule between a fire and its re-arm.</summary>
    public bool Armed { get; set; }
    public DateTime? LastFiredUtc { get; set; }
    public int FireCount { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>What the service last read for the target, while it is watching it.</summary>
    public LevelAlertReadingDto? Now { get; set; }
    /// <summary>Where that reading sits against the level: "above", "below" or "at".</summary>
    public string? Side { get; set; }
}

/// <summary>What a target reads now, or why it reads nothing.</summary>
public sealed class LevelAlertReadingDto
{
    public string Kind { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public bool InSession { get; set; }
    public decimal? Value { get; set; }
    public DateTime? AsOfUtc { get; set; }
    /// <summary>"live", "stale", "no-quote", "no-chain", "no-call-oi", "closed" or "unknown-exchange".</summary>
    public string State { get; set; } = string.Empty;
    public string? Detail { get; set; }
    /// <summary>For a PCR, the expiry it is read on.</summary>
    public string? Context { get; set; }
}

public sealed class SaveLevelAlertRuleRequest
{
    public string? Kind { get; set; }
    public string? Target { get; set; }
    public string? Condition { get; set; }
    public decimal? Level { get; set; }
    public bool Repeat { get; set; }
    /// <summary>For a repeating rule: percent of the level (price) or PCR points. The default when null.</summary>
    public decimal? RearmBy { get; set; }
    public string? Note { get; set; }
    public DateOnly? ExpiresOn { get; set; }
    /// <summary>Also to Telegram's Desk System chat; honoured for an admin's rule only.</summary>
    public bool Notify { get; set; } = true;
    /// <summary>Watching (true) or paused. True on a rule that has fired arms it again.</summary>
    public bool Active { get; set; } = true;
}

/// <summary>One time a rule fired.</summary>
public sealed class LevelAlertFireDto
{
    public long Id { get; set; }
    public long RuleId { get; set; }
    public long UserId { get; set; }
    public string? OwnerUserName { get; set; }
    public DateTime FiredUtc { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Condition { get; set; } = string.Empty;
    public decimal Level { get; set; }
    public decimal Value { get; set; }
    public DateTime ValueAsOfUtc { get; set; }
    public decimal PreviousValue { get; set; }
    public DateTime PreviousAsOfUtc { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool DeliveredToTelegram { get; set; }
    public string? TelegramNote { get; set; }
    public bool Seen { get; set; }
}

/// <summary>The top bar's count: the caller's own fires not yet seen.</summary>
public sealed class LevelAlertSummaryDto
{
    public int Unseen { get; set; }
    public int FiredToday { get; set; }
    public DateTime? NewestUtc { get; set; }
    public string? NewestTitle { get; set; }
}

/// <summary>The service's health and what it is watching (for a trader, only what their own rules watch).</summary>
public sealed class LevelAlertStatusDto
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; }
    public int PcrIntervalSeconds { get; set; }
    public int PriceFreshSeconds { get; set; }
    public int PcrFreshSeconds { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public double? LastScanMilliseconds { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
    public int ActiveRules { get; set; }
    public int MaxRulesPerUser { get; set; }
    public decimal DefaultPriceRearmPercent { get; set; }
    public decimal DefaultPcrRearmPoints { get; set; }
    public List<LevelAlertReadingDto> Watching { get; set; } = [];
    public List<string> Skipped { get; set; } = [];
    public bool TelegramConfigured { get; set; }
    public bool TelegramSystemChatConfigured { get; set; }
    public int TelegramMaxMessages { get; set; }
    public int TelegramWindowMinutes { get; set; }
    public int TelegramMessagesSent { get; set; }
    public int TelegramMessagesSuppressed { get; set; }
    public int TelegramMessagesFailed { get; set; }
    public string? LastTelegramProblem { get; set; }
}
