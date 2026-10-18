using System.IO.Compression;
using System.Net;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using Xunit;
using static AlgoTrading.UnitTests.MarketIntelligenceTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The circulars recorder: SEBI's listing and circular pages, NSE's circulars API and BSE's notices feed, read from
/// the pages saved on 10 Oct 2026 (Fixtures/MarketIntelligence); only what might bind an F&amp;O desk is kept, each
/// once, with the text of its document. No network: the documents are PDFs made here.
/// </summary>
public class RegulatoryCircularsTests
{
    private static readonly DateTime Now = Ist(2026, 10, 10, 20, 35);

    // ---------- parsing the saved pages ----------

    [Fact]
    public void SEBI_s_listing_gives_each_circular_its_date_title_page_and_key()
    {
        var list = RegulatoryCircularParsers.ParseSebiListing(Fixture("sebi-circulars.html"));

        Assert.Equal(4, list.Count);
        var first = list[0];
        Assert.Equal(("sebi:105081", new DateOnly(2026, 10, 7)), (first.Key, first.PublishedOn));
        Assert.StartsWith("Introduction of Credit Risk-o-Meter", first.Title);
        Assert.StartsWith("https://www.sebi.gov.in/legal/circulars/oct-2026/", first.Url);
        Assert.All(list, c => Assert.Equal(RegulatoryCircularSource.Sebi, c.Source));
    }

    [Fact]
    public void A_SEBI_circular_s_page_gives_its_number_and_the_PDF_its_frame_shows()
    {
        var (number, pdf) = RegulatoryCircularParsers.ParseSebiPage(Fixture("sebi-circular-page.html"));

        Assert.Equal("HO/38/24/(15)2026-MIRSD-PODMMC/I/22872/2026", number);
        Assert.Equal("https://www.sebi.gov.in/sebi_data/attachdocs/oct-2026/1790852162769.pdf", pdf);
    }

    [Fact]
    public void NSE_s_circulars_are_kept_by_department_or_subject_and_listings_and_fund_notices_are_not()
    {
        var list = RegulatoryCircularParsers.ParseNseCirculars(Fixture("nse-circulars.json"));
        var keys = list.Select(c => c.Key).ToHashSet();

        // F&O by department; the algo-ID risk controls and an algo provider's empanelment by subject.
        Assert.Contains("nse:76827", keys);
        Assert.Contains("nse:76748", keys);
        Assert.Contains("nse:76841", keys);
        Assert.Contains("nse:76786", keys);
        Assert.Contains("nse:76784", keys);    // inspection and compliance
        // A listing, a mutual fund notice, surveillance orders about one stock: not this desk's business.
        Assert.DoesNotContain("nse:76842", keys);
        Assert.DoesNotContain("nse:76825", keys);
        Assert.DoesNotContain("nse:76847", keys);
        Assert.DoesNotContain("nse:76848", keys);

        var mock = list.Single(c => c.Key == "nse:76827");
        Assert.Equal(("NSE/FAOP/76827", new DateOnly(2026, 10, 9), "department: Futures & Options"), (mock.Number, mock.PublishedOn, mock.Relevance));
        Assert.Equal("https://nsearchives.nseindia.com/content/circulars/FAOP76827.pdf", mock.DocumentUrl);
        Assert.Equal("subject: algo", list.Single(c => c.Key == "nse:76841").Relevance);
    }

    [Fact]
    public void BSE_s_notices_are_kept_when_their_title_names_derivatives_algos_or_risk()
    {
        // The feed as served: one notice, about a mock trading session, which is not kept.
        Assert.Empty(RegulatoryCircularParsers.ParseBseNotices(Fixture("bse-notices.xml")));

        // Written by hand in the feed's shape: a derivatives notice is kept, keyed on BSE's notice number.
        const string feed = """
            <?xml version="1.0" encoding="utf-8"?><rss version="2.0"><channel><title>BSE Notices</title>
            <item><title>Revision in lot size of SENSEX derivatives contracts</title><link>https://www.bseindia.com/downloads/UploadDocs/Notices/20261009-12/20261009-12.pdf</link><pubDate>Fri, 09 Oct 2026 12:00:00 GMT</pubDate></item>
            <item><title>Listing of new securities of ABC Ltd</title><link>https://www.bseindia.com/downloads/UploadDocs/Notices/20261009-13/20261009-13.pdf</link><pubDate>Fri, 09 Oct 2026 12:00:00 GMT</pubDate></item>
            </channel></rss>
            """;
        var kept = Assert.Single(RegulatoryCircularParsers.ParseBseNotices(feed));
        Assert.Equal(("bse:20261009-12", new DateOnly(2026, 10, 9)), (kept.Key, kept.PublishedOn));
    }

    [Fact]
    public void A_document_s_text_is_read_from_a_PDF_or_the_first_PDF_in_a_zip_and_garbage_reads_as_nothing()
    {
        byte[] pdf = Pdf("All orders from algorithms must carry a unique Algo ID issued by the exchange.");

        Assert.Contains("unique Algo ID issued by the exchange", RegulatoryCircularParsers.DocumentText(pdf));
        Assert.Contains("unique Algo ID", RegulatoryCircularParsers.DocumentText(ZipOf("FAOP76748.pdf", pdf)));
        Assert.Equal(string.Empty, RegulatoryCircularParsers.DocumentText("not a pdf"u8.ToArray()));
        Assert.Equal(40, RegulatoryCircularParsers.DocumentText(pdf, maxChars: 40).Length);
    }

    // ---------- recording ----------

    [Fact]
    public async Task A_run_stores_each_relevant_circular_once_with_its_text_and_the_next_run_fetches_no_document_again()
    {
        var db = Db();
        var web = Web();
        var recorder = Recorder(db, web);

        var first = await recorder.RecordAsync(CancellationToken.None);

        var stored = await db.RegulatoryCirculars.AsNoTracking().ToListAsync();
        Assert.Equal(stored.Count, first.Stored);
        Assert.Equal(4 + 6, stored.Count);   // four SEBI circulars, six NSE ones, no BSE notice
        var sebi = stored.Single(c => c.Key == "sebi:104858");
        Assert.Equal("HO/38/24/(15)2026-MIRSD-PODMMC/I/22872/2026", sebi.Number);
        Assert.Contains("investor awareness message", sebi.Excerpt);
        Assert.Contains("must carry a unique Algo ID", stored.Single(c => c.Key == "nse:76748").Excerpt);
        Assert.Contains("Internal audit", stored.Single(c => c.Key == "nse:76784").Excerpt);   // a zip with a PDF in it
        Assert.All(stored, c => Assert.Equal(Now, c.FirstSeenUtc));

        int requests = web.Requests.Count;
        var second = await Recorder(db, web, Now.AddDays(1)).RecordAsync(CancellationToken.None);

        Assert.Equal(0, second.Stored);
        // Only the three listings were read again: no circular page, no document.
        Assert.Equal(3 + 1, web.Requests.Count - requests);   // SEBI, NSE's API (its page primed again a day later), BSE
        Assert.Equal(4 + 6, await db.RegulatoryCirculars.CountAsync());
    }

    [Fact]
    public async Task A_text_the_old_cap_cut_short_is_read_again_whole_once()
    {
        // 17 Oct 2026: SEBI's circular on market timings was kept as its first 4,000 characters; its dates are on page 6.
        const string document = "https://www.sebi.gov.in/sebi_data/attachdocs/oct-2026/1792079571420.pdf";
        var db = Db();
        db.RegulatoryCirculars.AddRange(
            new RegulatoryCircular
            {
                Source = RegulatoryCircularSource.Sebi, Key = "sebi:105210", Title = "Review of certain aspects of the Closing Auction Session",
                Url = "https://www.sebi.gov.in/legal/circulars/oct-2026/review_105210.html", DocumentUrl = document,
                Excerpt = new string('x', RegulatoryCircularsRecorder.LegacyTextChars), FirstSeenUtc = Now.AddDays(-5),
            },
            // Shorter than the old cap: it was read whole the first time.
            new RegulatoryCircular
            {
                Source = RegulatoryCircularSource.Nse, Key = "nse:1", Title = "Short", DocumentUrl = "https://nsearchives.nseindia.com/content/circulars/FAOP1.pdf",
                Excerpt = "All of it.", FirstSeenUtc = Now.AddDays(-5),
            });
        await db.SaveChangesAsync();
        var web = Web().Serve(document, Pages(
            string.Join(' ', Enumerable.Repeat("4.2.4. Trading in index derivatives contracts shall continue until 3:45 p.m. on all trading days.", 40)),
            string.Join(' ', Enumerable.Repeat("4.2.6. The post close session in the cash segment shall operate between 3:50 p.m. to 4:00 p.m.", 20)),
            "6.1. The modifications specified in this circular, other than those specified at para 4.8, shall come into effect from December 07, 2026."));

        var first = await Recorder(db, web).RecordAsync(CancellationToken.None);

        var sebi = await db.RegulatoryCirculars.AsNoTracking().SingleAsync(c => c.Key == "sebi:105210");
        Assert.Equal(1, first.Reread);
        Assert.Equal(Now, sebi.TextRereadUtc);
        Assert.True(sebi.Excerpt.Length > RegulatoryCircularsRecorder.LegacyTextChars);
        Assert.Contains("shall come into effect from December 07, 2026", sebi.Excerpt);
        Assert.Null((await db.RegulatoryCirculars.AsNoTracking().SingleAsync(c => c.Key == "nse:1")).TextRereadUtc);

        var second = await Recorder(db, web, Now.AddDays(1)).RecordAsync(CancellationToken.None);

        Assert.Equal(0, second.Reread);
        Assert.Single(web.Requests, u => u == document);
    }

    [Fact]
    public async Task A_source_that_fails_costs_only_its_own_circulars()
    {
        var db = Db();
        var web = Web(sebiDown: true);

        var report = await Recorder(db, web).RecordAsync(CancellationToken.None);

        Assert.Contains(report.Errors, e => e.StartsWith("SEBI: HTTP 503", StringComparison.Ordinal));
        Assert.Equal(6, await db.RegulatoryCirculars.CountAsync());
    }

    [Theory]
    [InlineData(2026, 10, 9, 20, 25, "2026-10-08T15:00:00Z", false)]   // before 20:30, yesterday's run was in time
    [InlineData(2026, 10, 9, 20, 31, "2026-10-08T15:00:00Z", true)]
    [InlineData(2026, 10, 9, 20, 40, "2026-10-09T15:01:00Z", false)]   // today's run is done
    [InlineData(2026, 10, 9, 11, 0, "2026-10-07T15:00:00Z", false)]    // a missed day, but inside Friday's session
    [InlineData(2026, 10, 9, 16, 0, "2026-10-07T15:00:00Z", true)]     // caught up after the close
    [InlineData(2026, 10, 10, 11, 0, "2026-10-08T15:00:00Z", true)]    // Saturday: no session to keep out of
    public void The_circulars_run_is_once_a_day_from_20_30_and_a_missed_one_is_caught_up_outside_the_session(int y, int m, int d, int hour, int minute,
        string lastRun, bool due)
    {
        var last = DateTime.Parse(lastRun, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        bool Trading(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

        Assert.Equal(due, MarketIntelligenceSchedule.CircularsDue(Ist(y, m, d, hour, minute), last, Trading));
        Assert.True(MarketIntelligenceSchedule.CircularsDue(Ist(2026, 10, 10, 11, 0), null, Trading));
    }

    // ---------- helpers ----------

    private static RegulatoryCircularsRecorder Recorder(Infrastructure.Persistence.TradingDbContext db, FakeWeb web, DateTime? now = null)
    {
        var at = now ?? Now;
        var nse = new NseWebClient(new Factory(web), new NseRequestPacer(TimeSpan.Zero), NullLogger<NseWebClient>.Instance) { Clock = () => at };
        return new RegulatoryCircularsRecorder(db, new Factory(web), nse, new MarketIntelligenceStatus(), NullLogger<RegulatoryCircularsRecorder>.Instance)
        {
            Clock = () => at,
            DocumentGap = TimeSpan.Zero,
        };
    }

    /// <summary>The three sources as saved, every SEBI circular page as the saved one, and a PDF behind every document link.</summary>
    private static FakeWeb Web(bool sebiDown = false)
    {
        var web = new FakeWeb()
            .Serve(RegulatoryCircularParsers.NsePage, "<html></html>")
            .Serve(RegulatoryCircularParsers.BseNoticesUrl, Fixture("bse-notices.xml"))
            .Serve("https://www.sebi.gov.in/sebi_data/attachdocs/oct-2026/1790852162769.pdf",
                Pdf("Display of investor awareness message(s) by stock brokers on their trading apps and websites."))
            .Serve("https://nsearchives.nseindia.com/content/circulars/FAOP76748.pdf", Pdf("Every order from an algorithm must carry a unique Algo ID."))
            // As NSE files it: the manuals first, the circular last.
            .Serve("https://nsearchives.nseindia.com/content/circulars/INSP76784.zip", ZipOf(
                ("Annexure I_Manual for Auditor.pdf", Pdf("Manual for the auditor: register on the portal.")),
                ("INSP76784.pdf", Pdf("Internal audit of trading members for the half year."))))
            .ServeWhen(u => u.StartsWith("https://www.nseindia.com/api/circulars?", StringComparison.Ordinal), Fixture("nse-circulars.json"))
            .ServeWhen(u => u.StartsWith("https://www.sebi.gov.in/legal/circulars/", StringComparison.Ordinal), Fixture("sebi-circular-page.html"));
        web.Serve(RegulatoryCircularParsers.SebiListingUrl, sebiDown ? "busy" : Fixture("sebi-circulars.html"), sebiDown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        foreach (var other in new[] { "CD76841", "FAOP76827", "INVG76786", "CMPT76736" })
        {
            web.Serve($"https://nsearchives.nseindia.com/content/circulars/{other}.pdf", Pdf($"Circular {other} must carry a unique Algo ID text."));
        }

        return web;
    }

    /// <summary>A one-page PDF holding <paramref name="text"/>, made with PdfPig's writer.</summary>
    internal static byte[] Pdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page.AddText(text, 10, new PdfPoint(25, 700), font);
        return builder.Build();
    }

    /// <summary>A PDF of one page per text, each written in lines of about 90 characters down the page.</summary>
    internal static byte[] Pages(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        foreach (string text in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            var lines = new List<string>();
            var line = new System.Text.StringBuilder();
            foreach (string word in text.Split(' '))
            {
                if (line.Length + word.Length > 90)
                {
                    lines.Add(line.ToString().TrimEnd());
                    line.Clear();
                }

                line.Append(word).Append(' ');
            }

            lines.Add(line.ToString().TrimEnd());
            for (int i = 0; i < lines.Count; i++) page.AddText(lines[i], 8, new PdfPoint(25, 810 - 10 * i), font);
        }

        return builder.Build();
    }

    private static byte[] ZipOf(string name, byte[] content) => ZipOf((name, content));

    private static byte[] ZipOf(params (string Name, byte[] Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return buffer.ToArray();
    }
}
