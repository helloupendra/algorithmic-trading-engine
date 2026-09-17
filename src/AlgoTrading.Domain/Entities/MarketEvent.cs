namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A scheduled event that can move the market: an RBI or US Fed policy
/// decision, a US inflation print, the Union Budget, an election result.
/// </summary>
/// <remarks>
/// Shipped rows carry the official source they were copied from, so every date
/// can be checked. Weekly expiries and exchange holidays are not stored here:
/// the page derives them from the instrument master and the market calendar.
/// </remarks>
public class MarketEvent
{
    public long Id { get; set; }

    /// <summary>The IST calendar date the market meets the event on.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The IST time of the announcement when one is scheduled, e.g. 10:00 for an RBI decision.</summary>
    public TimeOnly? TimeIst { get; set; }

    /// <summary>"IN", "US" or "GLOBAL".</summary>
    public string Region { get; set; } = "IN";

    /// <summary>A short kind the page groups by: "RBI policy", "Fed policy", "US CPI", "Budget", "Election", "Results", "Other".</summary>
    public string Category { get; set; } = "Other";

    public string Title { get; set; } = string.Empty;

    /// <summary>1 = worth knowing, 2 = moves the market, 3 = can move it a lot.</summary>
    public int Importance { get; set; } = 2;

    public string? Notes { get; set; }

    /// <summary>Where the date was taken from, so it can be checked.</summary>
    public string? Source { get; set; }

    /// <summary>"seed" for shipped rows, otherwise the admin's user name.</summary>
    public string? UpdatedBy { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
