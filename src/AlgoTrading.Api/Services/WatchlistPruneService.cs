using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Finds and removes recording-list rows the feed can no longer serve.
/// </summary>
/// <remarks>
/// Two reasons, both provable from what is stored:
/// <list type="bullet">
/// <item><b>expired</b> — the instrument's expiry date is behind today's IST
/// date. The weekly options a strategy watched last week are the usual case:
/// after expiry Tuesday they sit in the list showing "21h ago" forever.</item>
/// <item><b>silent</b> — the session has been open for twenty minutes, some
/// watchlist symbol ticked in the last five minutes (so the feed itself is
/// alive), and this one has not ticked since before 09:00 today. A dead feed
/// flags nothing: silence everywhere is a pipeline problem, not a symbol's.</item>
/// </list>
/// </remarks>
public sealed class WatchlistPruneService
{
    private static readonly TimeSpan SessionStart = new(9, 0, 0);
    private static readonly TimeSpan SilentAfter = new(9, 20, 0);
    private static readonly TimeSpan FeedAliveWindow = TimeSpan.FromMinutes(5);

    private readonly TradingDbContext _db;
    private readonly IRedisPublisherService _publisher;

    public WatchlistPruneService(TradingDbContext db, IRedisPublisherService publisher)
    {
        _db = db;
        _publisher = publisher;
    }

    public async Task<StaleWatchlistResponse> FindAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var nowIst = IstTime.ToIst(nowUtc);
        var today = DateOnly.FromDateTime(nowIst);

        var items = await _db.LiveWatchlistItems.AsNoTracking()
            .Select(x => new { x.Id, x.Symbol })
            .ToListAsync(cancellationToken);
        if (items.Count == 0) return new StaleWatchlistResponse();

        var symbols = items.Select(x => x.Symbol).ToList();
        var expiries = await _db.Instruments.AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol) && x.ExpiryDate != null)
            .Select(x => new { x.Symbol, x.ExpiryDate })
            .ToListAsync(cancellationToken);
        var expiryBySymbol = expiries.ToDictionary(x => x.Symbol, x => x.ExpiryDate!.Value, StringComparer.Ordinal);

        var quotes = await _db.LiveQuotesLatest.AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol))
            .Select(x => new { x.Symbol, x.UpdatedUtc })
            .ToListAsync(cancellationToken);
        var lastBySymbol = quotes.ToDictionary(x => x.Symbol, x => x.UpdatedUtc, StringComparer.Ordinal);

        bool feedAlive = quotes.Count > 0 && quotes.Max(q => q.UpdatedUtc) >= nowUtc - FeedAliveWindow;
        bool sessionOpen = nowIst.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && nowIst.TimeOfDay >= SilentAfter;
        DateTime? sessionStartUtc = sessionOpen ? IstTime.FromIst(nowIst.Date + SessionStart) : null;

        var response = new StaleWatchlistResponse { FeedAlive = feedAlive, SessionStartUtc = sessionStartUtc };
        foreach (var item in items)
        {
            lastBySymbol.TryGetValue(item.Symbol, out var last);
            DateTime? lastTick = lastBySymbol.ContainsKey(item.Symbol) ? last : null;

            if (expiryBySymbol.TryGetValue(item.Symbol, out var expiry) && expiry < today)
            {
                response.Items.Add(new StaleWatchlistItem
                {
                    Id = item.Id, Symbol = item.Symbol, Reason = "expired",
                    Detail = $"expired {expiry:dd MMM}",
                    ExpiryDate = expiry.ToString("yyyy-MM-dd"), LastTickUtc = lastTick,
                });
                continue;
            }

            if (feedAlive && sessionStartUtc is not null && (lastTick is null || lastTick < sessionStartUtc))
            {
                response.Items.Add(new StaleWatchlistItem
                {
                    Id = item.Id, Symbol = item.Symbol, Reason = "silent",
                    Detail = lastTick is null ? "never ticked" : $"no tick today (last {IstTime.ToIst(lastTick.Value):dd MMM HH:mm})",
                    LastTickUtc = lastTick,
                });
            }
        }
        return response;
    }

    /// <summary>Removes the stale rows (all of them, or only the ids asked for) and tells the ingestor the list changed.</summary>
    public async Task<PruneWatchlistResponse> PruneAsync(IReadOnlyCollection<long>? onlyIds, CancellationToken cancellationToken)
    {
        var stale = await FindAsync(cancellationToken);
        var targets = stale.Items
            .Where(x => onlyIds is null || onlyIds.Count == 0 || onlyIds.Contains(x.Id))
            .ToList();
        if (targets.Count == 0)
            return new PruneWatchlistResponse { Message = "Nothing to remove: every symbol on the list is current." };

        var ids = targets.Select(x => x.Id).ToList();
        var rows = await _db.LiveWatchlistItems.Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
        _db.LiveWatchlistItems.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        await _publisher.PublishWatchlistUpdateAsync(cancellationToken);

        int expired = targets.Count(x => x.Reason == "expired"), silent = targets.Count - expired;
        return new PruneWatchlistResponse
        {
            Removed = targets,
            Message = $"Removed {targets.Count} symbol(s): {expired} expired, {silent} silent.",
        };
    }
}
