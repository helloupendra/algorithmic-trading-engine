namespace AlgoTrading.Domain.Entities;

/// <summary>
/// What the candle-pattern scanner watches: which symbols, on which timeframes,
/// for which patterns, and whether a hit goes to Telegram.
/// </summary>
/// <remarks>
/// Lists are stored as comma-separated text, the convention the schema already
/// uses (<c>symbol_sync_states.KnownEmptyDatesCsv</c>): they are short, read
/// whole, and never queried inside.
/// </remarks>
public class CandlePatternRule
{
    public long Id { get; set; }

    /// <summary>What the operator calls it, e.g. "Indices — 5m and 15m".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Explicit canonical symbols, e.g. "NSE:NIFTY50-INDEX,NSE:SBIN-EQ".</summary>
    public string SymbolsCsv { get; set; } = string.Empty;

    /// <summary>
    /// Symbol groups resolved on every scan: "indices", "index-futures",
    /// "recording-stocks", "mcx-futures", or "future:CRUDEOIL" for one
    /// underlying's nearest future. Resolved each time so a contract that expires
    /// is replaced by the next without anyone editing the rule.
    /// </summary>
    public string GroupsCsv { get; set; } = string.Empty;

    /// <summary>Candle sizes in minutes, e.g. "5,15".</summary>
    public string TimeframesCsv { get; set; } = string.Empty;

    /// <summary>Pattern keys, e.g. "doji,hammer"; see the pattern catalog.</summary>
    public string PatternsCsv { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Send hits to Telegram as well as recording them.</summary>
    public bool Notify { get; set; } = true;

    /// <summary>"seed" for a shipped default, otherwise the admin who last saved it.</summary>
    public string? UpdatedBy { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
