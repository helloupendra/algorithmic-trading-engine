using System.Globalization;
using System.Net;
using System.Text;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// One direct alert for each circular the Compliance Triage found applies to the desk: to Telegram's Desk System
/// channel, where Sentinel and the desk's own notices go, and, through <see cref="ComplianceFollowUp"/>, to Ira, who
/// speaks it to each linked owner (<c>IraDesk</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> SEBI's circular of 15 Oct 2026 moves the index F&amp;O close to 3:45 p.m. from 7 Dec 2026. The triage
/// found it applied on 17 Oct, and the owner heard of it as an info line on Today and one line in the digest.
/// </para>
/// <para>
/// <b>When.</b> A report that is <c>ok</c>, of the dated shape (<see cref="ComplianceTriageAgent.Format"/>), and says
/// the circular applies (<c>yes</c>). Never for "does not apply" or "unclear": those stay in the digest. Raised as the
/// report is written, and caught up on the agent's next pass (<see cref="CatchUpAsync"/>) for one written while the
/// API was going down.
/// </para>
/// <para>
/// <b>Once.</b> The follow-up row is written before anything is sent, and its circular is unique: a second triage of
/// the same circular, a re-read, a "Run now", finds the row and sends nothing. A circular the owner marked handled
/// before any alert has a row too. A crash between the row and the send loses that alert; the checkup's "Rule changes
/// ahead" still lists the circular.
/// </para>
/// </remarks>
public sealed class ComplianceAlerts(TradingDbContext db, TelegramSender telegram, ILogger<ComplianceAlerts> logger, TimeProvider? time = null)
{
    /// <summary>How far back, by when they were written, reports are caught up on: the triage's own lookback.</summary>
    public const int CatchUpDays = 21;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Whether <paramref name="report"/> raises an alert: the Compliance Triage's, ok, dated, and it applies.</summary>
    public static bool Raises(AiReport report, out ComplianceTriageAgent.Facts facts, out long circularId)
    {
        facts = null!;
        circularId = 0;
        if (report.AgentKey != AiCatalog.ComplianceTriage || report.SubjectType != AiReportSubject.Check || report.Status != AiReportStatus.Ok) return false;
        if (ComplianceTriageAgent.CircularIdOf(report.SubjectId) is not long id) return false;
        if (ComplianceTriageAgent.ReadFacts(report.DataJson) is not { } read || read.Format < ComplianceTriageAgent.Format || read.Applies != "yes") return false;
        facts = read;
        circularId = id;
        return true;
    }

    /// <summary>
    /// The alert for one report, when it raises one and none went for its circular: the row first, then Telegram.
    /// The row, or null when nothing was sent. Never throws for Telegram's trouble.
    /// </summary>
    public async Task<ComplianceFollowUp?> RaiseAsync(AiReport report, CancellationToken cancellationToken)
    {
        if (!Raises(report, out var facts, out long circularId)) return null;
        if (await db.ComplianceFollowUps.AsNoTracking().AnyAsync(f => f.CircularId == circularId, cancellationToken)) return null;
        var circular = await db.RegulatoryCirculars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == circularId, cancellationToken);
        if (circular is null) return null;

        var now = _time.GetUtcNow().UtcDateTime;
        if (AwaitsRereading(circular, report, now))
        {
            // The one alert would go without the dates on the pages the old cap cut off: it waits for the text read
            // whole and the triage written again on it, and goes then (CatchUpAsync).
            return null;
        }

        var row = new ComplianceFollowUp
        {
            CircularId = circularId,
            ReportId = report.Id,
            CreatedUtc = now,
            AlertedUtc = now,
            AlertText = ComplianceAlertText.Plain(circular, facts, report.Id),
        };
        db.ComplianceFollowUps.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another pass wrote it between the look and the write: the unique circular kept that one, which sends.
            db.Entry(row).State = EntityState.Detached;
            return null;
        }

        row.AlertDelivered = await telegram.SendHtmlAsync(ComplianceAlertText.Html(circular, facts, report.Id), TelegramChannel.System, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Compliance alert for circular {Circular} (report {Report}) {How}", circularId, report.Id,
            row.AlertDelivered ? "went to the Desk System channel" : "did not reach Telegram; Ira and the checkup still have it");
        return row;
    }

    /// <summary>
    /// Whether <paramref name="report"/> was written on less than the circular's whole text: the text was cut at the old
    /// cap and the recorder will still read it again (<see cref="RegulatoryCircularsRecorder.LegacyTextChars"/>, within
    /// <see cref="RegulatoryCircularsRecorder.RereadDays"/>), or it was read again after the report, which the triage
    /// then writes again.
    /// </summary>
    public static bool AwaitsRereading(RegulatoryCircular c, AiReport report, DateTime nowUtc)
    {
        if (c.TextRereadUtc is DateTime reread) return report.UpdatedUtc < reread;
        return c.DocumentUrl.Length > 0 && !RegulatoryCircularSource.IsBroker(c.Source)
            && c.Excerpt.Length == RegulatoryCircularsRecorder.LegacyTextChars
            && c.FirstSeenUtc >= nowUtc.AddDays(-RegulatoryCircularsRecorder.RereadDays);
    }

    /// <summary>
    /// The reports of the last <see cref="CatchUpDays"/> days that raise an alert and have none: each raised now. How
    /// many went.
    /// </summary>
    public async Task<int> CatchUpAsync(CancellationToken cancellationToken)
    {
        var since = _time.GetUtcNow().UtcDateTime.AddDays(-CatchUpDays);
        var candidates = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.ComplianceTriage && r.SubjectType == AiReportSubject.Check && r.Status == AiReportStatus.Ok
                        && r.UpdatedUtc >= since && r.DataJson.Contains("\"applies\":\"yes\""))
            .OrderBy(r => r.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        var owed = candidates.Where(r => Raises(r, out _, out _)).ToList();
        if (owed.Count == 0) return 0;
        var circularIds = owed.Select(r => ComplianceTriageAgent.CircularIdOf(r.SubjectId)!.Value).ToList();
        var done = (await db.ComplianceFollowUps.AsNoTracking().Where(f => circularIds.Contains(f.CircularId)).Select(f => f.CircularId)
            .ToListAsync(cancellationToken)).ToHashSet();

        int sent = 0;
        foreach (var report in owed.Where(r => !done.Contains(ComplianceTriageAgent.CircularIdOf(r.SubjectId)!.Value)))
        {
            if (await RaiseAsync(report, cancellationToken) is not null) sent++;
        }

        return sent;
    }
}

/// <summary>
/// The words of a compliance alert, built by code from the circular and its checked triage: the title, the source and
/// number, "applies to the desk", the dates it takes effect, what changes on the desk, and the link.
/// </summary>
public static class ComplianceAlertText
{
    /// <summary>The headline every alert starts with.</summary>
    public const string Headline = "Rule change: applies to the desk";

    /// <summary>"SEBI", "NSE", "BSE", "FYERS".</summary>
    public static string SourceName(string source) => source.Trim().ToLowerInvariant() switch
    {
        RegulatoryCircularSource.Sebi => "SEBI",
        RegulatoryCircularSource.Nse => "NSE",
        RegulatoryCircularSource.Bse => "BSE",
        RegulatoryCircularSource.Fyers => "FYERS",
        var other => other.ToUpperInvariant(),
    };

    /// <summary>"SEBI circular HO/47/… of 15 Oct 2026", "FYERS notice of 16 Oct 2026".</summary>
    public static string Origin(RegulatoryCircular c)
    {
        string kind = RegulatoryCircularSource.IsBroker(c.Source) ? "notice" : "circular";
        string number = c.Number.Length > 0 ? $" {c.Number}" : string.Empty;
        string date = c.PublishedOn is DateOnly d ? $" of {Day(d)}" : string.Empty;
        return $"{SourceName(c.Source)} {kind}{number}{date}";
    }

    /// <summary>"7 Dec 2026".</summary>
    public static string Day(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The alert in plain text, as it is kept and as the console shows it.</summary>
    public static string Plain(RegulatoryCircular c, ComplianceTriageAgent.Facts f, long reportId) =>
        string.Join('\n', Lines(c, f, reportId, html: false));

    /// <summary>The alert for Telegram (parse_mode HTML): the same lines, the circular's words escaped.</summary>
    public static string Html(RegulatoryCircular c, ComplianceTriageAgent.Facts f, long reportId) =>
        string.Join('\n', Lines(c, f, reportId, html: true));

    /// <summary>The longest line of the circular's words an alert carries: eight of them stay well inside Telegram's 4,096.</summary>
    public const int MaxLine = 320;

    private static IEnumerable<string> Lines(RegulatoryCircular c, ComplianceTriageAgent.Facts f, long reportId, bool html)
    {
        string E(string text) => html ? WebUtility.HtmlEncode(text) : text;
        yield return html ? $"<b>{Headline}</b>" : Headline;
        yield return html ? $"<b>{E(Clip(c.Title))}</b>" : Clip(c.Title);
        yield return E($"{Origin(c)}: applies to the desk{(f.Area.Length > 0 ? $" ({f.Area})" : string.Empty)}.");
        if (f.EffectiveDates.Count == 0)
        {
            yield return "Takes effect: the circular's text gives no date.";
        }
        else
        {
            yield return "Takes effect:";
            foreach (var d in f.EffectiveDates) yield return E($"• {Day(d.Date)}: {Clip(d.What)}");
        }

        yield return "On the desk:";
        IReadOnlyList<string> changes = f.DeskChanges.Count > 0 ? f.DeskChanges : f.Change.Length > 0 ? [f.Change] : [];
        foreach (string change in changes) yield return E($"• {Clip(change)}");
        if (changes.Count == 0) yield return "• (the triage named no change)";
        if (c.Url.Length > 0) yield return E(c.Url);
        yield return E($"Triage report #{reportId.ToString(CultureInfo.InvariantCulture)} on AI → Reports: mark it handled there once the desk is ready.");
    }

    /// <summary>
    /// What Ira says, short enough to speak: "A SEBI circular applies to the desk: Review of …. It takes effect on 7
    /// December 2026 and 8 February 2027. What changes is on the Desk System channel."
    /// </summary>
    public static string Spoken(string source, string title, IReadOnlyList<DateOnly> dates)
    {
        string kind = RegulatoryCircularSource.IsBroker(source) ? "notice" : "circular";
        var text = new StringBuilder($"A {SourceName(source)} {kind} applies to the desk: {title.Trim().TrimEnd('.')}.");
        var spoken = dates.Distinct().Order().Select(d => d.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)).ToList();
        text.Append(spoken.Count switch
        {
            0 => " It gives no date.",
            1 => $" It takes effect on {spoken[0]}.",
            _ => $" It takes effect on {string.Join(", ", spoken.Take(spoken.Count - 1))} and {spoken[^1]}.",
        });
        text.Append(" What changes is on the Desk System channel.");
        return text.ToString();
    }

    private static string Clip(string text)
    {
        string t = text.Trim();
        return t.Length <= MaxLine ? t : t[..(MaxLine - 1)] + "…";
    }
}
