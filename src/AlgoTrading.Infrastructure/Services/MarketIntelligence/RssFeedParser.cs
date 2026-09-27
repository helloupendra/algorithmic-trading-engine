using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>One item of a feed, as read: nothing looked up, nothing stored.</summary>
public sealed record FeedItem(string Title, string Link, string Summary, DateTime? PublishedUtc);

/// <summary>
/// Reads RSS 2.0, RSS 1.0 (RDF) and Atom feeds into <see cref="FeedItem"/>s,
/// and gives each item the key the recorder deduplicates on. Pure functions
/// over text, shared by the console's live news and the recorder.
/// </summary>
/// <remarks>
/// Feeds are written by hand-rolled publishing systems and the parser is
/// lenient where they disagree: elements are matched by local name whatever
/// their namespace, a DOCTYPE is ignored rather than refused (and never
/// resolved, so a feed cannot make the server fetch anything), and a date
/// that cannot be read is null rather than an error, since the item is still
/// worth having.
/// </remarks>
public static partial class RssFeedParser
{
    /// <summary>Summaries are kept whole up to this; beyond it a feed is carrying the article, not a summary.</summary>
    public const int MaxSummaryLength = 4000;

    // Query parameters that say where a click came from, not which article it
    // is. Two feeds of one publisher tag the same link differently (BBC's
    // at_medium, MarketWatch's mod, ET's from=mdr), and without dropping them
    // the same story would be stored once per feed.
    private static readonly string[] TrackingParameters = ["from", "ref", "mod", "cmpid", "at_medium", "at_campaign", "at_link_origin"];

    // Zone abbreviations seen in feeds, which .NET does not read.
    private static readonly Dictionary<string, string> ZoneNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IST"] = "+05:30", ["EST"] = "-05:00", ["EDT"] = "-04:00", ["CST"] = "-06:00", ["CDT"] = "-05:00",
        ["PST"] = "-08:00", ["PDT"] = "-07:00", ["BST"] = "+01:00", ["CET"] = "+01:00", ["CEST"] = "+02:00",
    };

    public static IReadOnlyList<FeedItem> Parse(string xml, TimeSpan zoneIfUnstated)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };

        // RBI's feed starts with a byte-order mark, which survives decoding as a character.
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')), settings);
        var document = XDocument.Load(reader);

        var items = new List<FeedItem>();
        foreach (var item in document.Descendants().Where(e => e.Name.LocalName is "item" or "entry"))
        {
            string title = Clean(Child(item, "title")?.Value);
            string link = LinkOf(item);
            if (title.Length == 0 && link.Length == 0) continue;

            string summary = Clean(Child(item, "description")?.Value ?? Child(item, "summary")?.Value ?? string.Empty);
            if (summary.Length > MaxSummaryLength) summary = summary[..MaxSummaryLength].TrimEnd() + "…";

            string? date = (Child(item, "pubDate") ?? Child(item, "published") ?? Child(item, "updated") ?? Child(item, "date"))?.Value;
            items.Add(new FeedItem(title.Length > 0 ? title : "(untitled)", link, summary, ParseDate(date, zoneIfUnstated)));
        }

        return items;
    }

    /// <summary>
    /// A feed's date as UTC. RFC 822 dates with a numeric or named zone, ISO
    /// 8601, and the shapes Indian regulators use ("24 Sep, 2026 +0530", or no
    /// zone at all, which is read in <paramref name="zoneIfUnstated"/>).
    /// </summary>
    public static DateTime? ParseDate(string? value, TimeSpan zoneIfUnstated)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string text = value.Trim();

        // "... 10:00:00 EDT" -> "... 10:00:00 -04:00"
        var named = TrailingZoneName().Match(text);
        if (named.Success && ZoneNames.TryGetValue(named.Groups[1].Value, out var offset))
            text = text[..named.Index] + " " + offset;

        // "24 Sep, 2026 +0530": the comma after the month is the one thing .NET will not skip.
        text = MonthComma().Replace(text, "$1");

        // "+0530" -> "+05:30", which every .NET pattern reads.
        text = CompactOffset().Replace(text, "$1$2:$3");

        bool hasZone = ExplicitZone().IsMatch(text);
        const DateTimeStyles styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var parsed)
            // .NET refuses a weekday that does not match the date, and feeds get it wrong; the date is what counts.
            && !DateTimeOffset.TryParse(Weekday().Replace(text, string.Empty), CultureInfo.InvariantCulture, styles, out parsed))
            return null;

        if (!hasZone) parsed = new DateTimeOffset(DateTime.SpecifyKind(parsed.DateTime, DateTimeKind.Unspecified), zoneIfUnstated);
        return parsed.UtcDateTime;
    }

    /// <summary>
    /// The link reduced to what identifies the article: no scheme, no "www.",
    /// no fragment, no click-tracking parameters, no doubled or trailing slash.
    /// Anything that is not an absolute http(s) URL is returned trimmed.
    /// </summary>
    public static string NormaliseLink(string link)
    {
        string trimmed = link.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return trimmed;

        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        string path = DoubledSlash().Replace(uri.AbsolutePath, "/");
        if (path.Length > 1) path = path.TrimEnd('/');

        var kept = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair =>
            {
                string name = pair.Split('=', 2)[0];
                return !name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)
                       && !name.StartsWith("syn-", StringComparison.OrdinalIgnoreCase)
                       && !TrackingParameters.Contains(name, StringComparer.OrdinalIgnoreCase);
            })
            .ToList();

        string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return host + port + path + (kept.Count > 0 ? "?" + string.Join('&', kept) : string.Empty);
    }

    /// <summary>
    /// The duplicate key for <c>news_items.LinkHash</c>: SHA-256 of the
    /// normalised link, or of source and title for an item with no link.
    /// </summary>
    public static string LinkHash(string source, string title, string link) =>
        Sha256Hex(link.Trim().Length > 0 ? NormaliseLink(link) : $"{source}\n{title}");

    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Tags removed, entities decoded (twice-encoded ones too), whitespace collapsed.</summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string text = WebUtility.HtmlDecode(value);
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        return Whitespace().Replace(text, " ").Trim();
    }

    private static XElement? Child(XElement item, string localName) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string LinkOf(XElement item)
    {
        var links = item.Elements().Where(e => e.Name.LocalName == "link").ToList();

        // RSS: <link>url</link>. Atom: <link rel="alternate" href="url"/>.
        string? text = links.Select(l => l.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        if (text is not null) return text;

        var atom = links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate") ?? links.FirstOrDefault();
        string? href = (string?)atom?.Attribute("href");
        if (!string.IsNullOrWhiteSpace(href)) return href.Trim();

        var guid = Child(item, "guid");
        return guid is not null && !string.Equals((string?)guid.Attribute("isPermaLink"), "false", StringComparison.OrdinalIgnoreCase)
               && Uri.TryCreate(guid.Value.Trim(), UriKind.Absolute, out _)
            ? guid.Value.Trim()
            : string.Empty;
    }

    [GeneratedRegex(@"\s([A-Za-z]{3,4})$")]
    private static partial Regex TrailingZoneName();

    [GeneratedRegex(@"^(\d{1,2}\s+[A-Za-z]{3}),")]
    private static partial Regex MonthComma();

    [GeneratedRegex(@"(\s)([+-]\d{2})(\d{2})$")]
    private static partial Regex CompactOffset();

    [GeneratedRegex(@"([+-]\d{2}:\d{2}|\bGMT|\bUTC|\bUT|Z)$")]
    private static partial Regex ExplicitZone();

    [GeneratedRegex(@"^[A-Za-z]{3,9},\s*")]
    private static partial Regex Weekday();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("/{2,}")]
    private static partial Regex DoubledSlash();
}
