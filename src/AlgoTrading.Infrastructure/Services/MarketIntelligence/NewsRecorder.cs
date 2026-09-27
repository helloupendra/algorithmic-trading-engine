using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>What one news poll did.</summary>
public sealed record NewsRecordReport(int Feeds, int FeedsFailed, int ItemsRead, int Stored);

/// <summary>
/// Reads every news feed and stores the headlines it has not seen before, each
/// with the moment it was first seen.
/// </summary>
/// <remarks>
/// <para>
/// Insert-only. A headline already stored (same <see cref="NewsItem.LinkHash"/>)
/// is left exactly as it is, so its FirstSeenUtc stays the first sighting
/// however many polls and feeds carry it later. That is the property the
/// forecasts rely on, and the one the tests pin down.
/// </para>
/// <para>
/// One feed failing costs only its own headlines for that poll: the others are
/// stored, and the failure is logged when it starts and when it ends
/// (<see cref="RecorderHealth"/>), not every five minutes.
/// </para>
/// </remarks>
public sealed class NewsRecorder
{
    public const string HttpClientName = "news-recorder";

    // Publishers tolerate a few parallel requests; all 33 in one burst would be rude.
    private const int Parallelism = 4;

    private readonly TradingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly MarketIntelligenceStatus _status;
    private readonly ILogger<NewsRecorder> _logger;

    public NewsRecorder(TradingDbContext db, IHttpClientFactory http, MarketIntelligenceStatus status, ILogger<NewsRecorder> logger)
    {
        _db = db;
        _http = http;
        _status = status;
        _logger = logger;
    }

    /// <summary>The clock FirstSeenUtc is read from; replaced in tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The feeds to read; every feed in <see cref="NewsFeedCatalog"/> unless a test says otherwise.</summary>
    public IReadOnlyList<(string Category, NewsFeed Feed)> Feeds { get; set; } = NewsFeedCatalog.RecorderFeeds();

    public async Task<NewsRecordReport> RecordAsync(CancellationToken ct)
    {
        var health = _status.Recorder(MarketIntelligenceNames.News);
        health.Attempted(Clock());

        using var gate = new SemaphoreSlim(Parallelism);
        var reads = Feeds.Select(async entry =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var items = await ReadFeedAsync(entry.Feed, ct);
                health.LogSourceRecovery(_logger, entry.Feed.Source);
                return (entry.Category, entry.Feed, Items: items, Failed: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                health.LogSourceFailure(_logger, entry.Feed.Source, Describe(ex), Clock());
                return (entry.Category, entry.Feed, Items: (IReadOnlyList<FeedItem>)[], Failed: true);
            }
            finally
            {
                gate.Release();
            }
        });
        var results = await Task.WhenAll(reads);

        // One timestamp for the whole poll: everything in it was first seen now.
        var seenUtc = Clock();
        var candidates = new List<NewsItem>();
        var inThisPoll = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (category, feed, items, _) in results)
        {
            foreach (var item in items)
            {
                string hash = RssFeedParser.LinkHash(feed.Source, item.Title, item.Link);

                // Two feeds of one poll carrying one story: the first feed in
                // catalogue order files it, which puts a console category first.
                if (!inThisPoll.Add(hash)) continue;

                candidates.Add(new NewsItem
                {
                    Source = feed.Source,
                    Category = category,
                    Title = item.Title,
                    Summary = item.Summary,
                    Link = item.Link,
                    LinkHash = hash,
                    PublishedUtc = item.PublishedUtc,
                    FirstSeenUtc = seenUtc,
                });
            }
        }

        int stored = await InsertNewAsync(candidates, ct);
        int failed = results.Count(r => r.Failed);
        var report = new NewsRecordReport(results.Length, failed, candidates.Count, stored);

        string message = $"{stored} new headline(s) from {results.Length - failed} of {results.Length} feed(s)";
        // The poll succeeded if anything could be read; a feed down is reported on its own.
        health.Finished(Clock(), message, failed == results.Length && results.Length > 0 ? "every feed failed" : null);
        _logger.LogDebug("News recorder: {Message}.", message);
        return report;
    }

    private async Task<int> InsertNewAsync(List<NewsItem> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return 0;

        var hashes = candidates.Select(c => c.LinkHash).ToList();
        var known = (await _db.NewsItems.AsNoTracking()
                .Where(n => hashes.Contains(n.LinkHash))
                .Select(n => n.LinkHash)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = candidates.Where(c => !known.Contains(c.LinkHash)).ToList();
        if (fresh.Count == 0) return 0;

        _db.NewsItems.AddRange(fresh);
        try
        {
            await _db.SaveChangesAsync(ct);
            return fresh.Count;
        }
        catch (DbUpdateException)
        {
            // Something else stored one of these between the read and the
            // write. The unique index kept its first sighting; store the rest
            // one at a time so a single clash does not cost the whole poll.
            _db.ChangeTracker.Clear();
            int stored = 0;
            foreach (var item in fresh)
            {
                item.Id = 0;
                _db.NewsItems.Add(item);
                try
                {
                    await _db.SaveChangesAsync(ct);
                    stored++;
                }
                catch (DbUpdateException)
                {
                }
                finally
                {
                    _db.ChangeTracker.Clear();
                }
            }

            return stored;
        }
    }

    private async Task<IReadOnlyList<FeedItem>> ReadFeedAsync(NewsFeed feed, CancellationToken ct)
    {
        using var response = await _http.CreateClient(HttpClientName).GetAsync(feed.Url, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        return RssFeedParser.Parse(await response.Content.ReadAsStringAsync(ct), feed.ZoneIfUnstated);
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timed out",
        System.Xml.XmlException xml => $"not a readable feed ({xml.Message})",
        _ => ex.Message,
    };

    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MarketIntelService.UserAgent);
    }
}
