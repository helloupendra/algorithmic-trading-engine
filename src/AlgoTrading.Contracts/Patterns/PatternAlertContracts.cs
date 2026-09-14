namespace AlgoTrading.Contracts.Patterns;

/// <summary>Everything the rules editor offers: patterns with their definitions, symbol groups, timeframes.</summary>
public sealed class PatternCatalogResponse
{
    public List<PatternInfoDto> Patterns { get; set; } = [];
    public List<PatternGroupDto> Groups { get; set; } = [];
    public List<int> Timeframes { get; set; } = [];
}

public sealed class PatternInfoDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>"bullish", "bearish" or "neutral"; marubozu is "neutral" here and takes its candle's colour when it fires.</summary>
    public string Direction { get; set; } = string.Empty;
    public int Bars { get; set; }
    public string Suggests { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;
    /// <summary>The strategies/indicators.py function whose thresholds it reproduces, or null.</summary>
    public string? PortedFrom { get; set; }
}

public sealed class PatternGroupDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class PatternRuleDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> Symbols { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public List<int> Timeframes { get; set; } = [];
    public List<string> Patterns { get; set; } = [];
    public bool IsEnabled { get; set; }
    public bool Notify { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
    /// <summary>What the rule watches right now, groups resolved (futures to today's nearest contract).</summary>
    public List<string> ResolvedSymbols { get; set; } = [];
}

public sealed class SavePatternRuleRequest
{
    public string? Name { get; set; }
    public List<string>? Symbols { get; set; }
    public List<string>? Groups { get; set; }
    public List<int>? Timeframes { get; set; }
    public List<string>? Patterns { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool Notify { get; set; } = true;
}

/// <summary>A pattern hit, as the page lists it.</summary>
public sealed class PatternHitDto
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
}

/// <summary>One recorded pattern alert.</summary>
public sealed class PatternAlertDto
{
    public long Id { get; set; }
    /// <summary>When the candle closed.</summary>
    public DateTime OccurredUtc { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Timeframe { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public string PatternName { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public DateTime BarStartUtc { get; set; }
    public DateTime BarEndUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public int MinutesInBar { get; set; }
    public int MinutesExpected { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool DeliveredToTelegram { get; set; }
    /// <summary>Whether a rule wanted it on Telegram when it was recorded.</summary>
    public bool Notify { get; set; }
    public string? NotifySkippedReason { get; set; }
    public List<string> Rules { get; set; } = [];
}

/// <summary>The candle forming now on one watched symbol and timeframe.</summary>
public sealed class PatternFormingDto
{
    public string Symbol { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Timeframe { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public bool InSession { get; set; }
    public DateTime? BarStartUtc { get; set; }
    public DateTime? BarEndUtc { get; set; }
    /// <summary>Null when no 1-minute bar has arrived for this candle yet.</summary>
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal? Close { get; set; }
    public int MinutesElapsed { get; set; }
    public int MinutesExpected { get; set; }
    public int MinutesWithData { get; set; }
    /// <summary>Patterns (among those watched here) the candle would be if it closed now. Never notified.</summary>
    public List<PatternHitDto> WouldBe { get; set; } = [];
    /// <summary>Patterns the last closed candle completed.</summary>
    public List<PatternHitDto> LastClosed { get; set; } = [];
    public DateTime? LastClosedStartUtc { get; set; }
}

public sealed class PatternFormingResponse
{
    public DateTime AsOfUtc { get; set; }
    public List<PatternFormingDto> Items { get; set; } = [];
}

public sealed class PatternSymbolStatusDto
{
    public string Symbol { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public bool InSession { get; set; }
    public int BarsToday { get; set; }
    public DateTime? LastBarUtc { get; set; }
    /// <summary>"no live bars — not streamed by any feed", or null when the symbol is receiving data.</summary>
    public string? Problem { get; set; }
}

public sealed class PatternCountDto
{
    public string Key { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class PatternScannerStatusResponse
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public double? LastScanMilliseconds { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
    public int RuleCount { get; set; }
    public int WatchCount { get; set; }
    public int ClosedCandlesRead { get; set; }
    public List<PatternSymbolStatusDto> Symbols { get; set; } = [];
    public List<string> Unresolved { get; set; } = [];

    public bool TelegramConfigured { get; set; }
    public int TelegramMaxMessages { get; set; }
    public int TelegramWindowMinutes { get; set; }
    public DateTime? LastTelegramUtc { get; set; }
    public int TelegramMessagesSent { get; set; }
    public int TelegramMessagesSuppressed { get; set; }
    public int TelegramMessagesFailed { get; set; }
    public string? LastTelegramProblem { get; set; }

    /// <summary>Alerts recorded since 00:00 IST, from the database (survives restarts).</summary>
    public int AlertsToday { get; set; }
    public int DeliveredToday { get; set; }
    public List<PatternCountDto> TodayByPattern { get; set; } = [];
    public List<PatternCountDto> TodayByTimeframe { get; set; } = [];
}
