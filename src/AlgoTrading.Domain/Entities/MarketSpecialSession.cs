namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A session outside the normal timetable, such as Diwali's Muhurat trading or a
/// special live session on a Saturday. On its date these hours replace the usual
/// ones, even when the date is a weekend or a holiday.
/// </summary>
public class MarketSpecialSession
{
    public long Id { get; set; }

    /// <summary>"NSE", "BSE" or "MCX".</summary>
    public string Exchange { get; set; } = string.Empty;

    /// <summary>The calendar date in IST.</summary>
    public DateOnly Date { get; set; }

    /// <summary>What the exchange calls it, e.g. "Muhurat trading".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Session start, IST.</summary>
    public TimeOnly OpenIst { get; set; }

    /// <summary>Session end, IST.</summary>
    public TimeOnly CloseIst { get; set; }

    /// <summary>The circular that announced it.</summary>
    public string? Source { get; set; }

    public string? UpdatedBy { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
