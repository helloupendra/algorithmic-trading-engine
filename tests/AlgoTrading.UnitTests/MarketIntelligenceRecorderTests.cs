using System.Net;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static AlgoTrading.UnitTests.MarketIntelligenceTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The recorders end to end against an in-memory database and a stand-in web
/// serving the real answers.
/// </summary>
/// <remarks>
/// The property everything else depends on is that a row, once stored, keeps
/// the moment it was first seen: a forecast replayed later must see exactly
/// what was known at 08:50 that day. So every recorder is run twice, later,
/// over the same answer, and the first sighting must stand.
/// </remarks>
public class MarketIntelligenceRecorderTests
{
    private const string EtMarkets = "https://example.test/et-markets.xml";
    private const string EtStocks = "https://example.test/et-stocks.xml";
    private const string Bbc = "https://example.test/bbc.xml";

    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    private static NewsRecorder News(Infrastructure.Persistence.TradingDbContext db, FakeWeb web, MarketIntelligenceStatus status, Func<DateTime> clock,
        params (string Category, string Source, string Url)[] feeds) =>
        new(db, new Factory(web), status, NullLogger<NewsRecorder>.Instance)
        {
            Clock = clock,
            Feeds = feeds.Select(f => (f.Category, new NewsFeed(f.Source, f.Url, Ist))).ToList(),
        };

    // ---------- news ----------

    [Fact]
    public async Task A_headline_is_stored_once_and_keeps_the_moment_it_was_first_seen()
    {
        await using var db = Db();
        var web = new FakeWeb()
            .Serve(EtMarkets, Fixture("rss_economictimes_markets.xml"))
            .Serve(EtStocks, Fixture("rss_economictimes_stocks.xml"));
        var status = new MarketIntelligenceStatus();
        var now = Ist(2026, 9, 27, 19, 0);
        var recorder = News(db, web, status, () => now,
            ("india", "Economic Times · Markets", EtMarkets), ("india", "Economic Times · Stocks", EtStocks));

        var first = await recorder.RecordAsync(CancellationToken.None);

        // ET Stocks carries two of ET Markets' three stories: three headlines, not five.
        Assert.Equal(3, first.Stored);
        Assert.Equal(3, await db.NewsItems.CountAsync());
        Assert.All(db.NewsItems, n => Assert.Equal(now, n.FirstSeenUtc));
        Assert.All(db.NewsItems, n => Assert.Equal("Economic Times · Markets", n.Source));   // the first feed files it
        Assert.All(db.NewsItems, n => Assert.Null(n.ScoredUtc));

        // Five and fifty minutes later the same feeds carry the same stories.
        now = now.AddMinutes(5);
        Assert.Equal(0, (await recorder.RecordAsync(CancellationToken.None)).Stored);
        now = now.AddMinutes(50);
        Assert.Equal(0, (await recorder.RecordAsync(CancellationToken.None)).Stored);

        Assert.Equal(3, await db.NewsItems.CountAsync());
        Assert.All(db.NewsItems.AsNoTracking(), n => Assert.Equal(Ist(2026, 9, 27, 19, 0), n.FirstSeenUtc));
    }

    [Fact]
    public async Task A_headline_edited_by_its_publisher_keeps_its_first_title_and_time()
    {
        await using var db = Db();
        string feed = Fixture("rss_economictimes_markets.xml");
        var web = new FakeWeb().Serve(EtMarkets, feed);
        var now = Ist(2026, 9, 27, 19, 0);
        var recorder = News(db, web, new MarketIntelligenceStatus(), () => now, ("india", "Economic Times · Markets", EtMarkets));
        await recorder.RecordAsync(CancellationToken.None);

        // Same link, new headline and date: the story as it was first seen is the record.
        web.Serve(EtMarkets, feed.Replace("Father time always wins!", "UPDATED: Father time always wins!")
            .Replace("Sun, 27 Sep 2026 17:08:51 +0530", "Sun, 27 Sep 2026 20:00:00 +0530"));
        now = now.AddHours(1);
        Assert.Equal(0, (await recorder.RecordAsync(CancellationToken.None)).Stored);

        var buffett = await db.NewsItems.AsNoTracking().SingleAsync(n => n.Title.Contains("Buffett"));
        Assert.StartsWith("Father time", buffett.Title);
        Assert.Equal(Ist(2026, 9, 27, 19, 0), buffett.FirstSeenUtc);
        Assert.Equal(new DateTime(2026, 9, 27, 11, 38, 51, DateTimeKind.Utc), buffett.PublishedUtc);
    }

    [Fact]
    public async Task One_dead_feed_costs_only_its_own_headlines_and_is_reported_until_it_recovers()
    {
        await using var db = Db();
        var web = new FakeWeb()
            .Serve(EtMarkets, Fixture("rss_economictimes_markets.xml"))
            .Serve(Bbc, "Service Unavailable", HttpStatusCode.ServiceUnavailable);
        var status = new MarketIntelligenceStatus();
        var now = Ist(2026, 9, 27, 19, 0);
        var recorder = News(db, web, status, () => now, ("india", "Economic Times · Markets", EtMarkets), ("global", "BBC Business", Bbc));

        var report = await recorder.RecordAsync(CancellationToken.None);
        var health = status.Recorder(MarketIntelligenceNames.News);

        Assert.Equal(3, report.Stored);
        Assert.Equal(1, report.FeedsFailed);
        Assert.Equal("HTTP 503", health.FailingSources["BBC Business"]);
        Assert.Equal(now, health.LastSuccessUtc);   // a poll with one feed down is still a poll

        web.Serve(Bbc, Fixture("rss_bbc_business.xml"));
        now = now.AddMinutes(5);
        report = await recorder.RecordAsync(CancellationToken.None);

        Assert.Equal(2, report.Stored);
        Assert.Empty(health.FailingSources);
        Assert.Equal("global", (await db.NewsItems.FirstAsync(n => n.Source == "BBC Business")).Category);
        Assert.Equal(now, (await db.NewsItems.FirstAsync(n => n.Source == "BBC Business")).FirstSeenUtc);
    }

    [Fact]
    public async Task A_poll_where_every_feed_fails_is_a_failed_poll()
    {
        await using var db = Db();
        var status = new MarketIntelligenceStatus();
        var recorder = News(db, new FakeWeb(), status, () => Ist(2026, 9, 27, 19, 0), ("india", "Economic Times · Markets", EtMarkets));

        await recorder.RecordAsync(CancellationToken.None);

        var health = status.Recorder(MarketIntelligenceNames.News);
        Assert.Null(health.LastSuccessUtc);
        Assert.Equal("every feed failed", health.LastError);
    }

    [Fact]
    public void Every_recorder_feed_is_filed_under_a_console_category_and_read_once()
    {
        var keys = NewsFeedCatalog.ConsoleCategories.Select(c => c.Key).ToHashSet();
        var feeds = NewsFeedCatalog.RecorderFeeds();

        Assert.All(feeds, f => Assert.Contains(f.Category, keys));
        Assert.Equal(feeds.Count, feeds.Select(f => f.Feed.Url).Distinct().Count());
        Assert.All(feeds, f => Assert.True(f.Feed.Source.Length <= 100, f.Feed.Source));

        // The console's own list is unchanged by the recorder's extra feeds.
        Assert.Equal(16, NewsFeedCatalog.ConsoleCategories.Sum(c => c.Feeds.Count));
    }

    // ---------- NSE filings ----------

    private static (CorporateFilingsRecorder Recorder, FakeWeb Web) Filings(Infrastructure.Persistence.TradingDbContext db, MarketIntelligenceStatus status, Func<DateTime> clock)
    {
        var web = new FakeWeb();
        var nse = new NseWebClient(new Factory(web), new NseRequestPacer(TimeSpan.Zero), NullLogger<NseWebClient>.Instance) { Clock = clock };
        return (new CorporateFilingsRecorder(db, nse, status, NullLogger<CorporateFilingsRecorder>.Instance) { Clock = clock }, web);
    }

    [Fact]
    public async Task The_first_poll_after_a_start_asks_for_the_missed_days_and_later_polls_do_not()
    {
        await using var db = Db();
        var now = Ist(2026, 9, 27, 19, 0);
        var (recorder, web) = Filings(db, new MarketIntelligenceStatus(), () => now);
        string range = CorporateFilingsRecorder.AnnouncementsForDaysUrl(new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 27));
        web.Serve(CorporateFilingsRecorder.LatestAnnouncementsUrl, Fixture("nse_announcements_latest.json"))
            .Serve(range, Fixture("nse_announcements_range.json"));

        var first = await recorder.RecordAnnouncementsAsync(firstSinceStart: true, CancellationToken.None);

        Assert.True(first.AskedForWholeDays);
        Assert.Contains(range, web.Requests);
        Assert.Equal(7, first.Stored);
        Assert.Equal(7, await db.CorporateAnnouncements.CountAsync());

        web.Requests.Clear();
        now = now.AddMinutes(10);
        var second = await recorder.RecordAnnouncementsAsync(firstSinceStart: false, CancellationToken.None);

        Assert.False(second.AskedForWholeDays);
        Assert.DoesNotContain(web.Requests, u => u.Contains("from_date"));
        Assert.Equal(0, second.Stored);
        Assert.All(db.CorporateAnnouncements.AsNoTracking(), a => Assert.Equal(Ist(2026, 9, 27, 19, 0), a.FirstSeenUtc));
    }

    [Fact]
    public async Task When_every_one_of_the_latest_is_new_the_whole_day_is_asked_for()
    {
        await using var db = Db();
        var now = Ist(2026, 9, 27, 19, 0);
        var (recorder, web) = Filings(db, new MarketIntelligenceStatus(), () => now);
        web.Serve(CorporateFilingsRecorder.LatestAnnouncementsUrl, Fixture("nse_announcements_range.json"))
            .Serve(CorporateFilingsRecorder.AnnouncementsForDaysUrl(new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 27)), Fixture("nse_announcements_range.json"));
        await recorder.RecordAnnouncementsAsync(firstSinceStart: true, CancellationToken.None);

        // Ten minutes later the latest 20 are all filings never seen: a burst may have pushed some out of the answer.
        web.Serve(CorporateFilingsRecorder.LatestAnnouncementsUrl, Fixture("nse_announcements_latest.json"));
        web.Requests.Clear();
        now = now.AddMinutes(10);
        var report = await recorder.RecordAnnouncementsAsync(firstSinceStart: false, CancellationToken.None);

        Assert.True(report.AskedForWholeDays);
        // From the newest stored filing's day (26 Sep), not further back.
        Assert.Contains(CorporateFilingsRecorder.AnnouncementsForDaysUrl(new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 27)), web.Requests);
        Assert.Equal(4, report.Stored);
    }

    [Fact]
    public async Task NSE_is_asked_like_a_browser_and_a_refusal_fetches_fresh_cookies_once()
    {
        await using var db = Db();
        var now = Ist(2026, 9, 27, 19, 0);
        var (recorder, web) = Filings(db, new MarketIntelligenceStatus(), () => now);
        int pageLoads = 0, apiCalls = 0;
        web.Serve(CorporateFilingsRecorder.AnnouncementsPage, () =>
        {
            pageLoads++;
            var page = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html></html>") };
            page.Headers.Add("Set-Cookie", $"nsit=session{pageLoads}; Path=/; Secure");
            return page;
        });
        web.Serve(CorporateFilingsRecorder.LatestAnnouncementsUrl, () =>
            ++apiCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixture("nse_announcements_latest.json")) });
        web.Serve(CorporateFilingsRecorder.AnnouncementsForDaysUrl(new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 27)), Fixture("nse_announcements_range.json"));

        var report = await recorder.RecordAnnouncementsAsync(firstSinceStart: true, CancellationToken.None);

        Assert.Equal(7, report.Stored);
        Assert.Equal(2, pageLoads);
        var retried = web.Messages.Last(m => m.RequestUri!.ToString() == CorporateFilingsRecorder.LatestAnnouncementsUrl);
        Assert.Equal("nsit=session2", Assert.Single(retried.Headers.GetValues("Cookie")));
        Assert.Equal(CorporateFilingsRecorder.AnnouncementsPage, retried.Headers.Referrer!.ToString());
    }

    [Fact]
    public async Task A_failing_announcements_poll_is_recorded_and_surfaced()
    {
        await using var db = Db();
        var status = new MarketIntelligenceStatus();
        var (recorder, _) = Filings(db, status, () => Ist(2026, 9, 27, 19, 0));

        await Assert.ThrowsAsync<HttpRequestException>(() => recorder.RecordAnnouncementsAsync(false, CancellationToken.None));

        var health = status.Recorder(MarketIntelligenceNames.Announcements);
        Assert.Equal("HTTP 404", health.LastError?.Split(": ").Last());
        Assert.Null(health.LastSuccessUtc);
    }

    [Fact]
    public async Task A_board_meeting_is_stored_once_with_when_it_was_first_known()
    {
        await using var db = Db();
        var now = Ist(2026, 9, 27, 19, 0);
        var (recorder, web) = Filings(db, new MarketIntelligenceStatus(), () => now);
        web.Serve(CorporateFilingsRecorder.CalendarUrl(new DateOnly(2026, 9, 27), new DateOnly(2026, 9, 27).AddDays(CorporateFilingsRecorder.CalendarDaysAhead)),
            Fixture("nse_event_calendar.json"));

        Assert.Equal(4, (await recorder.RecordCalendarAsync(CancellationToken.None)).Stored);
        now = now.AddHours(4);
        Assert.Equal(0, (await recorder.RecordCalendarAsync(CancellationToken.None)).Stored);

        Assert.All(db.CorporateCalendar.AsNoTracking(), e => Assert.Equal(Ist(2026, 9, 27, 19, 0), e.FirstSeenUtc));
    }

    // ---------- global markets ----------

    private static GlobalMarketsRecorder Global(Infrastructure.Persistence.TradingDbContext db, FakeWeb web, MarketIntelligenceStatus status, Func<DateTime> clock) =>
        new(db, new Factory(web), new GlobalCuesService(new Factory(web), NullLogger<GlobalCuesService>.Instance), status, NullLogger<GlobalMarketsRecorder>.Instance)
        {
            Clock = clock,
            QuotePause = TimeSpan.Zero,
            HistoryPause = TimeSpan.Zero,
        };

    private static bool IsGspc(string url) => url.Contains("GSPC", StringComparison.Ordinal);

    [Fact]
    public async Task A_snapshot_stores_gift_nifty_and_every_global_key_under_one_fetch_time()
    {
        await using var db = Db();
        var web = new FakeWeb().ServeWhen(u => u.StartsWith("https://query1.finance.yahoo.com/", StringComparison.Ordinal), Fixture("yahoo_chart_gspc_sample.json"));
        var status = new MarketIntelligenceStatus();
        var at = Ist(2026, 9, 28, 8, 45);
        var recorder = Global(db, web, status, () => at);
        recorder.GiftNiftySource = _ => Task.FromResult<GiftNiftyQuote?>(
            new GiftNiftyQuote(23333.00m, -52.00m, -0.22m, new DateOnly(2026, 9, 29), 52899, Ist(2026, 9, 28, 8, 44, 50)));

        var report = await recorder.SnapshotAsync(CancellationToken.None);

        Assert.Equal(GlobalMarketKeys.All.Count + 1, report.Stored);
        var rows = await db.MarketQuoteSnapshots.AsNoTracking().ToListAsync();
        Assert.All(rows, r => Assert.Equal(at, r.FetchedUtc));

        var gift = rows.Single(r => r.Key == GlobalMarketKeys.GiftNifty);
        Assert.Equal("nseix", gift.Source);
        Assert.Equal(23385.00m, gift.PreviousClose);
        Assert.Equal(-0.22m, gift.ChangePct);

        var spx = rows.Single(r => r.Key == "SPX");
        Assert.Equal("yahoo:^GSPC", spx.Source);
        Assert.Equal(YahooDailyBars.ChangePct(spx.Price, spx.PreviousClose), spx.ChangePct);
    }

    [Fact]
    public async Task A_snapshot_without_gift_nifty_still_stores_the_rest()
    {
        await using var db = Db();
        var web = new FakeWeb().ServeWhen(IsGspc, Fixture("yahoo_chart_gspc_sample.json"));
        var status = new MarketIntelligenceStatus();
        var recorder = Global(db, web, status, () => Ist(2026, 9, 28, 8, 45));
        recorder.GiftNiftySource = _ => throw new HttpRequestException("NSE IX refused the token");

        var report = await recorder.SnapshotAsync(CancellationToken.None);

        Assert.Equal(1, report.Stored);   // only ^GSPC answered
        Assert.Contains(report.Errors, e => e.StartsWith("GIFTNIFTY: NSE IX refused the token", StringComparison.Ordinal));
        Assert.Contains(GlobalMarketKeys.GiftNifty, status.Recorder(MarketIntelligenceNames.QuoteSnapshots).FailingSources.Keys);
    }

    [Fact]
    public async Task Daily_bars_wait_for_the_backfill_window_then_store_only_finished_days()
    {
        await using var db = Db();
        var web = new FakeWeb().ServeWhen(IsGspc, Fixture("yahoo_chart_gspc_sample.json"));
        var status = new MarketIntelligenceStatus();

        // Tue 22 Sep 2026, 03:00 IST: New York's Monday (21 Sep) is not over for the futures yet.
        var now = Ist(2026, 9, 22, 3, 0);
        var recorder = Global(db, web, status, () => now);

        var waiting = await recorder.UpdateDailyAsync(allowHistory: false, CancellationToken.None);
        Assert.Equal(GlobalMarketKeys.All.Count, waiting.Skipped);
        Assert.Empty(web.Requests);

        await recorder.UpdateDailyAsync(allowHistory: true, CancellationToken.None);
        Assert.Equal(new[] { new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18) },
            await db.MarketGlobalDaily.Where(x => x.Symbol == "SPX").OrderBy(x => x.Date).Select(x => x.Date).ToListAsync());
        // The markets this fake does not serve are reported one by one; SPX is not among them.
        Assert.DoesNotContain("SPX", status.Recorder(MarketIntelligenceNames.GlobalDaily).FailingSources.Keys);
        Assert.Contains("NDX", status.Recorder(MarketIntelligenceNames.GlobalDaily).FailingSources.Keys);

        // 08:00 IST the same day: 21 Sep is final, and the rest is written again in place, not twice.
        now = Ist(2026, 9, 22, 8, 0);
        await recorder.UpdateDailyAsync(allowHistory: true, CancellationToken.None);

        var spx = await db.MarketGlobalDaily.AsNoTracking().Where(x => x.Symbol == "SPX").OrderBy(x => x.Date).ToListAsync();
        Assert.Equal(3, spx.Count);
        Assert.Equal(new DateOnly(2026, 9, 21), spx[^1].Date);
        Assert.Equal(7764.700195m, spx[^1].Close);
        Assert.Equal("yahoo:^GSPC", spx[^1].Source);
        Assert.Equal(now, spx[0].FetchedUtc);
    }

    [Fact]
    public async Task A_market_already_up_to_date_is_not_asked_again()
    {
        await using var db = Db();
        var web = new FakeWeb().ServeWhen(IsGspc, Fixture("yahoo_chart_gspc_sample.json"));
        var now = Ist(2026, 9, 22, 8, 0);
        var recorder = Global(db, web, new MarketIntelligenceStatus(), () => now);
        await recorder.UpdateDailyAsync(allowHistory: true, CancellationToken.None);

        web.Requests.Clear();
        var again = await recorder.UpdateDailyAsync(allowHistory: true, CancellationToken.None);

        Assert.DoesNotContain(web.Requests, IsGspc);
        Assert.Equal(GlobalMarketKeys.All.Count - 1, again.Failed);   // the rest still have no rows and 404 in this fake
    }

    // ---------- breadth ----------

    private static BreadthRecorder Breadth(Infrastructure.Persistence.TradingDbContext db, FakeWeb web) =>
        new(db, new Factory(web), new NseRequestPacer(TimeSpan.Zero), NullLogger<BreadthRecorder>.Instance) { MinimumEquities = 5 };

    [Fact]
    public async Task A_UDiFF_day_is_stored_with_its_52_week_counts_and_a_second_fetch_replaces_it()
    {
        await using var db = Db();
        var day = new DateOnly(2026, 9, 25);
        var web = new FakeWeb()
            .Serve(BreadthRecorder.UdiffUrl(day), Zip("BhavCopy_NSE_CM_0_0_0_20260925_F_0000.csv", Fixture("cm_bhavcopy_udiff_20260925_sample.csv")))
            .Serve(BreadthRecorder.Week52Url(day), Fixture("cm_52wk_high_low_25092026_sample.csv"));
        var recorder = Breadth(db, web);

        Assert.Equal(DayFetchResult.Stored, (await recorder.FetchDayAsync(day, CancellationToken.None)).Result);
        Assert.Equal(DayFetchResult.Stored, (await recorder.FetchDayAsync(day, CancellationToken.None)).Result);

        var row = await db.MarketBreadthDaily.AsNoTracking().SingleAsync();
        Assert.Equal((6, 3, 1, 10), (row.Advances, row.Declines, row.Unchanged, row.Traded));
        Assert.Equal((2, 1), (row.Highs52w!.Value, row.Lows52w!.Value));
        Assert.DoesNotContain(BreadthRecorder.LegacyUrl(day), web.Requests);
    }

    [Fact]
    public async Task Before_July_2024_the_legacy_file_is_asked_first_and_a_missing_52_week_file_leaves_the_counts_empty()
    {
        await using var db = Db();
        var day = new DateOnly(2024, 7, 5);
        var web = new FakeWeb()
            .Serve(BreadthRecorder.LegacyUrl(day), Zip("cm05JUL2024bhav.csv", Fixture("cm_bhavcopy_legacy_20240705_sample.csv")));
        var recorder = Breadth(db, web);

        Assert.Equal(DayFetchResult.Stored, (await recorder.FetchDayAsync(day, CancellationToken.None)).Result);

        Assert.Equal("https://archives.nseindia.com/content/historical/EQUITIES/2024/JUL/cm05JUL2024bhav.csv.zip", web.Requests[0]);
        Assert.DoesNotContain(BreadthRecorder.UdiffUrl(day), web.Requests);
        var row = await db.MarketBreadthDaily.AsNoTracking().SingleAsync();
        Assert.Equal(NseBreadthParsers.LegacyFormat, row.Source);
        Assert.Null(row.Highs52w);
    }

    [Fact]
    public async Task A_day_with_no_file_in_either_format_is_not_published_and_a_misdated_file_is_a_failure()
    {
        await using var db = Db();
        var web = new FakeWeb();
        var recorder = Breadth(db, web);
        var holiday = new DateOnly(2026, 10, 2);

        Assert.Equal(DayFetchResult.NotPublished, (await recorder.FetchDayAsync(holiday, CancellationToken.None)).Result);
        Assert.Equal(new[] { BreadthRecorder.UdiffUrl(holiday), BreadthRecorder.LegacyUrl(holiday) }, web.Requests);

        // NSE served 25 Sep's file under 28 Sep's name.
        var monday = new DateOnly(2026, 9, 28);
        web.Serve(BreadthRecorder.UdiffUrl(monday), Zip("x.csv", Fixture("cm_bhavcopy_udiff_20260925_sample.csv")));
        var outcome = await recorder.FetchDayAsync(monday, CancellationToken.None);
        Assert.Equal(DayFetchResult.Failed, outcome.Result);
        Assert.Contains("dated 2026-09-25", outcome.Error);
        Assert.Empty(db.MarketBreadthDaily);
    }
}
