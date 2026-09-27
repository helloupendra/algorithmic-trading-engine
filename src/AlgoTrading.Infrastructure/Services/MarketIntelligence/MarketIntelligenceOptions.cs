namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>
/// The <c>MarketIntelligence</c> section of appsettings: one switch per
/// recorder, all on unless set to false.
/// </summary>
/// <remarks>
/// A development machine that shares nothing with production can turn them
/// off, but none of them opens a broker or feed connection: they read public
/// web pages and archives, and write only this module's tables.
/// </remarks>
public sealed class MarketIntelligenceOptions
{
    public const string SectionName = "MarketIntelligence";

    /// <summary>Every RSS feed, every 5 minutes, all day, every day.</summary>
    public bool NewsEnabled { get; set; } = true;

    /// <summary>NSE's corporate announcements every 10 minutes and its board-meeting calendar every 4 hours, 06:00–23:30 IST, every day.</summary>
    public bool AnnouncementsEnabled { get; set; } = true;

    /// <summary>GIFT Nifty and the global keys every 15 minutes, 06:00–16:00 IST on weekdays.</summary>
    public bool QuoteSnapshotsEnabled { get; set; } = true;

    /// <summary>Overseas daily bars after 07:00 IST, and their history from 2020 for a symbol that has none.</summary>
    public bool GlobalDailyEnabled { get; set; } = true;

    /// <summary>NSE cash-market breadth each evening.</summary>
    public bool BreadthEnabled { get; set; } = true;

    /// <summary>The history backfills (breadth and participant OI from 2020), outside market hours.</summary>
    public bool BackfillEnabled { get; set; } = true;

    /// <summary><c>python -m analysis news-score</c> every 10 minutes, 06:00–23:30 IST.</summary>
    public bool NewsScoringEnabled { get; set; } = true;
}
