// src/AlgoTrading.Api/Services/ProviderUsageService.cs

using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.Providers;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Reads, for one connector, what the platform is taking from it right now:
/// its session, its feed, and the rows it has written to each price table.
/// </summary>
/// <remarks>
/// Every connector is read the same way. Rows carry the connector key as their
/// SourceKey, feeds come from <see cref="FeedSupervisorRegistry"/>, and routing
/// from the router, so nothing here names a vendor.
/// <para>
/// The console polls this every 10 seconds, so every query is bounded: by an
/// indexed time column (ticks), by a small table (latest quotes, heartbeats),
/// or by the newest N primary keys where the time column has no index of its
/// own (chain snapshots, candles). Slow-moving answers are cached for a minute.
/// </para>
/// <para>
/// Each read is isolated. One that fails makes its items "unknown" and leaves
/// the others standing; it never turns into "off".
/// </para>
/// </remarks>
public sealed class ProviderUsageService
{
    /// <summary>
    /// Chain rows read back from the newest id. A poll writes a few hundred rows,
    /// so this covers the 10-minute window many times over; when it does not, the
    /// answer is "unknown", not "off".
    /// </summary>
    internal const int ChainRowWindow = 50_000;

    /// <summary>Candle rows read back from the newest id, for the "newest stored bar" line.</summary>
    internal const int CandleRowWindow = 20_000;

    private static readonly TimeSpan SlowCacheFor = TimeSpan.FromMinutes(1);

    private readonly TradingDbContext _db;
    private readonly IProviderCatalog _catalog;
    private readonly IProviderRouter _router;
    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IBrokerSessionStore _sessions;
    private readonly FeedSupervisorRegistry _feeds;
    private readonly IMarketSessionService _marketSessions;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ProviderUsageService> _logger;

    public ProviderUsageService(
        TradingDbContext db,
        IProviderCatalog catalog,
        IProviderRouter router,
        IBrokerCredentialsProvider credentials,
        IBrokerSessionStore sessions,
        FeedSupervisorRegistry feeds,
        IMarketSessionService marketSessions,
        IMemoryCache cache,
        ILogger<ProviderUsageService> logger)
    {
        _db = db;
        _catalog = catalog;
        _router = router;
        _credentials = credentials;
        _sessions = sessions;
        _feeds = feeds;
        _marketSessions = marketSessions;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>The usage picture for a connector, or null when no connector has that key.</summary>
    public async Task<ProviderUsageResponse?> GetAsync(string providerKey, CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(providerKey);
        if (descriptor is null) return null;

        string key = descriptor.Key;
        var caps = descriptor.Capabilities;
        var now = DateTime.UtcNow;

        var markets = ReadMarkets(now);
        bool? marketsOpen = ProviderUsageRules.AnyCoveredMarketOpen(markets, caps.Segments);
        string closedReason = ProviderUsageRules.ClosedReason(markets, caps.Segments);

        var session = await ReadSessionAsync(descriptor, cancellationToken);
        var feed = await ReadFeedAsync(key, caps.LiveTicks, now, cancellationToken);

        bool? feedRunning = ProviderUsageRules.FeedRunning(feed, now);

        bool wantsQuotes = caps.Quotes || caps.LiveTicks || caps.Depth || caps.OpenInterest || caps.Greeks;
        var quotes = wantsQuotes ? await ReadQuotesAsync(key, now, cancellationToken) : null;

        bool wantsChain = caps.OptionChain || caps.OpenInterest || caps.Greeks;
        var chain = wantsChain ? await ReadChainAsync(key, now, cancellationToken) : null;

        var (position, primaryName) = caps.History
            ? await ReadHistoryRoutingAsync(key, cancellationToken)
            : (null, null);
        var candles = caps.History ? await ReadCandlesAsync(key, cancellationToken) : null;

        var instruments = caps.UsesCanonicalSymbols ? null : await ReadInstrumentsAsync(key, cancellationToken);

        var items = new List<ProviderUsageItemResponse>
        {
            Item("session", "Session", true,
                ProviderUsageRules.Session(session, now, dataArriving: feed.TicksLastMinute > 0 || quotes?.Fresh > 0)),
            Item("liveTicks", "Live ticks", caps.LiveTicks,
                ProviderUsageRules.LiveTicks(caps.LiveTicks, feed, markets, caps.Segments, now)),
            Item("quotes", "Quotes", caps.Quotes,
                ProviderUsageRules.Quotes(caps.Quotes, quotes, feedRunning, marketsOpen, closedReason, now)),
            Item("depth", "Bid/ask depth", caps.Depth,
                ProviderUsageRules.Depth(caps.Depth, quotes, feedRunning, marketsOpen, closedReason)),
            Item("openInterest", "Open interest", caps.OpenInterest,
                ProviderUsageRules.OpenInterest(caps.OpenInterest, quotes, chain, marketsOpen, closedReason)),
            Item("greeks", "Greeks", caps.Greeks,
                ProviderUsageRules.Greeks(caps.Greeks, quotes, chain, marketsOpen, closedReason)),
            Item("optionChain", "Option chain", caps.OptionChain,
                ProviderUsageRules.OptionChain(caps.OptionChain, chain, marketsOpen, closedReason, now)),
            Item("history", "History", caps.History,
                ProviderUsageRules.History(caps.History, position, primaryName, candles, now)),
            Item("instruments", "Instruments", !caps.UsesCanonicalSymbols,
                ProviderUsageRules.Instruments(caps.UsesCanonicalSymbols, instruments, now)),
            Item("orders", "Orders", caps.Orders,
                ProviderUsageRules.Orders(caps.Orders)),
        };

        return new ProviderUsageResponse
        {
            ProviderKey = key,
            CheckedUtc = now,
            Markets = new ProviderUsageMarketsResponse
            {
                Nse = new ProviderUsageMarketResponse { Open = markets.NseOpen, Holiday = markets.NseHoliday },
                Mcx = new ProviderUsageMarketResponse { Open = markets.McxOpen, Holiday = markets.McxHoliday },
            },
            Items = items,
        };
    }

    private static ProviderUsageItemResponse Item(string id, string label, bool offered, UsageVerdict verdict) => new()
    {
        Id = id,
        Label = label,
        Offered = offered,
        State = ProviderUsageRules.Wire(verdict.State),
        Summary = verdict.Summary,
        LastUtc = verdict.LastUtc,
    };

    // ------------------------------------------------------------------
    // Reads. Each returns null (or an "unknown" flag) when it fails.
    // ------------------------------------------------------------------

    private UsageMarkets ReadMarkets(DateTime now)
    {
        var (nseOpen, nseHoliday) = ReadMarket(now, "NSE", "FO");
        var (mcxOpen, mcxHoliday) = ReadMarket(now, "MCX", "COM");
        return new UsageMarkets(nseOpen, nseHoliday, mcxOpen, mcxHoliday);
    }

    private (bool? Open, string? Holiday) ReadMarket(DateTime now, string exchange, string segment)
    {
        try
        {
            var info = _marketSessions.GetSessionInfo(now, exchange, segment);
            return (info.IsMarketOpen, info.HolidayName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Usage: the {Exchange} session could not be read.", exchange);
            return (null, null);
        }
    }

    private async Task<UsageSessionFacts> ReadSessionAsync(ProviderDescriptor descriptor, CancellationToken ct)
    {
        if (descriptor.Auth == ProviderAuthKind.None)
        {
            return new UsageSessionFacts(descriptor.Auth, CredentialsSaved: null, TokenKnown: true, Token: null);
        }

        var credentials = await TryAsync(
            "credentials",
            descriptor.Key,
            async () => (bool?)((await _credentials.GetAsync(descriptor.Key, cancellationToken: ct)).Source != "none"),
            ct);

        var session = await TryAsync(
            "session",
            descriptor.Key,
            async () => new SessionBox(await _sessions.GetForProviderAsync(descriptor.Key, ct)),
            ct);

        var row = session?.Session;
        var token = row is null
            ? null
            : new UsageToken(
                Active: row.IsActive && !string.IsNullOrWhiteSpace(row.AccessToken),
                IssuedUtc: row.UpdatedUtc,
                ExpiresUtc: row.ExpiresAtUtc);

        return new UsageSessionFacts(descriptor.Auth, credentials, TokenKnown: session is not null, token);
    }

    private sealed record SessionBox(AlgoTrading.Domain.Entities.BrokerSession? Session);

    private async Task<UsageFeedFacts> ReadFeedAsync(string key, bool offered, DateTime now, CancellationToken ct)
    {
        var supervisor = offered ? _feeds.Get(key) : null;
        if (supervisor is null)
        {
            return new UsageFeedFacts(false, null, false, null, null, null);
        }

        var status = await TryAsync("feed status", key, async () => (bool?)(await supervisor.GetStatusAsync(ct)).IsRunning, ct);

        var names = ProviderUsageRules.HeartbeatSourceNames(key, supervisor is IngestorSupervisor);
        var beats = await TryAsync(
            "heartbeat",
            key,
            () => _db.LiveIngestorStatuses
                .AsNoTracking()
                .Where(x => names.Contains(x.SourceName))
                .OrderByDescending(x => x.LastHeartbeatUtc)
                .Take(1)
                .ToListAsync(ct),
            ct);

        var beat = beats?.FirstOrDefault() is { } b
            ? new UsageHeartbeat(
                b.SourceName,
                b.Status,
                b.LastHeartbeatUtc,
                CountSymbols(b.CurrentSubscribedSymbolsJson),
                ProviderUsageRules.IsRecapSourceName(b.SourceName),
                b.LastError)
            : null;

        // Both reads walk the ReceivedUtc index of the hypertable, newest chunk only.
        var tickSince = now - ProviderUsageRules.TickWindow;
        var ticks = await TryAsync(
            "tick count",
            key,
            async () => (long?)await _db.LiveTicks
                .AsNoTracking()
                .Where(x => x.ReceivedUtc >= tickSince && x.SourceKey == key)
                .LongCountAsync(ct),
            ct);

        var lookback = now - ProviderUsageRules.TickLookback;
        var lastTick = await TryAsync(
            "last tick",
            key,
            async () => new TimeBox(await _db.LiveTicks
                .AsNoTracking()
                .Where(x => x.ReceivedUtc >= lookback && x.SourceKey == key)
                .MaxAsync(x => (DateTime?)x.ReceivedUtc, ct)),
            ct);

        return new UsageFeedFacts(true, status, beats is not null, beat, ticks, lastTick?.Utc);
    }

    private sealed record TimeBox(DateTime? Utc);

    private static int CountSymbols(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.Count ?? 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private Task<UsageQuoteFacts?> ReadQuotesAsync(string key, DateTime now, CancellationToken ct)
    {
        // One row per symbol, so this table stays in the thousands; one pass counts everything.
        var since = now - ProviderUsageRules.QuoteWindow;

        return TryAsync(
            "quotes",
            key,
            async () =>
            {
                var row = await _db.LiveQuotesLatest
                    .AsNoTracking()
                    .Where(x => x.SourceKey == key)
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        Total = g.LongCount(),
                        Fresh = g.LongCount(x => x.UpdatedUtc >= since),
                        Depth = g.LongCount(x => x.UpdatedUtc >= since && (x.BidSize > 0 || x.AskSize > 0)),
                        Oi = g.LongCount(x => x.UpdatedUtc >= since && x.OpenInterest > 0),
                        Greeks = g.LongCount(x => x.UpdatedUtc >= since && (x.Delta != null || x.ImpliedVolatility != null)),
                        Newest = g.Max(x => (DateTime?)x.UpdatedUtc),
                    })
                    .FirstOrDefaultAsync(ct);

                return row is null
                    ? new UsageQuoteFacts(0, 0, 0, 0, 0, null)
                    : new UsageQuoteFacts(row.Total, row.Fresh, row.Depth, row.Oi, row.Greeks, row.Newest);
            },
            ct);
    }

    private Task<UsageChainFacts?> ReadChainAsync(string key, DateTime now, CancellationToken ct)
    {
        var since = now - ProviderUsageRules.ChainWindow;

        return TryAsync(
            "chain snapshots",
            key,
            async () =>
            {
                var chain = _db.OptionChainSnapshots.AsNoTracking();

                long? maxId = await chain.MaxAsync(x => (long?)x.Id, ct);
                if (maxId is null)
                {
                    return new UsageChainFacts(true, null, 0, Array.Empty<string>(), 0, 0);
                }

                // CapturedUtc has no index of its own, but ids grow with time: the
                // newest ids are the newest captures. The row just below the window
                // says whether the window reaches back far enough to prove absence.
                long floor = maxId.Value - ChainRowWindow;
                var boundary = await chain
                    .Where(x => x.Id <= floor)
                    .OrderByDescending(x => x.Id)
                    .Select(x => (DateTime?)x.CapturedUtc)
                    .FirstOrDefaultAsync(ct);
                bool covered = boundary is null || boundary < since;

                var recent = chain.Where(x => x.Id > floor && x.SourceKey == key && x.CapturedUtc >= since);

                var captures = await recent
                    .GroupBy(x => x.CapturedUtc)
                    .Select(g => new
                    {
                        CapturedUtc = g.Key,
                        Rows = g.Count(),
                        Oi = g.LongCount(x => x.OpenInterest > 0),
                        Greeks = g.LongCount(x => x.Delta != null || x.ImpliedVolatility != null),
                    })
                    .ToListAsync(ct);

                if (captures.Count == 0)
                {
                    return new UsageChainFacts(covered, null, 0, Array.Empty<string>(), 0, 0);
                }

                var latest = captures.MaxBy(x => x.CapturedUtc)!;

                var underlyings = await recent
                    .Select(x => x.Underlying)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToListAsync(ct);

                return new UsageChainFacts(
                    covered,
                    latest.CapturedUtc,
                    latest.Rows,
                    underlyings,
                    captures.Sum(x => x.Oi),
                    captures.Sum(x => x.Greeks));
            },
            ct);
    }

    private async Task<(int? Position, string? PrimaryName)> ReadHistoryRoutingAsync(string key, CancellationToken ct)
    {
        var chain = await TryAsync(
            "history routing",
            key,
            async () => (await _router.ResolveDataChainAsync(ProviderCapability.History, cancellationToken: ct))
                .Select(p => p.Descriptor)
                .ToList(),
            ct);

        if (chain is null) return (null, null);

        int position = chain.FindIndex(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
        return (position, chain.Count > 0 ? chain[0].DisplayName : null);
    }

    private Task<UsageCandleFacts?> ReadCandlesAsync(string key, CancellationToken ct)
        => CachedAsync($"usage:candles:{key}", "stored bars", key, async () =>
        {
            var candles = _db.Candles.AsNoTracking();

            long? maxId = await candles.MaxAsync(x => (long?)x.Id, ct);
            if (maxId is null) return new UsageCandleFacts(null, null, 0);

            long floor = maxId.Value - CandleRowWindow;
            int examined = (int)Math.Min(maxId.Value, CandleRowWindow);

            var newest = await candles
                .Where(x => x.Id > floor && x.SourceKey == key)
                .OrderByDescending(x => x.TimeStampUtc)
                .Select(x => new { x.TimeStampUtc, x.Resolution })
                .FirstOrDefaultAsync(ct);

            return new UsageCandleFacts(newest?.TimeStampUtc, newest?.Resolution, examined);
        }, ct);

    private Task<UsageInstrumentFacts?> ReadInstrumentsAsync(string key, CancellationToken ct)
        => CachedAsync($"usage:instruments:{key}", "vendor symbols", key, async () =>
        {
            var row = await _db.InstrumentVendorSymbols
                .AsNoTracking()
                .Where(x => x.ProviderKey == key)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.LongCount(), Updated = g.Max(x => (DateTime?)x.UpdatedUtc) })
                .FirstOrDefaultAsync(ct);

            return new UsageInstrumentFacts(row?.Count ?? 0, row?.Updated);
        }, ct);

    // ------------------------------------------------------------------

    /// <summary>Caches a successful answer for a minute; a failure is never cached.</summary>
    private async Task<T?> CachedAsync<T>(string cacheKey, string what, string providerKey, Func<Task<T>> read, CancellationToken ct)
        where T : class
    {
        if (_cache.TryGetValue(cacheKey, out T? hit) && hit is not null) return hit;

        var value = await TryAsync(what, providerKey, read, ct);
        if (value is not null) _cache.Set(cacheKey, value, SlowCacheFor);
        return value;
    }

    /// <summary>Runs one read; a failure is logged and becomes null, which the rules report as unknown.</summary>
    private async Task<T?> TryAsync<T>(string what, string providerKey, Func<Task<T>> read, CancellationToken ct)
    {
        try
        {
            return await read();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Usage: {What} for connector {Key} could not be read.", what, providerKey);
            return default;
        }
    }
}
