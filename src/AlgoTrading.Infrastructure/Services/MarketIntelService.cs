using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketIntel;
using AlgoTrading.Infrastructure.Persistence;
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

    /// <summary>Feeds per category. All are public, key-less RSS endpoints.</summary>
    private static readonly IReadOnlyDictionary<string, (string Source, string Url)[]> Feeds =
        new Dictionary<string, (string, string)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["india"] =
            [
                ("Economic Times · Markets", "https://economictimes.indiatimes.com/markets/rssfeeds/1977021501.cms"),
                ("Economic Times · Stocks", "https://economictimes.indiatimes.com/markets/stocks/rssfeeds/2146842.cms"),
            ],
            ["global"] =
            [
                ("BBC Business", "https://feeds.bbci.co.uk/news/business/rss.xml"),
                ("Economic Times · Forex", "https://economictimes.indiatimes.com/markets/forex/rssfeeds/1150221130.cms"),
            ],
            ["commodities"] =
            [
                ("Economic Times · Commodities", "https://economictimes.indiatimes.com/markets/commodities/rssfeeds/1808152121.cms"),
            ],
        };

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

    public async Task<NewsResponse> GetNewsAsync(string category, CancellationToken cancellationToken = default)
    {
        if (!Feeds.TryGetValue(category, out var feeds))
        {
            throw new ArgumentException(
                $"Unknown news category '{category}'. Valid: {string.Join(", ", Feeds.Keys)}.");
        }

        string cacheKey = $"news:{category.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out NewsResponse? cached) && cached is not null)
        {
            return cached;
        }

        var items = new List<NewsItemDto>();
        foreach (var (source, url) in feeds)
        {
            try
            {
                items.AddRange(await FetchFeedAsync(source, url, cancellationToken));
            }
            catch (Exception ex)
            {
                // One dead feed must not blank the whole section.
                _logger.LogWarning(ex, "News feed failed: {Source}", source);
            }
        }

        var response = new NewsResponse(
            category.ToLowerInvariant(),
            DateTime.UtcNow,
            items
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

    private async Task<List<NewsItemDto>> FetchFeedAsync(
        string source, string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(MarketIntelService));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("AlgoTradingEngine/1.0 (+local dashboard)");

        using var httpResponse = await client.SendAsync(request, cancellationToken);
        httpResponse.EnsureSuccessStatusCode();

        var xml = XDocument.Parse(await httpResponse.Content.ReadAsStringAsync(cancellationToken));

        return xml.Descendants("item")
            .Select(item => new NewsItemDto(
                Title: item.Element("title")?.Value.Trim() ?? "(untitled)",
                Link: item.Element("link")?.Value.Trim() ?? "",
                Source: source,
                PublishedUtc: ParseRssDate(item.Element("pubDate")?.Value),
                Summary: Truncate(StripHtml(item.Element("description")?.Value), 220)))
            .Where(i => i.Link.Length > 0)
            .Take(15)
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

    private static DateTime? ParseRssDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.UtcDateTime
            : null;
    }

    private static string? StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(text).Trim();
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max].TrimEnd() + "…";
}
