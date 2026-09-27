using System.Collections.Concurrent;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketIntel;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Fetches market news (public RSS) and day movers (public Yahoo quote data)
/// server-side, because browsers cannot call those origins directly (CORS).
/// Responses are cached for a few minutes so the dashboard polling never
/// hammers the upstream sources. All of it is informational market data.
/// </summary>
public class MarketIntelService : IMarketIntelService
{
    private static readonly TimeSpan NewsCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MoversCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>What the desk calls itself to the publishers; the recorder sends the same.</summary>
    public const string UserAgent = "AlgoTradingEngine/1.0 (+local dashboard)";

    // The categories and their feeds live in NewsFeedCatalog, which the news
    // recorder reads too, so the console and the recorded history can never be
    // reading two different lists of feeds.
    private static readonly IReadOnlyDictionary<string, NewsFeedCategory> CategoriesByKey =
        NewsFeedCatalog.ConsoleCategories.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly TradingDbContext _dbContext;
    private readonly ILogger<MarketIntelService> _logger;

    public MarketIntelService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        TradingDbContext dbContext,
        ILogger<MarketIntelService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _dbContext = dbContext;
        _logger = logger;
    }

    public IReadOnlyList<NewsCategoryDto> GetNewsCategories()
        => NewsFeedCatalog.ConsoleCategories.Select(c => new NewsCategoryDto(c.Key, c.Label, c.Group)).ToList();

    public async Task<NewsResponse> GetNewsAsync(string category, CancellationToken cancellationToken = default)
    {
        if (!CategoriesByKey.TryGetValue(category, out var entry))
        {
            throw new ArgumentException(
                $"Unknown news category '{category}'. Valid: {string.Join(", ", CategoriesByKey.Keys)}.");
        }

        string cacheKey = $"news:{entry.Key}";
        if (_cache.TryGetValue(cacheKey, out NewsResponse? cached) && cached is not null)
        {
            return cached;
        }

        // The feeds of a category are fetched together rather than one after
        // another: a category now holds up to four, and read in series the
        // slowest one would decide how long every cache miss took.
        var fetches = entry.Feeds.Select(async feed =>
        {
            try
            {
                return await FetchFeedAsync(feed, cancellationToken);
            }
            catch (Exception ex)
            {
                // One dead feed must not blank the whole section.
                _logger.LogWarning(ex, "News feed failed: {Source}", feed.Source);
                return new List<NewsItemDto>();
            }
        });

        var items = (await Task.WhenAll(fetches)).SelectMany(x => x);

        var response = new NewsResponse(
            entry.Key,
            DateTime.UtcNow,
            items
                // Two publishers carrying the same wire story give the same
                // link twice; the headline is left alone, because two outlets
                // wording one story differently are two headlines.
                .DistinctBy(i => i.Link, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(i => i.PublishedUtc ?? DateTime.MinValue)
                .Take(30)
                .ToList());

        _cache.Set(cacheKey, response, NewsCacheTtl);
        return response;
    }

    public async Task<MoversResponse> GetMoversAsync(
        string groupName, int top = 10, CancellationToken cancellationToken = default)
    {
        string cacheKey = $"movers:{groupName.ToUpperInvariant()}:{top}";
        if (_cache.TryGetValue(cacheKey, out MoversResponse? cached) && cached is not null)
        {
            return cached;
        }

        var group = await _dbContext.EquityGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Name == groupName, cancellationToken)
            ?? throw new ArgumentException($"Unknown equity group '{groupName}'.");

        var symbols = await _dbContext.EquityGroupMembers
            .AsNoTracking()
            .Where(m => m.EquityGroupId == group.Id && m.IsEnabled)
            .Select(m => m.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        var results = new ConcurrentBag<MoverDto>();
        int failed = 0;

        // Yahoo tolerates modest parallelism; keep it polite and bounded.
        using var gate = new SemaphoreSlim(6);
        var tasks = symbols.Select(async symbol =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var mover = await FetchMoverAsync(symbol, cancellationToken);
                if (mover is not null) results.Add(mover);
                else Interlocked.Increment(ref failed);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                _logger.LogDebug(ex, "Mover fetch failed for {Symbol}", symbol);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);

        var withChange = results
            .Where(m => m.ChangePercent is not null)
            .ToList();

        var response = new MoversResponse(
            group.Name,
            string.IsNullOrWhiteSpace(group.DisplayName) ? group.Name : group.DisplayName,
            DateTime.UtcNow,
            withChange.OrderByDescending(m => m.ChangePercent).Take(top).ToList(),
            withChange.OrderBy(m => m.ChangePercent).Take(top).ToList(),
            withChange.Count,
            failed);

        _cache.Set(cacheKey, response, MoversCacheTtl);
        return response;
    }

    // ---------- helpers ----------

    private async Task<List<NewsItemDto>> FetchFeedAsync(NewsFeed feed, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(MarketIntelService));
        using var request = new HttpRequestMessage(HttpMethod.Get, feed.Url);
        request.Headers.UserAgent.ParseAdd(UserAgent);

        using var httpResponse = await client.SendAsync(request, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var items = RssFeedParser.Parse(await httpResponse.Content.ReadAsStringAsync(cancellationToken), feed.ZoneIfUnstated);

        return items
            .Where(i => i.Link.Length > 0)
            .Take(15)
            .Select(i => new NewsItemDto(
                Title: i.Title,
                Link: i.Link,
                Source: feed.Source,
                PublishedUtc: i.PublishedUtc,
                Summary: i.Summary.Length > 0 ? Truncate(i.Summary, 220) : null))
            .ToList();
    }

    private async Task<MoverDto?> FetchMoverAsync(string symbol, CancellationToken cancellationToken)
    {
        string? yahooSymbol = ToYahooSymbol(symbol);
        if (yahooSymbol is null) return null;

        var client = _httpClientFactory.CreateClient(nameof(MarketIntelService));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}?range=1d&interval=1d");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (AlgoTradingEngine local dashboard)");

        using var httpResponse = await client.SendAsync(request, cancellationToken);
        if (!httpResponse.IsSuccessStatusCode) return null;

        using var json = JsonDocument.Parse(await httpResponse.Content.ReadAsStringAsync(cancellationToken));
        var meta = json.RootElement
            .GetProperty("chart").GetProperty("result")[0].GetProperty("meta");

        decimal? last = ReadDecimal(meta, "regularMarketPrice");
        decimal? prevClose = ReadDecimal(meta, "chartPreviousClose") ?? ReadDecimal(meta, "previousClose");

        decimal? changePct = last is not null && prevClose is > 0
            ? Math.Round((last.Value - prevClose.Value) / prevClose.Value * 100m, 2)
            : null;

        return new MoverDto(symbol, yahooSymbol, last, prevClose, changePct);
    }

    /// <summary>NSE:HDFCBANK-EQ → HDFCBANK.NS; BSE:RELIANCE → RELIANCE.BO.</summary>
    private static string? ToYahooSymbol(string symbol)
    {
        var parts = symbol.Split(':', 2);
        if (parts.Length != 2) return null;

        string exchange = parts[0].ToUpperInvariant();
        string name = parts[1];
        if (name.EndsWith("-EQ", StringComparison.OrdinalIgnoreCase)) name = name[..^3];

        // Yahoo uses '-' only for share classes; NSE names with '&' (M&M) pass through.
        return exchange switch
        {
            "NSE" => $"{name}.NS",
            "BSE" => $"{name}.BO",
            _ => null,
        };
    }

    private static decimal? ReadDecimal(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
