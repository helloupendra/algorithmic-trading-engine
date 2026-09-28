namespace AlgoTrading.Contracts.Patterns;

/// <summary>
/// The indicator alerts as the API reads them: the config file, its lines,
/// where every watched symbol stands, and the scanner's health.
/// </summary>
public sealed class IndicatorAlertsResponse
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; }

    /// <summary>The config file read; null when none was found (see <see cref="Searched"/>).</summary>
    public string? File { get; set; }
    public DateTime? FileModifiedUtc { get; set; }
    public List<string> Searched { get; set; } = [];
    /// <summary>Why an existing file could not be read.</summary>
    public string? FileError { get; set; }

    public int CooldownMinutes { get; set; }
    public bool Telegram { get; set; }
    public int WarmupCandles { get; set; }
    /// <summary>Lines the parser skipped or trimmed, with the reason ("Line 12: …").</summary>
    public List<string> Warnings { get; set; } = [];
    public List<IndicatorLineDto> Lines { get; set; } = [];
    /// <summary>Every rule the file may use, with its default numbers.</summary>
    public List<IndicatorRuleDto> Catalog { get; set; } = [];

    public DateTime StartedUtc { get; set; }
    public DateTime? LastScanUtc { get; set; }
    public double? LastScanMilliseconds { get; set; }
    public DateTime? LastErrorUtc { get; set; }
    public string? LastError { get; set; }
    public List<IndicatorWatchDto> Watches { get; set; } = [];
    public List<string> Unresolved { get; set; } = [];

    public bool TelegramConfigured { get; set; }

    /// <summary>
    /// Whether Telegram:SystemChatId is set. When it is not, indicator alerts
    /// go to the trades channel (Live Algotrading), not Desk System.
    /// </summary>
    public bool TelegramSystemChatConfigured { get; set; }
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
}

public sealed class IndicatorRuleDto
{
    /// <summary>"rsi-above(14,70)".</summary>
    public string Key { get; set; } = string.Empty;
    /// <summary>"rsi-above".</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>"RSI(14) crosses above 70".</summary>
    public string Label { get; set; } = string.Empty;
    public string Definition { get; set; } = string.Empty;
    /// <summary>"rsi-above(PERIOD,LEVEL)".</summary>
    public string Syntax { get; set; } = string.Empty;
    public int SettleCandles { get; set; }
    public bool NeedsVolume { get; set; }
}

/// <summary>One watch line of the config file.</summary>
public sealed class IndicatorLineDto
{
    public int Number { get; set; }
    public string Text { get; set; } = string.Empty;
    public List<string> Symbols { get; set; } = [];
    public List<string> Groups { get; set; } = [];
    public List<int> Timeframes { get; set; } = [];
    public List<IndicatorRuleDto> Rules { get; set; } = [];
    public bool PageOnly { get; set; }
    /// <summary>What the line watches right now, groups resolved (futures to today's nearest contract).</summary>
    public List<string> ResolvedSymbols { get; set; } = [];
}

/// <summary>One rule on one watched symbol and timeframe: "ready", "warming up", "waiting" or "skipped".</summary>
public sealed class IndicatorRuleStateDto
{
    public string Rule { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class IndicatorWatchDto
{
    public string Symbol { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Timeframe { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public bool InSession { get; set; }
    public int HistoryCandles { get; set; }
    public int TodayCandles { get; set; }
    public DateTime? LastBarUtc { get; set; }
    public List<IndicatorRuleStateDto> Rules { get; set; } = [];
    /// <summary>"no live bars — not streamed by any feed", or null while bars arrive.</summary>
    public string? Problem { get; set; }
}

/// <summary>One recorded indicator alert.</summary>
public sealed class IndicatorAlertDto
{
    public long Id { get; set; }
    /// <summary>When the candle closed.</summary>
    public DateTime OccurredUtc { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Timeframe { get; set; }
    /// <summary>"rsi-above(14,70)".</summary>
    public string Rule { get; set; } = string.Empty;
    public string RuleName { get; set; } = string.Empty;
    /// <summary>"RSI(14) crossed above 70".</summary>
    public string What { get; set; } = string.Empty;
    /// <summary>"up" or "down".</summary>
    public string Direction { get; set; } = string.Empty;
    public DateTime BarStartUtc { get; set; }
    public DateTime BarEndUtc { get; set; }
    public decimal Close { get; set; }
    public int MinutesInBar { get; set; }
    public int MinutesExpected { get; set; }
    public Dictionary<string, decimal> Values { get; set; } = [];
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool DeliveredToTelegram { get; set; }
    public bool Notify { get; set; }
    public string? NotifySkippedReason { get; set; }
    /// <summary>True when it fell inside the cooldown of an earlier alert of the same rule.</summary>
    public bool CooledDown { get; set; }
}
