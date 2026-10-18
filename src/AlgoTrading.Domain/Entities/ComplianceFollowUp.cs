namespace AlgoTrading.Domain.Entities;

/// <summary>
/// What the desk did about a circular or a broker's notice the Compliance Triage found applies: the one alert sent
/// for it, and the owner's mark that the desk is ready for it.
/// </summary>
/// <remarks>
/// <para>
/// One row per circular (<see cref="CircularId"/> is unique), written before the alert is sent: the next triage of the
/// same circular finds it and sends nothing, and a crash between the two loses an alert rather than repeating one. In
/// October 2026 the circular that moves the index F&amp;O close to 3:45 p.m. reached the owner only as a line on Today
/// and in the digest.
/// </para>
/// <para>
/// "Mark handled" (<c>POST /api/Ai/compliance/{reportId}/handled</c>) fills <see cref="HandledUtc"/>, and makes the row
/// when no alert went yet, which then never goes. The checkup's "Rule changes ahead" lists what is not handled.
/// </para>
/// </remarks>
public class ComplianceFollowUp
{
    public long Id { get; set; }

    /// <summary>The circular (<see cref="RegulatoryCircular"/>); deleting it deletes this.</summary>
    public long CircularId { get; set; }

    /// <summary>The triage report the alert was raised from, or the one marked handled.</summary>
    public long? ReportId { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>When the alert went; null when the circular was marked handled before any alert.</summary>
    public DateTime? AlertedUtc { get; set; }

    /// <summary>The alert as sent, in plain text; empty when none went.</summary>
    public string AlertText { get; set; } = string.Empty;

    /// <summary>Whether Telegram's Desk System channel took the alert (false: not configured, or refused).</summary>
    public bool AlertDelivered { get; set; }

    /// <summary>When the owner marked the desk ready for it; null while it is not.</summary>
    public DateTime? HandledUtc { get; set; }

    /// <summary>Who marked it handled; empty while nobody has.</summary>
    public string HandledBy { get; set; } = string.Empty;

    /// <summary>What was done, as the owner wrote it.</summary>
    public string HandledNote { get; set; } = string.Empty;
}
