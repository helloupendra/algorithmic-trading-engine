namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One listed company's filing to the exchange (results, board outcomes,
/// orders won, insider trades, rating changes...), as NSE broadcasts it on its
/// corporate-announcements page.
/// </summary>
/// <remarks>
/// <para>
/// Two times, for two uses. <see cref="AnnouncedUtc"/> is the exchange's own
/// dissemination time: the moment the filing became public. <see cref="FirstSeenUtc"/>
/// is when the recorder stored it, normally within ten minutes of the broadcast
/// (the poll interval). For a live model, FirstSeenUtc is the conservative
/// point-in-time test. The two differ by more than a poll only for a filing
/// fetched late: after the API was down, the recorder asks NSE for the missed
/// days by date, and those rows carry a FirstSeenUtc hours after AnnouncedUtc.
/// </para>
/// <para>
/// Written once, never changed by the recorder: <see cref="UniqueKey"/> is the
/// duplicate check, so a filing seen on every poll is stored on the first.
/// </para>
/// </remarks>
public class CorporateAnnouncement : IScoredText
{
    public long Id { get; set; }

    /// <summary>"NSE".</summary>
    public string Exchange { get; set; } = "NSE";

    /// <summary>The exchange symbol, e.g. "RELIANCE".</summary>
    public string Symbol { get; set; } = string.Empty;

    public string Company { get; set; } = string.Empty;

    /// <summary>The exchange's subject line for the filing, e.g. "Outcome of Board Meeting".</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The exchange's summary of what the company said.</summary>
    public string Details { get; set; } = string.Empty;

    /// <summary>The filing's PDF or XBRL on the exchange's archive; empty when none.</summary>
    public string AttachmentUrl { get; set; } = string.Empty;

    /// <summary>When the exchange broadcast it, in UTC; null when the answer did not say.</summary>
    public DateTime? AnnouncedUtc { get; set; }

    /// <summary>When the recorder first stored it. Never updated.</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>SHA-256 (lower-case hex) of the exchange's own id for the filing; the duplicate check.</summary>
    public string UniqueKey { get; set; } = string.Empty;

    public decimal? Sentiment { get; set; }
    public short? Importance { get; set; }
    public string Symbols { get; set; } = string.Empty;
    public string Topics { get; set; } = string.Empty;
    public DateTime? ScoredUtc { get; set; }
    public string ScoreModel { get; set; } = string.Empty;
}
