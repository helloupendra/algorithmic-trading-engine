namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One headline from a publisher's RSS feed, as the news recorder first saw it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FirstSeenUtc"/> is the point-in-time stamp: the moment the desk
/// knew the headline existed. A model issued at 08:50 IST may use a row only
/// when its FirstSeenUtc is before 08:50 IST that day. <see cref="PublishedUtc"/>
/// is what the feed claims and is kept for reading, not for that test: feeds
/// back-date, re-date and omit it.
/// </para>
/// <para>
/// A row is written once and never changed by the recorder. A headline seen
/// again (the next poll, or another feed carrying the same link) matches
/// <see cref="LinkHash"/> and is skipped, so FirstSeenUtc is always the first
/// sighting. RSS keeps no archive: there is no history before the day the
/// recorder started, and a headline that scrolled off a feed while the
/// recorder was down is lost.
/// </para>
/// </remarks>
public class NewsItem : IScoredText
{
    public long Id { get; set; }

    /// <summary>The feed's name as the console shows it, e.g. "Economic Times · Markets".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>The news category key the feed is filed under: india, global, commodities, or a sector key.</summary>
    public string Category { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>The feed's description, stripped of HTML; empty when the feed gives none.</summary>
    public string Summary { get; set; } = string.Empty;

    public string Link { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 (lower-case hex) of the normalised link, or of source + title
    /// when an item has no link. The duplicate check.
    /// </summary>
    public string LinkHash { get; set; } = string.Empty;

    /// <summary>The feed's pubDate, in UTC; null when absent or unreadable.</summary>
    public DateTime? PublishedUtc { get; set; }

    /// <summary>When the recorder first saw the item. Never updated.</summary>
    public DateTime FirstSeenUtc { get; set; }

    public decimal? Sentiment { get; set; }
    public short? Importance { get; set; }
    public string Symbols { get; set; } = string.Empty;
    public string Topics { get; set; } = string.Empty;
    public DateTime? ScoredUtc { get; set; }
    public string ScoreModel { get; set; } = string.Empty;
}
