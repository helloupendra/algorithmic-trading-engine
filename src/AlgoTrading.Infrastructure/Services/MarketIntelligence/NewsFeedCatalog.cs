using AlgoTrading.Contracts.MarketIntel;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>One publisher's RSS feed.</summary>
/// <param name="Source">The name shown with each headline, e.g. "Economic Times · Markets".</param>
/// <param name="Url">The feed's address.</param>
/// <param name="ZoneIfUnstated">
/// The offset a pubDate without one is read in. RBI writes "Fri, 25 Sep 2026
/// 21:50:00" with no zone; read as UTC that would be five and a half hours late.
/// </param>
public sealed record NewsFeed(string Source, string Url, TimeSpan ZoneIfUnstated);

/// <summary>A news category: its key, the console's label and group, and its feeds.</summary>
public sealed record NewsFeedCategory(string Key, string Label, string Group, IReadOnlyList<NewsFeed> Feeds);

/// <summary>
/// Every news feed the desk reads, in one place: the categories the console's
/// news section shows, and the extra feeds only the recorder reads.
/// </summary>
/// <remarks>
/// <para>
/// All feeds are the publishers' own public RSS endpoints, which is what they
/// are published for. An aggregator's feed would read the same but carries
/// terms that forbid using it inside a product.
/// </para>
/// <para>
/// The sector feeds are Economic Times' industry sections, one per sector, so a
/// category is the publisher's own idea of "pharma" rather than a keyword match
/// over general news that would file every mention of the word under it. IT is
/// the exception: ET's technology feed carries barely a headline at a time, so
/// that one is Business Standard and Mint, both of which keep a full section.
/// </para>
/// <para>
/// The recorder-only feeds are filed under the console's own category keys, so
/// <c>news_items.Category</c> has one vocabulary. They are kept out of the
/// console so its tabs read as they did. Each was checked on 27 Sep 2026: it
/// answered, carried current items, and is the publisher's or the institution's
/// own feed. Checked and left out: Moneycontrol (its feeds stopped in April
/// 2024), WSJ and MarketWatch's market pulse (stopped in 2025).
/// </para>
/// </remarks>
public static class NewsFeedCatalog
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);
    private static readonly TimeSpan Utc = TimeSpan.Zero;

    /// <summary>The console's news categories, in the order it shows them.</summary>
    public static readonly IReadOnlyList<NewsFeedCategory> ConsoleCategories =
    [
        new("india", "India markets", NewsCategoryGroups.Markets,
        [
            new("Economic Times · Markets", "https://economictimes.indiatimes.com/markets/rssfeeds/1977021501.cms", Ist),
            new("Economic Times · Stocks", "https://economictimes.indiatimes.com/markets/stocks/rssfeeds/2146842.cms", Ist),
            new("Business Standard · Markets", "https://www.business-standard.com/rss/markets-106.rss", Ist),
            new("Mint · Markets", "https://www.livemint.com/rss/markets", Ist),
        ]),
        new("global", "Global", NewsCategoryGroups.Markets,
        [
            new("BBC Business", "https://feeds.bbci.co.uk/news/business/rss.xml", Utc),
            new("Economic Times · Forex", "https://economictimes.indiatimes.com/markets/forex/rssfeeds/1150221130.cms", Ist),
        ]),
        new("commodities", "Commodities", NewsCategoryGroups.Markets,
        [
            new("Economic Times · Commodities", "https://economictimes.indiatimes.com/markets/commodities/rssfeeds/1808152121.cms", Ist),
        ]),

        new("banking", "Banking & financials", NewsCategoryGroups.Sectors,
        [
            new("Economic Times · Banking/Finance", "https://economictimes.indiatimes.com/rssfeeds/13358259.cms", Ist),
        ]),
        new("pharma", "Pharma & healthcare", NewsCategoryGroups.Sectors,
        [
            new("Economic Times · Healthcare/Biotech", "https://economictimes.indiatimes.com/rssfeeds/13358050.cms", Ist),
        ]),
        new("it", "IT & technology", NewsCategoryGroups.Sectors,
        [
            new("Business Standard · Technology", "https://www.business-standard.com/rss/technology-108.rss", Ist),
            new("Mint · Technology", "https://www.livemint.com/rss/technology", Ist),
        ]),
        new("auto", "Auto", NewsCategoryGroups.Sectors,
        [
            new("Economic Times · Auto", "https://economictimes.indiatimes.com/rssfeeds/13359412.cms", Ist),
        ]),
        new("energy", "Energy & power", NewsCategoryGroups.Sectors,
        [
            new("Economic Times · Energy", "https://economictimes.indiatimes.com/rssfeeds/13358350.cms", Ist),
        ]),
        new("fmcg", "FMCG & consumer", NewsCategoryGroups.Sectors,
        [
            new("Economic Times · Cons. Products", "https://economictimes.indiatimes.com/rssfeeds/13358759.cms", Ist),
        ]),
        new("metals", "Metals & mining", NewsCategoryGroups.Sectors,
        [
            new("Economic Times · Metals & Mining", "https://economictimes.indiatimes.com/rssfeeds/13357828.cms", Ist),
        ]),
        new("realty", "Realty & construction", NewsCategoryGroups.Sectors,
        [
            new("Economic Times · Property/Construction", "https://economictimes.indiatimes.com/rssfeeds/13357019.cms", Ist),
        ]),
    ];

    /// <summary>
    /// Feeds only the recorder reads, by category key: the economy and policy
    /// news the market sections leave out, the regulators' and central banks'
    /// own releases, and overnight news from the US and Asia.
    /// </summary>
    public static readonly IReadOnlyList<(string Category, NewsFeed Feed)> RecorderOnlyFeeds =
    [
        // India: the economy desks, and what RBI and SEBI publish themselves
        // (policy, auctions, circulars, orders), which reach the market before
        // the papers write them up.
        ("india", new("Economic Times · Economy", "https://economictimes.indiatimes.com/news/economy/rssfeeds/1373380680.cms", Ist)),
        ("india", new("Business Standard · Economy", "https://www.business-standard.com/rss/economy-102.rss", Ist)),
        ("india", new("Mint · Economy", "https://www.livemint.com/rss/economy", Ist)),
        ("india", new("BusinessLine · Markets", "https://www.thehindubusinessline.com/markets/feeder/default.rss", Ist)),
        ("india", new("BusinessLine · Economy", "https://www.thehindubusinessline.com/economy/feeder/default.rss", Ist)),
        ("india", new("RBI · Press releases", "https://www.rbi.org.in/pressreleases_rss.xml", Ist)),
        ("india", new("SEBI", "https://www.sebi.gov.in/sebirss.xml", Ist)),

        // Global: what happened while India slept. US markets and economy,
        // Asia, the Fed and the ECB in their own words, and world news for
        // the geopolitics that moves oil and risk appetite.
        ("global", new("CNBC · US top news", "https://www.cnbc.com/id/100003114/device/rss/rss.html", Utc)),
        ("global", new("CNBC · Economy", "https://www.cnbc.com/id/20910258/device/rss/rss.html", Utc)),
        ("global", new("CNBC · Asia", "https://www.cnbc.com/id/19832390/device/rss/rss.html", Utc)),
        // RSS 1.0 with no dates on its items: FirstSeenUtc is the only time it has.
        ("global", new("Nikkei Asia", "https://asia.nikkei.com/rss/feed/nar", Utc)),
        ("global", new("MarketWatch · Top stories", "https://feeds.content.dowjones.io/public/rss/mw_topstories", Utc)),
        ("global", new("Financial Times · Markets", "https://www.ft.com/markets?format=rss", Utc)),
        ("global", new("Federal Reserve · Press releases", "https://www.federalreserve.gov/feeds/press_all.xml", Utc)),
        ("global", new("ECB · Press", "https://www.ecb.europa.eu/rss/press.html", Utc)),
        ("global", new("BBC World", "https://feeds.bbci.co.uk/news/world/rss.xml", Utc)),

        ("commodities", new("OilPrice.com", "https://oilprice.com/rss/main", Utc)),
    ];

    /// <summary>Every feed the recorder polls, with the category it files it under. The console's come first.</summary>
    public static IReadOnlyList<(string Category, NewsFeed Feed)> RecorderFeeds() =>
        ConsoleCategories.SelectMany(c => c.Feeds.Select(f => (c.Key, f)))
            .Concat(RecorderOnlyFeeds)
            .ToList();
}
