using AlgoTrading.Domain.Enums;

namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A day an exchange is closed, or closed for one of its sessions, as published
/// in that exchange's holiday circular.
///
/// The platform used to know only weekends. On 2026-09-14 (Ganesh Chaturthi)
/// the morning job restarted the API, waited for a FYERS sign-in until 14:30,
/// and every feed and runner took a closed market for a silent one.
/// </summary>
public class MarketHoliday
{
    public long Id { get; set; }

    /// <summary>"NSE", "BSE" or "MCX". Equity and equity derivatives share one list per exchange.</summary>
    public string Exchange { get; set; } = string.Empty;

    /// <summary>The calendar date in IST.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The occasion as the circular names it, e.g. "Ganesh Chaturthi".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which part of the day is closed.</summary>
    public MarketClosure Closure { get; set; } = MarketClosure.FullDay;

    /// <summary>The circular this row came from, so every date can be checked against its source.</summary>
    public string? Source { get; set; }

    /// <summary>Who last changed the row: "seed" for the shipped calendar, otherwise the admin's user name.</summary>
    public string? UpdatedBy { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
