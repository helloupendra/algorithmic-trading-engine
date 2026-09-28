using AlgoTrading.Infrastructure.Services.MarketFactors;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using static AlgoTrading.UnitTests.MarketIntelligenceTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The market-intelligence readers, each against a real answer of 27 Sep 2026
/// (trimmed): RSS from ET, RBI, SEBI and the BBC, NSE's announcements and
/// event calendar, the participant-OI archive's older formats, both CM
/// bhavcopy formats, NSE's 52-week file and Yahoo's daily chart.
/// </summary>
/// <remarks>
/// What each test pins is a way the real source differs from the tidy
/// version: a pubDate with no zone, a comma after the month, click-tracking
/// on links, a title whose punctuation changed over six years. A reader that
/// gets one of those wrong stores a wrong time or a duplicate, and nothing
/// downstream can tell.
/// </remarks>
public class MarketIntelligenceParserTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);

    // ---------- RSS ----------

    [Fact]
    public void An_Economic_Times_feed_reads_titles_links_summaries_and_ist_dates()
    {
        var items = RssFeedParser.Parse(Fixture("rss_economictimes_markets.xml"), Ist);

        Assert.Equal(3, items.Count);
        Assert.Equal("Father time always wins! What Warren Buffett’s 4-word farewell message means", items[0].Title);
        Assert.StartsWith("https://economictimes.indiatimes.com/markets/stocks/news/father-time-always-wins", items[0].Link);
        Assert.StartsWith("In a significant transition, Warren Buffett", items[0].Summary);
        // "Sun, 27 Sep 2026 17:08:51 +0530"
        Assert.Equal(new DateTime(2026, 9, 27, 11, 38, 51, DateTimeKind.Utc), items[0].PublishedUtc);
        Assert.Equal(DateTimeKind.Utc, items[0].PublishedUtc!.Value.Kind);
    }

    [Fact]
    public void An_RBI_date_without_a_zone_is_read_as_IST_not_UTC()
    {
        // RBI writes "Fri, 25 Sep 2026 21:50:00", and its feed starts with a byte-order mark.
        var items = RssFeedParser.Parse(Fixture("rss_rbi_press_releases.xml"), Ist);

        Assert.Equal(2, items.Count);
        Assert.Equal("RBI Bulletin – September 2026", items[0].Title);
        Assert.Equal(new DateTime(2026, 9, 25, 16, 20, 0, DateTimeKind.Utc), items[0].PublishedUtc);

        // Its description is an HTML table: stored as text.
        Assert.DoesNotContain("<", items[0].Summary);
        Assert.StartsWith("Today, the Reserve Bank released the September 2026 issue", items[0].Summary);
    }

    [Fact]
    public void A_SEBI_date_with_a_comma_after_the_month_is_read()
    {
        var items = RssFeedParser.Parse(Fixture("rss_sebi.xml"), Ist);

        Assert.Equal(2, items.Count);
        // "24 Sep, 2026 +0530": midnight IST, the evening before in UTC.
        Assert.Equal(new DateTime(2026, 9, 23, 18, 30, 0, DateTimeKind.Utc), items[1].PublishedUtc);
        Assert.Equal("Key decisions taken in the SEBI Board Meeting dated 24th September, 2026", items[1].Title);
    }

    [Theory]
    [InlineData("Sun, 27 Sep 2026 11:22:32 GMT", "2026-09-27T11:22:32Z")]
    [InlineData("Thu, 24 Sep 2026 14:00:00 +0200", "2026-09-24T12:00:00Z")]   // ECB
    [InlineData("Sat, 26 Sep 2026 18:00:00 -0500", "2026-09-26T23:00:00Z")]   // OilPrice.com
    [InlineData("Mon, 28 Sep 2026 10:00:00 EDT", "2026-09-28T14:00:00Z")]     // a named zone .NET does not read
    [InlineData("Mon, 27 Sep 2026 17:08:51 +0530", "2026-09-27T11:38:51Z")]   // 27 Sep 2026 was a Sunday; the date wins
    [InlineData("2026-09-27T10:00:00Z", "2026-09-27T10:00:00Z")]              // Atom
    public void Feed_dates_in_every_shape_seen_become_UTC(string text, string expected)
    {
        var parsed = RssFeedParser.ParseDate(text, TimeSpan.Zero);
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("yesterday")]
    public void An_unreadable_date_is_null_not_an_error(string? text) =>
        Assert.Null(RssFeedParser.ParseDate(text, Ist));

    [Fact]
    public void An_Atom_feed_reads_the_alternate_link_and_the_updated_time()
    {
        // Hand-written: none of the desk's feeds is Atom today, but a publisher can switch.
        const string atom = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Example</title>
              <entry>
                <title type="html">Rates &amp;amp; the rupee</title>
                <link rel="alternate" href="https://example.com/rates?utm_source=feed"/>
                <link rel="enclosure" href="https://example.com/rates.mp3"/>
                <updated>2026-09-27T03:15:00+05:30</updated>
                <summary>&lt;p&gt;Short&lt;/p&gt;</summary>
              </entry>
            </feed>
            """;

        var item = Assert.Single(RssFeedParser.Parse(atom, Ist));
        Assert.Equal("Rates & the rupee", item.Title);
        Assert.Equal("https://example.com/rates?utm_source=feed", item.Link);
        Assert.Equal("Short", item.Summary);
        Assert.Equal(new DateTime(2026, 9, 26, 21, 45, 0, DateTimeKind.Utc), item.PublishedUtc);
    }

    [Fact]
    public void A_feed_with_a_doctype_is_read_and_its_entities_are_never_resolved()
    {
        const string hostile = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <rss version="2.0"><channel><item><title>Plain</title><link>https://example.com/a</link></item></channel></rss>
            """;

        var item = Assert.Single(RssFeedParser.Parse(hostile, Ist));
        Assert.Equal("Plain", item.Title);
    }

    [Fact]
    public void Click_tracking_on_a_link_does_not_make_a_second_headline()
    {
        var bbc = RssFeedParser.Parse(Fixture("rss_bbc_business.xml"), TimeSpan.Zero);
        string link = bbc[0].Link;
        Assert.Contains("at_medium=RSS", link);

        Assert.Equal("bbc.co.uk/news/articles/cvrl6y8rx08wo", RssFeedParser.NormaliseLink(link));
        Assert.Equal(
            RssFeedParser.LinkHash("BBC Business", "any", link),
            RssFeedParser.LinkHash("BBC World", "other", "http://www.bbc.co.uk/news/articles/cvrl6y8rx08wo/#top"));

        // The part of a query that names the article is kept.
        Assert.Equal("rbi.org.in/scripts/BS_PressReleaseDisplay.aspx?prid=63674",
            RssFeedParser.NormaliseLink("https://www.rbi.org.in/scripts/BS_PressReleaseDisplay.aspx?prid=63674&utm_campaign=x"));
        Assert.NotEqual(
            RssFeedParser.LinkHash("RBI", "a", "https://www.rbi.org.in/scripts/BS_PressReleaseDisplay.aspx?prid=63674"),
            RssFeedParser.LinkHash("RBI", "a", "https://www.rbi.org.in/scripts/BS_PressReleaseDisplay.aspx?prid=63675"));
    }

    [Fact]
    public void An_item_without_a_link_is_keyed_by_source_and_title()
    {
        string hash = RssFeedParser.LinkHash("SEBI", "Circular on F&O", "");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, RssFeedParser.LinkHash("SEBI", "Circular on F&O", "  "));
        Assert.NotEqual(hash, RssFeedParser.LinkHash("RBI", "Circular on F&O", ""));
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    // ---------- NSE filings ----------

    [Fact]
    public void Announcements_carry_the_broadcast_time_in_UTC_and_NSE_s_own_id_as_key()
    {
        var seen = new DateTime(2026, 9, 27, 13, 20, 0, DateTimeKind.Utc);
        var rows = NseCorporateParsers.ParseAnnouncements(Fixture("nse_announcements_latest.json"), seen);

        Assert.Equal(4, rows.Count);
        var federal = rows[0];
        Assert.Equal("FEDERALBNK", federal.Symbol);
        Assert.Equal("NSE", federal.Exchange);
        Assert.Equal("The Federal Bank  Limited", federal.Company);
        Assert.Equal("General Updates", federal.Subject);
        Assert.StartsWith("The Federal Bank  Limited has informed the Exchange about Allotment", federal.Details);
        Assert.EndsWith(".pdf", federal.AttachmentUrl);
        // exchdisstime "27-Sep-2026 18:47:28" IST
        Assert.Equal(new DateTime(2026, 9, 27, 13, 17, 28, DateTimeKind.Utc), federal.AnnouncedUtc);
        Assert.Equal(seen, federal.FirstSeenUtc);
        Assert.Equal(RssFeedParser.Sha256Hex("NSE|seq|106796300"), federal.UniqueKey);

        // Three filings of one company in five minutes are three rows.
        Assert.Equal(3, rows.Where(r => r.Symbol == "MCL").Select(r => r.UniqueKey).Distinct().Count());
        Assert.All(rows, r => Assert.Null(r.ScoredUtc));
    }

    [Fact]
    public void The_event_calendar_keys_a_meeting_by_symbol_date_and_purpose()
    {
        var rows = NseCorporateParsers.ParseEventCalendar(Fixture("nse_event_calendar.json"), DateTime.UtcNow);

        Assert.Equal(5, rows.Count);
        Assert.Equal(new DateOnly(2026, 9, 8), rows[0].EventDate);
        Assert.Equal("Fund Raising", rows[0].Purpose);
        Assert.Equal("Knowledge Marine & Engineering Works Limited", rows[0].Company);

        // NSE lists KMEW's meeting twice; it is one meeting.
        Assert.Equal(4, rows.Select(r => r.UniqueKey).Distinct().Count());
    }

    [Fact]
    public void A_non_list_answer_is_refused()
    {
        Assert.Throws<FormatException>(() => NseCorporateParsers.ParseAnnouncements("""{"error":"blocked"}""", DateTime.UtcNow));
        Assert.Throws<FormatException>(() => NseCorporateParsers.ParseEventCalendar("""{"error":"blocked"}""", DateTime.UtcNow));
    }

    // ---------- participant OI archive ----------

    [Theory]
    [InlineData("participant_oi_02012020.csv", 2020, 1, 2, 64503L)]   // "as on Jan 02 2020", a quoted header cell with a tab
    [InlineData("participant_oi_03012023.csv", 2023, 1, 3, 58593L)]   // "as on Jan 03,\"2023\"\"\"\""
    public void Older_participant_files_read_despite_their_punctuation(string file, int year, int month, int day, long fiiIndexLong)
    {
        var date = new DateOnly(year, month, day);
        var rows = MarketFactorParsers.ParseParticipantOpenInterest(Fixture(file), date, "test");

        Assert.Equal(5, rows.Count);
        Assert.Equal(fiiIndexLong, rows.Single(r => r.ClientType == "FII").FutureIndexLong);
        Assert.Throws<FormatException>(() => MarketFactorParsers.ParseParticipantOpenInterest(Fixture(file), date.AddDays(1), "test"));
    }

    [Fact]
    public void A_participant_title_with_no_year_takes_it_from_the_file_name()
    {
        // 8 Jun 2021's title reads "as on Jun 08": the history backfill
        // reported "participant OI title does not name its date" and the day
        // stayed empty.
        var day = new DateOnly(2021, 6, 8);
        var rows = MarketFactorParsers.ParseParticipantOpenInterest(
            Fixture("participant_oi_08062021.csv"), day, MarketFactorsSync.ParticipantUrl(day));

        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal(day, r.Date));
        Assert.Equal(100133L, rows.Single(r => r.ClientType == "FII").FutureIndexLong);
        Assert.Equal(7803705L, rows.Single(r => r.ClientType == "TOTAL").TotalLong);
    }

    [Fact]
    public void A_participant_title_with_no_year_is_read_only_when_its_day_is_the_file_names()
    {
        string csv = Fixture("participant_oi_08062021.csv");
        var day = new DateOnly(2021, 6, 8);
        var next = day.AddDays(1);
        IReadOnlyList<Domain.Entities.MarketParticipantOpenInterest> Read(string text, DateOnly asked, string source)
            => MarketFactorParsers.ParseParticipantOpenInterest(text, asked, source);

        // The day's file served under the next day's name: the title's day disagrees.
        Assert.Throws<FormatException>(() => Read(csv, next, MarketFactorsSync.ParticipantUrl(next)));
        // Nothing to take the year from.
        Assert.Throws<FormatException>(() => Read(csv, day, "test"));
        // A file named for one day, asked for as another.
        Assert.Throws<FormatException>(() => Read(csv, next, MarketFactorsSync.ParticipantUrl(day)));
        // No date in the title at all: the name alone is not proof of the day.
        Assert.Throws<FormatException>(() => Read(csv.Replace("as on Jun 08", "as on"), day, MarketFactorsSync.ParticipantUrl(day)));
    }

    // ---------- breadth ----------

    [Fact]
    public void A_UDiFF_bhavcopy_keeps_the_EQ_series_only()
    {
        var bhav = NseBreadthParsers.ParseCmBhavcopy(Fixture("cm_bhavcopy_udiff_20260925_sample.csv"), minimumEquities: 5);

        Assert.Equal(new DateOnly(2026, 9, 25), bhav.Date);
        Assert.Equal(NseBreadthParsers.UdiffFormat, bhav.Format);
        Assert.Equal(10, bhav.Equities.Count);   // the gold bond (GB) and trade-for-trade (BE) lines are left out
        var hdfc = bhav.Equities.Single(r => r.Symbol == "HDFCBANK");
        Assert.Equal(735.60m, hdfc.Close);
        Assert.Equal(728.90m, hdfc.PreviousClose);
    }

    [Fact]
    public void A_legacy_bhavcopy_reads_the_same_way()
    {
        var bhav = NseBreadthParsers.ParseCmBhavcopy(Fixture("cm_bhavcopy_legacy_20240705_sample.csv"), minimumEquities: 5);

        Assert.Equal(new DateOnly(2024, 7, 5), bhav.Date);
        Assert.Equal(NseBreadthParsers.LegacyFormat, bhav.Format);
        Assert.Equal(6, bhav.Equities.Count);

        var breadth = NseBreadthParsers.Compute(bhav, week52: null);
        Assert.Equal(3, breadth.Advances);
        Assert.Equal(3, breadth.Declines);
        Assert.Equal(0, breadth.Unchanged);
        Assert.Equal(13256.27m, breadth.TurnoverCr);
        Assert.Null(breadth.Highs52w);
        Assert.Null(breadth.Lows52w);
    }

    [Fact]
    public void A_legacy_bhavcopy_with_a_two_digit_year_reads_its_date()
    {
        // The file for 13 Jul 2020 stamps its rows "13-Jul-20".
        string csv = Fixture("cm_bhavcopy_legacy_20240705_sample.csv").Replace("05-JUL-2024", "13-Jul-20");

        var bhav = NseBreadthParsers.ParseCmBhavcopy(csv, minimumEquities: 5);

        Assert.Equal(new DateOnly(2020, 7, 13), bhav.Date);
    }

    [Fact]
    public void A_real_session_file_is_not_mistaken_for_a_stub()
    {
        // The default minimum: a 10-line file is not a session.
        Assert.Throws<FormatException>(() => NseBreadthParsers.ParseCmBhavcopy(Fixture("cm_bhavcopy_udiff_20260925_sample.csv")));
        Assert.Throws<FormatException>(() => NseBreadthParsers.ParseCmBhavcopy("SYMBOL,SERIES\n"));
    }

    [Fact]
    public void Breadth_counts_advances_declines_and_new_52_week_extremes_against_NSE_s_adjusted_file()
    {
        var day = new DateOnly(2026, 9, 25);
        var bhav = NseBreadthParsers.ParseCmBhavcopy(Fixture("cm_bhavcopy_udiff_20260925_sample.csv"), minimumEquities: 5);
        var week52 = NseBreadthParsers.Parse52Week(Fixture("cm_52wk_high_low_25092026_sample.csv"), day, minimumEquities: 5);

        // "-" (no trade in the window) reads as unknown, not zero.
        Assert.Equal((null, null), week52["1STCUS$"]);
        Assert.Equal(957.30m, week52["AARTIPHARM"].High);

        var breadth = NseBreadthParsers.Compute(bhav, week52);
        Assert.Equal(day, breadth.Date);
        Assert.Equal(6, breadth.Advances);
        Assert.Equal(3, breadth.Declines);
        Assert.Equal(1, breadth.Unchanged);   // AFIL closed at its previous close
        Assert.Equal(10, breadth.Traded);
        Assert.Equal(5633.61m, breadth.TurnoverCr);
        Assert.Equal(2, breadth.Highs52w);    // AARTIPHARM 962 > 957.30, AETHER 1798 > 1717
        Assert.Equal(1, breadth.Lows52w);     // ABFRL 47.88 < 47.94
        Assert.Equal(NseBreadthParsers.UdiffFormat, breadth.Source);

        // The file is for one session; another day's is refused.
        Assert.Throws<FormatException>(() => NseBreadthParsers.Parse52Week(Fixture("cm_52wk_high_low_25092026_sample.csv"), day.AddDays(1), 5));
    }

    // ---------- Yahoo daily bars ----------

    [Fact]
    public void Daily_bars_are_dated_in_the_market_s_own_zone()
    {
        var bars = YahooDailyBars.Parse(Fixture("yahoo_chart_gspc_sample.json"));

        Assert.Equal(new[] { new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 21) }, bars.Select(b => b.Date));
        Assert.Equal(7764.700195m, bars[^1].Close);
        Assert.Equal(4828050000m, bars[^1].Volume);
    }

    [Fact]
    public void Futures_stamped_at_New_York_midnight_keep_their_date_across_daylight_saving()
    {
        // 05:00 UTC in January and 04:00 UTC in July are both midnight in New York.
        var bars = YahooDailyBars.Parse(Fixture("yahoo_chart_esf_sample.json"));

        Assert.Equal(new[] { new DateOnly(2025, 1, 15), new DateOnly(2025, 7, 15) }, bars.Select(b => b.Date));
    }

    [Fact]
    public void A_padded_day_without_a_close_is_skipped_and_a_zero_volume_is_none()
    {
        var bars = YahooDailyBars.Parse(Fixture("yahoo_chart_vix_sample.json"));

        Assert.Equal(new[] { new DateOnly(2026, 7, 2), new DateOnly(2026, 7, 6) }, bars.Select(b => b.Date));
        Assert.All(bars, b => Assert.Null(b.Volume));
    }

    [Fact]
    public void A_chart_answer_with_an_error_is_refused()
    {
        var ex = Assert.Throws<FormatException>(() => YahooDailyBars.Parse("""{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found"}}}"""));
        Assert.Contains("Not Found", ex.Message);
    }

    [Fact]
    public void Every_global_key_is_unique_and_named_as_the_contract_says()
    {
        string[] contract = ["SPX", "NDX", "DJI", "VIX", "N225", "HSI", "KS11", "STOXX50E", "FTSE", "BRENT", "WTI", "GOLD", "DXY", "US10Y", "USDINR", "ES"];
        Assert.Equal(contract, GlobalMarketKeys.All.Select(m => m.Key));
        Assert.Equal(GlobalMarketKeys.All.Count, GlobalMarketKeys.All.Select(m => m.YahooSymbol).Distinct().Count());
        Assert.All(GlobalMarketKeys.All, m => Assert.True(m.Key.Length <= 40));
    }
}
