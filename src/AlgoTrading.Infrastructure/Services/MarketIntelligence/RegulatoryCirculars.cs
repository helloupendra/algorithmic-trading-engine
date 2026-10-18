using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>One circular as a source lists it, before its text is read.</summary>
public sealed record CircularListing(
    string Source,
    string Key,
    string Number,
    string Title,
    string Department,
    DateOnly? PublishedOn,
    string Url,
    string DocumentUrl,
    string Relevance);

/// <summary>What one circulars run did: listed, stored new, read with text, and texts read again whole (<see cref="RegulatoryCircular.TextRereadUtc"/>).</summary>
public sealed record CircularsRecordReport(int Listed, int Stored, int TextRead, IReadOnlyList<string> Errors, int Reread = 0);

/// <summary>
/// Reads the three sources' circular listings: SEBI's circulars page, NSE's circulars API and BSE's notices feed.
/// Pure, so every rule is a test on a saved page.
/// </summary>
/// <remarks>
/// <para>
/// Only the circulars that might concern an F&amp;O desk are listed. SEBI's circulars all are: SEBI publishes a few a
/// week, and its orders, recovery notices and press releases are on other pages. NSE publishes about 170 a week,
/// most of them listings, mutual fund and debt notices; a circular is kept when its department is F&amp;O,
/// inspection and compliance, or legal, or its subject names algos, derivatives, options, expiries, lot sizes,
/// margins, position limits, risk controls or trading hours. BSE's notices the same way, by title.
/// </para>
/// </remarks>
public static partial class RegulatoryCircularParsers
{
    public const string SebiListingUrl = "https://www.sebi.gov.in/sebiweb/home/HomeAction.do?doListing=yes&sid=1&ssid=7&smid=0";
    public const string NsePage = "https://www.nseindia.com/resources/exchange-communication-circulars";
    public const string BseNoticesUrl = "https://www.bseindia.com/data/xml/notices.xml";

    /// <summary>NSE's circulars API for the days from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static string NseCircularsUrl(DateOnly from, DateOnly to) =>
        $"https://www.nseindia.com/api/circulars?fromDate={from:dd-MM-yyyy}&toDate={to:dd-MM-yyyy}";

    /// <summary>NSE departments whose circulars are always kept.</summary>
    private static readonly string[] NseDepartments = ["Futures & Options", "Inspection & Compliance", "Legal, ISC & Arbitration"];

    /// <summary>NSE departments never kept, whatever the subject: they are about listed issuers and other products.</summary>
    private static readonly string[] NseSkipped = ["Listing", "Mutual Fund", "Debt Segment", "Emerge", "Electronic Gold Receipt", "Securities Lending & Borrowing Scheme"];

    /// <summary>Words in a subject that make a circular worth reading for an F&amp;O desk.</summary>
    private static readonly (string Word, string Label)[] Keywords =
    [
        ("algo", "algo"), ("algorithmic", "algo"), (" api", "API"), ("retail investor", "retail investors"),
        ("derivative", "derivatives"), ("f&o", "F&O"), ("futures & options", "F&O"), ("futures and options", "F&O"),
        (" option", "options"), (" expir", "expiry"), ("lot size", "lot size"), (" margin", "margins"),
        ("position limit", "position limits"), ("pre-trade", "risk controls"), ("risk control", "risk controls"),
        ("risk management", "risk controls"), ("trading hours", "market hours"), ("market timing", "market hours"),
        (" nifty", "index"), (" sensex", "index"), (" bankex", "index"),
    ];

    /// <summary>The words of <paramref name="subject"/> that make it worth keeping, as one label; null when none do.</summary>
    public static string? KeywordIn(string subject)
    {
        string s = " " + subject.ToLowerInvariant() + " ";
        var hit = Keywords.FirstOrDefault(k => s.Contains(k.Word, StringComparison.Ordinal));
        return hit.Word is null ? null : hit.Label;
    }

    /// <summary>Why an NSE circular is kept, or null when it is not.</summary>
    public static string? NseRelevance(string department, string subject)
    {
        if (NseSkipped.Any(d => string.Equals(d, department, StringComparison.OrdinalIgnoreCase))) return null;
        if (NseDepartments.Any(d => string.Equals(d, department, StringComparison.OrdinalIgnoreCase))) return $"department: {department}";
        return KeywordIn(subject) is string word ? $"subject: {word}" : null;
    }

    /// <summary>SEBI's circulars page: each row's date, title and page. The page's own number (<c>_105081</c>) is the key.</summary>
    public static List<CircularListing> ParseSebiListing(string html)
    {
        var list = new List<CircularListing>();
        foreach (Match m in SebiRow().Matches(html))
        {
            string url = WebUtility.HtmlDecode(m.Groups["url"].Value.Trim());
            string title = Clean(WebUtility.HtmlDecode(m.Groups["title"].Value));
            if (title.Length == 0 || !url.StartsWith("https://www.sebi.gov.in/", StringComparison.Ordinal)) continue;
            DateOnly? date = DateOnly.TryParseExact(m.Groups["date"].Value.Trim(), "MMM dd, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;
            var id = SebiId().Match(url);
            string key = id.Success ? $"{RegulatoryCircularSource.Sebi}:{id.Groups[1].Value}" : $"{RegulatoryCircularSource.Sebi}:{Hash(url)}";
            list.Add(new CircularListing(RegulatoryCircularSource.Sebi, key, string.Empty, title, string.Empty, date, url, string.Empty, "SEBI circular"));
        }

        return list;
    }

    /// <summary>A SEBI circular's page: its number and the PDF its frame shows; either may be missing.</summary>
    public static (string Number, string? PdfUrl) ParseSebiPage(string html)
    {
        var number = SebiNumber().Match(html);
        var frame = SebiFrame().Match(html);
        string? pdf = frame.Success ? WebUtility.HtmlDecode(frame.Groups["pdf"].Value.Trim()) : null;
        if (pdf is not null && !pdf.StartsWith("https://www.sebi.gov.in/", StringComparison.Ordinal)) pdf = null;
        return (number.Success ? Clean(WebUtility.HtmlDecode(number.Groups["n"].Value)) : string.Empty, pdf);
    }

    /// <summary>NSE's circulars answer: the circulars worth keeping, with their document.</summary>
    public static List<CircularListing> ParseNseCirculars(string json)
    {
        var list = new List<CircularListing>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in data.EnumerateArray())
        {
            string number = Str(row, "circNumber");
            string subject = Clean(Str(row, "sub"));
            string department = Clean(Str(row, "circDepartment"));
            string link = Str(row, "circFilelink");
            if (number.Length == 0 || subject.Length == 0 || !seen.Add(number)) continue;
            if (NseRelevance(department, subject) is not string why) continue;
            DateOnly? date = DateOnly.TryParseExact(Str(row, "cirDate"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
            string document = link.StartsWith("https://", StringComparison.Ordinal) ? link : string.Empty;
            list.Add(new CircularListing(RegulatoryCircularSource.Nse, $"{RegulatoryCircularSource.Nse}:{number}", Str(row, "circDisplayNo"),
                subject, department, date, document, document, why));
        }

        return list;
    }

    /// <summary>BSE's notices feed: the notices worth keeping, each with its PDF.</summary>
    public static List<CircularListing> ParseBseNotices(string xml)
    {
        var list = new List<CircularListing>();
        var doc = XDocument.Parse(xml.TrimStart('﻿'));
        foreach (var item in doc.Descendants("item"))
        {
            string title = Clean(item.Element("title")?.Value ?? string.Empty);
            string link = (item.Element("link")?.Value ?? item.Element("guid")?.Value ?? string.Empty).Trim();
            if (title.Length == 0 || !link.StartsWith("https://", StringComparison.Ordinal)) continue;
            if (KeywordIn(title) is not string word) continue;
            var id = BseId().Match(link);
            DateOnly? date = DateTimeOffset.TryParse(item.Element("pubDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? DateOnly.FromDateTime(at.UtcDateTime + IstTime.Offset)
                : null;
            list.Add(new CircularListing(RegulatoryCircularSource.Bse,
                $"{RegulatoryCircularSource.Bse}:{(id.Success ? id.Groups[1].Value : Hash(link))}",
                id.Success ? id.Groups[1].Value : string.Empty, title, string.Empty, date, link, link, $"subject: {word}"));
        }

        return list;
    }

    /// <summary>The most of a document's text kept: a SEBI circular of seven pages is about 11,000 characters.</summary>
    public const int MaxTextChars = 16_000;

    /// <summary>The pages of a document read at most.</summary>
    public const int MaxTextPages = 12;

    /// <summary>
    /// The text of a document: a PDF's first <paramref name="maxPages"/> pages, or the first PDF in a zip (NSE files
    /// some circulars that way), whitespace made single and cut at <paramref name="maxChars"/>. Empty when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Four pages and 4,000 characters until 18 Oct 2026: SEBI's circular of 15 Oct on market timings gives its dates
    /// on page 6, so the triage never saw when its changes take effect.
    /// </remarks>
    public static string DocumentText(byte[] bytes, int maxChars = MaxTextChars, int maxPages = MaxTextPages)
    {
        try
        {
            if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
            {
                using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                // The circular itself, not its annexures: NSE's INSP76784.zip holds two manuals before INSP76784.pdf.
                var entry = zip.Entries
                    .Where(e => e.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && e.Length < 20_000_000)
                    .OrderBy(e => e.Name.Contains("annex", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .FirstOrDefault();
                if (entry is null) return string.Empty;
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return PdfText(buffer.ToArray(), maxChars, maxPages);
            }

            return PdfText(bytes, maxChars, maxPages);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return string.Empty;
        }
    }

    private static string PdfText(byte[] bytes, int maxChars, int maxPages)
    {
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            var text = new StringBuilder();
            foreach (var page in pdf.GetPages().Take(maxPages))
            {
                text.Append(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)).Append('\n');
                if (text.Length > maxChars * 2) break;
            }

            string clean = Clean(text.ToString());
            return clean.Length <= maxChars ? clean : clean[..maxChars];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A scanned or broken PDF has no text to give; the circular is kept with its title.
            return string.Empty;
        }
    }

    private static string Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;

    /// <summary>Tags out, entities read, whitespace made single.</summary>
    public static string Clean(string text) => Spaces().Replace(Tags().Replace(text, " "), " ").Trim();

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();

    [GeneratedRegex(@"<td>\s*(?<date>[A-Z][a-z]{2} \d{1,2}, \d{4})\s*</td>\s*<td>\s*<a href=""(?<url>[^""]+)""[^>]*?title=""(?<title>[^""]*)""", RegexOptions.Singleline)]
    private static partial Regex SebiRow();

    [GeneratedRegex(@"_(\d+)\.html$")]
    private static partial Regex SebiId();

    [GeneratedRegex(@"Circular No\.:\s*</span>\s*<span>(?<n>[^<]+)</span>", RegexOptions.Singleline)]
    private static partial Regex SebiNumber();

    [GeneratedRegex(@"<iframe[^>]+src='[^']*?file=(?<pdf>https://[^'""\s]+\.pdf)'", RegexOptions.IgnoreCase)]
    private static partial Regex SebiFrame();

    [GeneratedRegex(@"/Notices/([0-9]{8}-[0-9]+)/")]
    private static partial Regex BseId();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>
/// Records the circulars SEBI, NSE and BSE publish that might bind the desk, once a day, with the first pages of each
/// one's text, for the Compliance Triage.
/// </summary>
/// <remarks>
/// <para>
/// Insert-only: a circular already stored (same <see cref="RegulatoryCircular.Key"/>) is left as it is, and its
/// document is not fetched again, but for one thing: a text the old cap cut short (<see cref="LegacyTextChars"/>, until
/// 18 Oct 2026) is read once more, whole, for the circulars of the last <see cref="RereadDays"/> days, at most
/// <see cref="MaxRereadsPerRun"/> a run. One source failing costs only its own circulars for that run.
/// </para>
/// <para>
/// Polite: once a day, documents fetched one at a time <see cref="DocumentGap"/> apart, at most
/// <see cref="MaxDocumentsPerRun"/> a run and <see cref="MaxDocumentBytes"/> each; NSE's API goes through
/// <see cref="NseWebClient"/> and its pacer, like every other request to NSE.
/// </para>
/// </remarks>
public sealed class RegulatoryCircularsRecorder
{
    public const string HttpClientName = "circulars-recorder";

    /// <summary>How far back NSE's API is asked each run: a few missed days are caught up, the duplicates skipped.</summary>
    public const int NseLookbackDays = 7;

    public const int MaxDocumentsPerRun = 40;

    public const long MaxDocumentBytes = 8_000_000;

    /// <summary>The characters a text was cut at until 18 Oct 2026: a stored text exactly this long was cut short.</summary>
    public const int LegacyTextChars = 4000;

    /// <summary>How far back, by when they were recorded, texts cut at the old cap are read again.</summary>
    public const int RereadDays = 30;

    public const int MaxRereadsPerRun = 10;

    private readonly TradingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly NseWebClient _nse;
    private readonly MarketIntelligenceStatus _status;
    private readonly ILogger<RegulatoryCircularsRecorder> _logger;

    public RegulatoryCircularsRecorder(TradingDbContext db, IHttpClientFactory http, NseWebClient nse, MarketIntelligenceStatus status,
        ILogger<RegulatoryCircularsRecorder> logger)
    {
        _db = db;
        _http = http;
        _nse = nse;
        _status = status;
        _logger = logger;
    }

    /// <summary>The clock FirstSeenUtc is read from; replaced in tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The wait between two document downloads; zero in tests.</summary>
    public TimeSpan DocumentGap { get; set; } = TimeSpan.FromSeconds(2);

    public async Task<CircularsRecordReport> RecordAsync(CancellationToken ct)
    {
        var health = _status.Recorder(MarketIntelligenceNames.Circulars);
        health.Attempted(Clock());
        var errors = new List<string>();
        var listed = new List<CircularListing>();

        await ReadAsync("SEBI", async () => listed.AddRange(RegulatoryCircularParsers.ParseSebiListing(await GetTextAsync(RegulatoryCircularParsers.SebiListingUrl, ct))));
        await ReadAsync("NSE", async () =>
        {
            var today = IstTime.DateOf(Clock());
            var (status, body) = await _nse.GetJsonAsync(RegulatoryCircularParsers.NseCircularsUrl(today.AddDays(-NseLookbackDays), today),
                RegulatoryCircularParsers.NsePage, ct);
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"HTTP {(int)status}");
            listed.AddRange(RegulatoryCircularParsers.ParseNseCirculars(body));
        });
        await ReadAsync("BSE", async () => listed.AddRange(RegulatoryCircularParsers.ParseBseNotices(await GetTextAsync(RegulatoryCircularParsers.BseNoticesUrl, ct))));

        var keys = listed.Select(l => l.Key).Distinct().ToList();
        var known = (await _db.RegulatoryCirculars.AsNoTracking().Where(c => keys.Contains(c.Key)).Select(c => c.Key).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        var fresh = listed.Where(l => !known.Contains(l.Key)).GroupBy(l => l.Key).Select(g => g.First())
            .OrderByDescending(l => l.PublishedOn).Take(MaxDocumentsPerRun).ToList();

        int stored = 0, textRead = 0;
        foreach (var listing in fresh)
        {
            string number = listing.Number, document = listing.DocumentUrl, excerpt = string.Empty;
            try
            {
                if (DocumentGap > TimeSpan.Zero) await Task.Delay(DocumentGap, ct);
                if (listing.Source == RegulatoryCircularSource.Sebi)
                {
                    var (n, pdf) = RegulatoryCircularParsers.ParseSebiPage(await GetTextAsync(listing.Url, ct));
                    number = n;
                    document = pdf ?? string.Empty;
                }

                if (document.Length > 0)
                {
                    excerpt = RegulatoryCircularParsers.DocumentText(await GetBytesAsync(document, ct));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Kept with its title: the triage says unclear when the text is too short to tell.
                errors.Add($"{listing.Key}: {Describe(ex)}");
            }

            if (excerpt.Length > 0) textRead++;
            if (await InsertAsync(listing, number, document, excerpt, ct)) stored++;
        }

        int reread = await RereadCutTextsAsync(errors, ct);

        string message = $"{stored} new circular(s), {textRead} with text, from {listed.Count} listed" +
                         (reread > 0 ? $"; {reread} text(s) cut short before read again whole" : string.Empty);
        health.Finished(Clock(), message, listed.Count == 0 && errors.Count > 0 ? "every source failed" : null);
        _logger.LogInformation("Circulars recorder: {Message}.", message);
        return new CircularsRecordReport(listed.Count, stored, textRead, errors, reread);

        async Task ReadAsync(string source, Func<Task> read)
        {
            try
            {
                await read();
                health.LogSourceRecovery(_logger, source);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                health.LogSourceFailure(_logger, source, Describe(ex), Clock());
                errors.Add($"{source}: {Describe(ex)}");
            }
        }
    }

    /// <summary>
    /// The texts the old cap cut short, read again whole: on 17 Oct 2026 SEBI's circular on market timings was triaged
    /// on its first 4,000 characters, and its dates are on page 6. Each is read again once (<see cref="RegulatoryCircular.TextRereadUtc"/>,
    /// set whether the text grew or not); a document that cannot be fetched is tried again on the next run.
    /// </summary>
    private async Task<int> RereadCutTextsAsync(List<string> errors, CancellationToken ct)
    {
        var since = Clock().AddDays(-RereadDays);
        string[] documents = [RegulatoryCircularSource.Sebi, RegulatoryCircularSource.Nse, RegulatoryCircularSource.Bse];
        var cut = await _db.RegulatoryCirculars
            .Where(c => c.TextRereadUtc == null && c.FirstSeenUtc >= since && c.DocumentUrl != string.Empty
                        && c.Excerpt.Length == LegacyTextChars && documents.Contains(c.Source))
            .OrderByDescending(c => c.FirstSeenUtc)
            .Take(MaxRereadsPerRun)
            .ToListAsync(ct);

        int reread = 0;
        foreach (var circular in cut)
        {
            try
            {
                if (DocumentGap > TimeSpan.Zero) await Task.Delay(DocumentGap, ct);
                string text = RegulatoryCircularParsers.DocumentText(await GetBytesAsync(circular.DocumentUrl, ct));
                if (text.Length > circular.Excerpt.Length) circular.Excerpt = text;
                circular.TextRereadUtc = Clock();
                await _db.SaveChangesAsync(ct);
                reread++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"{circular.Key} (read again): {Describe(ex)}");
            }
        }

        _db.ChangeTracker.Clear();
        return reread;
    }

    private async Task<bool> InsertAsync(CircularListing listing, string number, string document, string excerpt, CancellationToken ct)
    {
        _db.RegulatoryCirculars.Add(new RegulatoryCircular
        {
            Source = listing.Source,
            Key = listing.Key,
            Number = Cut(number, 200),
            Title = listing.Title,
            Department = Cut(listing.Department, 200),
            PublishedOn = listing.PublishedOn,
            Url = listing.Url,
            DocumentUrl = document,
            Excerpt = excerpt,
            Relevance = Cut(listing.Relevance, 200),
            FirstSeenUtc = Clock(),
        });
        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException)
        {
            // Stored by something else between the read and the write: the unique key kept the first.
            return false;
        }
        finally
        {
            _db.ChangeTracker.Clear();
        }
    }

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await _http.CreateClient(HttpClientName).GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var response = await _http.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength > MaxDocumentBytes) throw new HttpRequestException("document too large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxDocumentBytes) throw new HttpRequestException("document too large");
        }

        return buffer.ToArray();
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timed out",
        JsonException => "not the JSON expected",
        System.Xml.XmlException => "not a readable feed",
        _ => ex.Message,
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MarketIntelService.UserAgent);
    }
}
