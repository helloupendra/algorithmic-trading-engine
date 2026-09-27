namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A board meeting a listed company has told the exchange about in advance,
/// usually to approve results, a dividend or a fund raise, from NSE's event
/// calendar.
/// </summary>
/// <remarks>
/// <see cref="FirstSeenUtc"/> is when the desk first knew of the meeting, so a
/// model can tell "results due today, known since last week" from a date that
/// only appeared this morning. A meeting that is moved shows up as a new row
/// with the new date; the old row is kept, because it was once what was known.
/// </remarks>
public class CorporateCalendarEvent
{
    public long Id { get; set; }

    /// <summary>"NSE".</summary>
    public string Exchange { get; set; } = "NSE";

    public string Symbol { get; set; } = string.Empty;

    public string Company { get; set; } = string.Empty;

    /// <summary>The exchange's purpose line, e.g. "Financial Results/Dividend".</summary>
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The meeting's date, IST.</summary>
    public DateOnly EventDate { get; set; }

    /// <summary>When the recorder first stored it. Never updated.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>SHA-256 (lower-case hex) of exchange, symbol, date and purpose; the duplicate check.</summary>
    public string UniqueKey { get; set; } = string.Empty;
}
