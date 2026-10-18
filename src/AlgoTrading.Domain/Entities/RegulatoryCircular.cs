namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One circular from SEBI, NSE or BSE, or one notice from a broker, that might bind this desk, as the circulars
/// recorder first saw it: its number, date, title, where it lives, and the first pages of its text.
/// </summary>
/// <remarks>
/// <para>
/// Written once: a circular seen again on the next day's poll matches <see cref="Key"/> and is skipped. The one change
/// afterwards is its text read again, whole, when the first read was cut at the old cap (<see cref="TextRereadUtc"/>).
/// The Compliance Triage reads these on Saturdays and quotes <see cref="Title"/> and <see cref="Excerpt"/>; its quote,
/// dates and times must be found in them.
/// </para>
/// <para>
/// Only the circulars that might concern an F&amp;O desk are kept: SEBI's circulars, NSE's from its F&amp;O,
/// clearing, inspection and legal departments or whose subject names algos, derivatives, risk or margins, and BSE's
/// notices on the same. <see cref="Relevance"/> says why each was kept.
/// </para>
/// </remarks>
public class RegulatoryCircular
{
    public long Id { get; set; }

    /// <summary>sebi, nse, bse or fyers (<see cref="RegulatoryCircularSource"/>).</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The duplicate check: the source and its own id for the circular ("nse:76848", "sebi:105081", "bse:20261010-1",
    /// "fyers:changes-to-fyers-api-rate-limits-and-usage").
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The circular's number as published ("NSE/FAOP/76827", "HO/38/24/(15)2026-MIRSD…"); empty when not given.</summary>
    public string Number { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The issuing department or category ("Futures &amp; Options"); empty when not given.</summary>
    public string Department { get; set; } = string.Empty;

    /// <summary>The date the source gives the circular, an IST day; null when it gives none it could read.</summary>
    public DateOnly? PublishedOn { get; set; }

    /// <summary>The circular's page (SEBI) or its document (NSE, BSE).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The document its text was read from (a PDF, or a zip holding one); empty when there was none.</summary>
    public string DocumentUrl { get; set; } = string.Empty;

    /// <summary>The first pages of the document's text, whitespace made single; empty when it could not be read.</summary>
    public string Excerpt { get; set; } = string.Empty;

    /// <summary>Why the recorder kept it, e.g. "department: Futures &amp; Options" or "subject: algo".</summary>
    public string Relevance { get; set; } = string.Empty;

    /// <summary>When the recorder first saw it.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>
    /// When its document was read again, to the larger cap, because the first read stopped at the old one (4,000
    /// characters until 18 Oct 2026); null when it never was. A triage written before this is written again.
    /// </summary>
    public DateTime? TextRereadUtc { get; set; }
}

/// <summary>Where a circular came from: a regulator, an exchange, or a broker's notice board.</summary>
public static class RegulatoryCircularSource
{
    public const string Sebi = "sebi";
    public const string Nse = "nse";
    public const string Bse = "bse";

    /// <summary>
    /// FYERS's notice board (fyers.in/notice-board), from 18 Oct 2026: on 10 Mar 2026 it announced SEBI's retail algo
    /// framework (daily 2FA, no refresh tokens), and the desk learned of it when a refresh was refused on 13 Oct.
    /// </summary>
    public const string Fyers = "fyers";

    /// <summary>The brokers among the sources: their notices have no number, and their text is the page itself.</summary>
    public static bool IsBroker(string? source) => source == Fyers;
}
