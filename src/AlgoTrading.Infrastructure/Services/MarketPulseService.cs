using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The fixed "market at a glance" universe and its quotes.
/// </summary>
/// <remarks>
/// The trader's first screen used to be the whole live watchlist — a hundred
/// option strikes with no order to them. What a person wants first is the
/// market itself: the three index levels, the large caps that move them, and
/// the three commodities. That set is the platform's, the same for everyone,
/// and it is kept on the feed by <see cref="EnsureSubscribedAsync"/> so its
/// quotes are always fresh. Their own watchlist is a separate, per-user thing.
///
/// Commodities trade as monthly futures, so "Crude oil" is whichever contract
/// expires next. It is resolved from the instrument master, re-resolved
/// daily, and the contract it replaced is taken off the feed unless a trader
/// has it on their own list.
/// </remarks>
public class MarketPulseService : IMarketPulseService
{
    public const int FeedPriority = 5;

    private static readonly (string Symbol, string Name)[] Indices =
    {
        ("NSE:NIFTY50-INDEX", "NIFTY 50"),
        ("NSE:NIFTYBANK-INDEX", "BANK NIFTY"),
        ("BSE:SENSEX-INDEX", "SENSEX"),
    };

    // The heavyweights of NIFTY 50 / BANK NIFTY / SENSEX by index weight.
    private static readonly (string Symbol, string Name)[] Equities =
    {
        ("NSE:RELIANCE-EQ", "Reliance"),
        ("NSE:HDFCBANK-EQ", "HDFC Bank"),
        ("NSE:ICICIBANK-EQ", "ICICI Bank"),
        ("NSE:INFY-EQ", "Infosys"),
        ("NSE:TCS-EQ", "TCS"),
        ("NSE:ITC-EQ", "ITC"),
        ("NSE:LT-EQ", "L&T"),
        ("NSE:SBIN-EQ", "SBI"),
        ("NSE:BHARTIARTL-EQ", "Bharti Airtel"),
        ("NSE:AXISBANK-EQ", "Axis Bank"),
        ("NSE:KOTAKBANK-EQ", "Kotak Bank"),
        ("NSE:HINDUNILVR-EQ", "Hindustan Unilever"),
    };

    // Root of the MCX future; the contract month is resolved at run time.
    private static readonly (string Root, string Name)[] Commodities =
    {
        ("CRUDEOIL", "Crude oil"),
        ("GOLD", "Gold"),
        ("SILVER", "Silver"),
    };

    private readonly TradingDbContext _db;
    private readonly ILiveDataService _liveData;
    private readonly IRedisPublisherService _publisher;
    private readonly ILogger<MarketPulseService> _logger;

    public MarketPulseService(TradingDbContext db, ILiveDataService liveData, IRedisPublisherService publisher, ILogger<MarketPulseService> logger)
    {
        _db = db;
        _liveData = liveData;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<MarketPulseResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var commodities = await ResolveCommoditiesAsync(cancellationToken);

        var rows = new List<(string Group, string Symbol, string Name, string? Contract)>();
        rows.AddRange(Indices.Select(i => ("index", i.Symbol, i.Name, (string?)null)));
        rows.AddRange(Equities.Select(e => ("equity", e.Symbol, e.Name, (string?)null)));
        rows.AddRange(commodities.Select(c => ("commodity", c.Symbol, c.Name, c.Contract)));

        var symbols = rows.Select(r => r.Symbol).ToList();
        var quotes = await _db.LiveQuotesLatest.AsNoTracking()
            .Where(q => symbols.Contains(q.Symbol))
            .ToDictionaryAsync(q => q.Symbol, cancellationToken);
        var subscribed = await _db.LiveWatchlistItems.AsNoTracking()
            .Where(w => w.IsActive && symbols.Contains(w.Symbol))
            .Select(w => w.Symbol)
            .ToListAsync(cancellationToken);
        var subscribedSet = subscribed.ToHashSet(StringComparer.OrdinalIgnoreCase);

        MarketPulseItem ToItem((string Group, string Symbol, string Name, string? Contract) r)
        {
            quotes.TryGetValue(r.Symbol, out var q);
            decimal? change = q?.LastTradedPrice is { } ltp && q.Close is { } close ? ltp - close : null;
            return new MarketPulseItem
            {
                Symbol = r.Symbol,
                Name = r.Name,
                Contract = r.Contract,
                LastTradedPrice = q?.LastTradedPrice,
                PreviousClose = q?.Close,
                Open = q?.Open,
                High = q?.High,
                Low = q?.Low,
                Volume = q?.Volume,
                Change = change,
                ChangePercent = change is { } c && q?.Close is > 0 ? Math.Round(c / q.Close.Value * 100m, 2) : null,
                UpdatedUtc = q?.UpdatedUtc,
                IsSubscribed = subscribedSet.Contains(r.Symbol),
            };
        }

        var groups = new[]
        {
            ("index", "Indices"),
            ("equity", "Large caps"),
            ("commodity", "Commodities"),
        }.Select(g => new MarketPulseGroup
        {
            Key = g.Item1,
            Title = g.Item2,
            Items = rows.Where(r => r.Group == g.Item1).Select(ToItem).ToList(),
        }).ToList();

        return new MarketPulseResponse
        {
            Groups = groups,
            LatestQuoteUtc = quotes.Values.Select(q => (DateTime?)q.UpdatedUtc).DefaultIfEmpty(null).Max(),
        };
    }

    public async Task<IReadOnlyList<string>> EnsureSubscribedAsync(CancellationToken cancellationToken = default)
    {
        var commodities = await ResolveCommoditiesAsync(cancellationToken);
        var wanted = Indices.Select(i => i.Symbol)
            .Concat(Equities.Select(e => e.Symbol))
            .Concat(commodities.Select(c => c.Symbol))
            .ToList();

        var active = await _db.LiveWatchlistItems.AsNoTracking()
            .Where(w => w.IsActive && wanted.Contains(w.Symbol))
            .Select(w => w.Symbol)
            .ToListAsync(cancellationToken);
        var missing = wanted.Except(active, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var symbol in missing)
        {
            await _liveData.UpsertWatchlistItemAsync(
                new UpsertWatchlistItemRequest { Symbol = symbol, IsActive = true, Priority = FeedPriority },
                cancellationToken);
        }

        // The contract each commodity rolled away from: still on the feed, but
        // nobody is looking at it — unless a trader put it on their own list.
        var retired = await RetireExpiredContractsAsync(commodities.Select(c => c.Symbol).ToList(), cancellationToken);

        if (missing.Count > 0 || retired > 0)
        {
            await _publisher.PublishWatchlistUpdateAsync(cancellationToken);
            _logger.LogInformation("Market pulse: subscribed {Added} symbol(s), retired {Retired} expired contract(s). Commodities: {Contracts}.",
                missing.Count, retired, string.Join(", ", commodities.Select(c => c.Symbol)));
        }

        return wanted;
    }

    private async Task<int> RetireExpiredContractsAsync(List<string> current, CancellationToken ct)
    {
        var today = IstTime.DateOf(DateTime.UtcNow);
        var expired = await (
            from w in _db.LiveWatchlistItems
            join i in _db.Instruments on w.Symbol equals i.Symbol
            where w.IsActive
                  && i.Exchange == "MCX"
                  && i.ExpiryDate != null && i.ExpiryDate < today
                  && !current.Contains(w.Symbol)
            select w).ToListAsync(ct);
        if (expired.Count == 0) return 0;

        var keep = await _db.UserWatchlistItems.AsNoTracking()
            .Where(u => expired.Select(e => e.Symbol).Contains(u.Symbol))
            .Select(u => u.Symbol)
            .ToListAsync(ct);
        var retired = 0;
        foreach (var row in expired.Where(e => !keep.Contains(e.Symbol)))
        {
            row.IsActive = false;
            row.UpdatedUtc = DateTime.UtcNow;
            retired++;
        }
        if (retired > 0) await _db.SaveChangesAsync(ct);
        return retired;
    }

    /// <summary>Each commodity's nearest unexpired future, from the instrument master.</summary>
    private async Task<List<(string Symbol, string Name, string? Contract)>> ResolveCommoditiesAsync(CancellationToken ct)
    {
        var today = IstTime.DateOf(DateTime.UtcNow);
        var result = new List<(string, string, string?)>();
        foreach (var (root, name) in Commodities)
        {
            // MCX:CRUDEOIL26SEPFUT — root, two-digit year, three-letter month, FUT.
            var pattern = $"MCX:{root}_____FUT";
            var contract = await _db.Instruments.AsNoTracking()
                .Where(i => i.Exchange == "MCX" && i.ExpiryDate != null && i.ExpiryDate >= today
                            && EF.Functions.Like(i.Symbol, pattern))
                .OrderBy(i => i.ExpiryDate)
                .Select(i => new { i.Symbol, i.ExpiryDate })
                .FirstOrDefaultAsync(ct);
            if (contract is null)
            {
                _logger.LogWarning("Market pulse: no unexpired MCX {Root} future in the instrument master — download the MCX symbol master.", root);
                continue;
            }
            result.Add((contract.Symbol, name, contract.ExpiryDate!.Value.ToString("MMM yyyy", CultureInfo.InvariantCulture)));
        }
        return result;
    }
}
