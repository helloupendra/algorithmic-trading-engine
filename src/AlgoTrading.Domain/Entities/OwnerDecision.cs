namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One decision on record for the owner's Today page: what was decided, by
/// whom, and whether it was the owner's, a default taken for them, or still
/// open.
/// </summary>
/// <remarks>
/// A row each, not one JSON value in <c>system_settings</c>: that value is
/// <c>varchar(2000)</c>, and on 1 Oct the sixth decision did not fit.
/// </remarks>
public class OwnerDecision
{
    /// <summary>The longest title the API accepts.</summary>
    public const int TitleMax = 200;

    /// <summary>The longest decision text the API accepts.</summary>
    public const int DecidedMax = 1000;

    public long Id { get; set; }

    /// <summary>The IST day it was decided.</summary>
    public DateOnly Date { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Decided { get; set; } = string.Empty;

    /// <summary>Who decided: "owner", or "Claude (default)" for a default taken while the owner was busy.</summary>
    public string By { get; set; } = string.Empty;

    /// <summary><c>decided</c>, <c>default</c> or <c>open</c>.</summary>
    public string Status { get; set; } = "decided";

    /// <summary>The console user who recorded it.</summary>
    public string RecordedBy { get; set; } = string.Empty;

    public DateTime RecordedUtc { get; set; }

    public static readonly IReadOnlyList<string> Statuses = ["decided", "default", "open"];
}
