using System.Globalization;
using System.Text.RegularExpressions;
using AlgoTrading.Contracts.MarketFactors;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Angel;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services.MarketFactors;

/// <summary>
/// Shapes the stored market factors into what the Market factors page shows.
/// </summary>
public sealed class MarketFactorsQueries
{
    public static readonly string[] IndexUnderlyings = ["NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY"];
    public static readonly string[] ParticipantOrder = ["FII", "DII", "Pro", "Client"];

    /// <summary>A live futures quote older than this is shown, but not as live.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    private readonly TradingDbContext _db;
    private readonly MarketFactorsStatus _status;

    public MarketFactorsQueries(TradingDbContext db, MarketFactorsStatus status)
    {
        _db = db;
        _status = status;
    }

    public List<MarketFactorsDatasetStatusDto> Status() => _status.All().Select(s => new MarketFactorsDatasetStatusDto
    {
        Dataset = s.Dataset,
        LastAttemptUtc = s.LastAttemptUtc,
        LastSuccessUtc = s.LastSuccessUtc,
        NewestDay = s.NewestDay,
        LastMessage = s.LastMessage,
        LastFailed = s.LastFailed,
    }).ToList();

    public async Task<MarketFlowsResponse> FlowsAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 250);

        var cashDates = await _db.MarketCashFlows.AsNoTracking()
            .Select(x => x.Date).Distinct().OrderByDescending(d => d).Take(days).ToListAsync(ct);
        var cash = cashDates.Count == 0
            ? []
            : await _db.MarketCashFlows.AsNoTracking().Where(x => x.Date >= cashDates.Min()).ToListAsync(ct);

        // One more day than shown, so the oldest shown day still gets its change.
        var participantDates = await _db.MarketParticipantOpenInterest.AsNoTracking()
            .Select(x => x.Date).Distinct().OrderByDescending(d => d).Take(days + 1).ToListAsync(ct);
        var participants = participantDates.Count == 0
            ? []
            : await _db.MarketParticipantOpenInterest.AsNoTracking().Where(x => x.Date >= participantDates.Min()).ToListAsync(ct);

        return new MarketFlowsResponse
        {
            ServerUtc = DateTime.UtcNow,
            Cash = ShapeCash(cash),
            Participants = ShapeParticipants(participants).Take(days).ToList(),
            Status = Status(),
        };
    }

    public static List<CashFlowDayDto> ShapeCash(IEnumerable<MarketCashFlow> rows) =>
        rows.GroupBy(r => r.Date)
            .OrderByDescending(g => g.Key)
            .Select(g => new CashFlowDayDto
            {
                Date = g.Key,
                Fii = Side(g.FirstOrDefault(r => r.Category.StartsWith("FII", StringComparison.OrdinalIgnoreCase))),
                Dii = Side(g.FirstOrDefault(r => r.Category.StartsWith("DII", StringComparison.OrdinalIgnoreCase))),
            })
            .ToList();

    private static CashFlowSideDto? Side(MarketCashFlow? row) =>
        row is null ? null : new CashFlowSideDto { Buy = row.BuyValueCrore, Sell = row.SellValueCrore, Net = row.NetValueCrore };

    /// <summary>Newest day first; each group's net change is against the previous stored day.</summary>
    public static List<ParticipantDayDto> ShapeParticipants(IEnumerable<MarketParticipantOpenInterest> rows)
    {
        var byDay = rows.Where(r => r.ClientType != "TOTAL")
            .GroupBy(r => r.Date)
            .OrderBy(g => g.Key)
            .ToList();

        var result = new List<ParticipantDayDto>();
        Dictionary<string, long>? previousNet = null;
        foreach (var day in byDay)
        {
            var net = new Dictionary<string, long>();
            var dto = new ParticipantDayDto { Date = day.Key };
            foreach (var type in ParticipantOrder)
            {
                var r = day.FirstOrDefault(x => x.ClientType == type);
                if (r is null) continue;
                long n = r.FutureIndexLong - r.FutureIndexShort;
                net[type] = n;
                long both = r.FutureIndexLong + r.FutureIndexShort;
                dto.Groups.Add(new ParticipantPositionDto
                {
                    ClientType = type,
                    FutureIndexLong = r.FutureIndexLong,
                    FutureIndexShort = r.FutureIndexShort,
                    FutureIndexNet = n,
                    FutureIndexLongPercent = both > 0 ? Math.Round((decimal)r.FutureIndexLong / both * 100m, 1) : null,
                    FutureIndexNetChange = previousNet is not null && previousNet.TryGetValue(type, out var p) ? n - p : null,
                    OptionIndexCallLong = r.OptionIndexCallLong,
                    OptionIndexCallShort = r.OptionIndexCallShort,
                    OptionIndexPutLong = r.OptionIndexPutLong,
                    OptionIndexPutShort = r.OptionIndexPutShort,
                    FutureStockLong = r.FutureStockLong,
                    FutureStockShort = r.FutureStockShort,
                });
            }

            result.Add(dto);
            previousNet = net;
        }

        result.Reverse();
        return result;
    }

    public static FuturesDayDto Day(MarketFuturesDaily r)
    {
        decimal? price = r.PreviousClose > 0 ? Math.Round((r.Close - r.PreviousClose) / r.PreviousClose * 100m, 2) : null;
        long before = r.OpenInterest - r.OpenInterestChange;
        decimal? oi = before > 0 ? Math.Round((decimal)r.OpenInterestChange / before * 100m, 2) : null;
        return new FuturesDayDto
        {
            Date = r.Date,
            Expiry = r.ExpiryDate,
            Close = r.Close,
            PreviousClose = r.PreviousClose,
            PriceChangePercent = price,
            OpenInterest = r.OpenInterest,
            OpenInterestChange = r.OpenInterestChange,
            OpenInterestChangePercent = oi,
            BuildUp = price is null || oi is null ? OiBuildup.Flat : OiBuildup.Classify(oi.Value, price.Value),
        };
    }

    public async Task<MarketFuturesResponse> FuturesAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 60);
        var nowUtc = DateTime.UtcNow;
        var today = IstTime.DateOf(nowUtc);

        var dates = await _db.MarketFuturesDaily.AsNoTracking()
            .Select(x => x.Date).Distinct().OrderByDescending(d => d).Take(days).ToListAsync(ct);
        var response = new MarketFuturesResponse { ServerUtc = nowUtc, LatestDay = dates.Count > 0 ? dates[0] : null, Status = Status() };
        if (dates.Count == 0) return response;

        var indexRows = await _db.MarketFuturesDaily.AsNoTracking()
            .Where(x => x.Date >= dates.Min() && x.InstrumentKind == "IDF" && IndexUnderlyings.Contains(x.Underlying))
            .ToListAsync(ct);

        foreach (var underlying in IndexUnderlyings)
        {
            // The nearest contract that had not expired before each day.
            var history = indexRows.Where(r => r.Underlying == underlying)
                .GroupBy(r => r.Date)
                .Select(g => g.Where(r => r.ExpiryDate >= g.Key).OrderBy(r => r.ExpiryDate).FirstOrDefault())
                .Where(r => r is not null)
                .OrderByDescending(r => r!.Date)
                .Select(r => Day(r!))
                .ToList();

            var dto = new IndexFuturesDto { Underlying = underlying, Latest = history.FirstOrDefault(), History = history };
            dto.Live = await LiveAsync(underlying, today, nowUtc, ct);
            response.Indices.Add(dto);
        }

        // Stocks: the nearest contract per underlying on the latest day.
        var latest = dates[0];
        var stocks = (await _db.MarketFuturesDaily.AsNoTracking()
                .Where(x => x.Date == latest && x.InstrumentKind == "STF")
                .ToListAsync(ct))
            .GroupBy(r => r.Underlying)
            .Select(g => g.OrderBy(r => r.ExpiryDate).First())
            .Select(r => (Row: r, Day: Day(r)))
            .ToList();

        foreach (var name in new[] { OiBuildup.LongBuildUp, OiBuildup.ShortBuildUp, OiBuildup.ShortCovering, OiBuildup.LongUnwinding })
        {
            var group = stocks.Where(s => s.Day.BuildUp == name).ToList();
            response.StockCounts[name] = group.Count;
            response.Stocks[name] = group
                .OrderByDescending(s => Math.Abs(s.Day.OpenInterestChangePercent ?? 0))
                .Take(10)
                .Select(s => new StockBuildUpDto
                {
                    Underlying = s.Row.Underlying,
                    Close = s.Row.Close,
                    PriceChangePercent = s.Day.PriceChangePercent,
                    OpenInterestChangePercent = s.Day.OpenInterestChangePercent,
                })
                .ToList();
        }

        return response;
    }

    /// <summary>
    /// An index future's symbol in the instrument master, e.g. NSE:NIFTY26SEPFUT.
    /// The master files NIFTY NEXT 50's future under the underlying "NIFTY" too,
    /// so the name itself is matched rather than the underlying column alone.
    /// </summary>
    public static bool IsIndexFutureOf(string symbol, string underlying) =>
        Regex.IsMatch(symbol, $"^NSE:{Regex.Escape(underlying)}\\d{{2}}[A-Z]{{3}}FUT$", RegexOptions.CultureInvariant);

    private async Task<FuturesLiveDto?> LiveAsync(string underlying, DateOnly today, DateTime nowUtc, CancellationToken ct)
    {
        var candidates = await _db.Instruments.AsNoTracking()
            .Where(i => i.Underlying == underlying && i.InstrumentType == "FUT" && i.ExpiryDate != null && i.ExpiryDate >= today)
            .OrderBy(i => i.ExpiryDate)
            .Select(i => new { i.Symbol, i.ExpiryDate })
            .Take(8)
            .ToListAsync(ct);
        var contract = candidates.FirstOrDefault(c => IsIndexFutureOf(c.Symbol, underlying));
        if (contract is null) return null;

        var quote = await _db.LiveQuotesLatest.AsNoTracking()
            .Where(q => q.Symbol == contract.Symbol)
            .OrderByDescending(q => q.UpdatedUtc)
            .FirstOrDefaultAsync(ct);
        if (quote is null) return null;

        var baseline = await _db.MarketFuturesDaily.AsNoTracking()
            .Where(x => x.Underlying == underlying && x.ExpiryDate == contract.ExpiryDate && x.Date < today)
            .OrderByDescending(x => x.Date)
            .FirstOrDefaultAsync(ct);

        var live = new FuturesLiveDto
        {
            Symbol = contract.Symbol,
            LastPrice = quote.LastTradedPrice,
            OpenInterest = quote.OpenInterest,
            AsOfUtc = quote.UpdatedUtc,
            SourceKey = quote.SourceKey,
            IsFresh = nowUtc - quote.UpdatedUtc <= FreshFor,
            BaselineDate = baseline?.Date,
        };

        if (baseline is not null && quote.LastTradedPrice is > 0 && baseline.Close > 0)
            live.PriceChangePercent = Math.Round((quote.LastTradedPrice.Value - baseline.Close) / baseline.Close * 100m, 2);
        if (baseline is not null && quote.OpenInterest is > 0 && baseline.OpenInterest > 0)
        {
            live.OpenInterestChange = quote.OpenInterest.Value - baseline.OpenInterest;
            live.OpenInterestChangePercent = Math.Round((decimal)live.OpenInterestChange.Value / baseline.OpenInterest * 100m, 2);
        }
        if (live.PriceChangePercent is not null && live.OpenInterestChangePercent is not null)
            live.BuildUp = OiBuildup.Classify(live.OpenInterestChangePercent.Value, live.PriceChangePercent.Value);

        return live;
    }

    /// <summary>NSE's NIFTY future of one expiry at its newest stored close.</summary>
    public async Task<(decimal? Close, DateOnly? Date)> NseFutureCloseAsync(DateOnly expiry, CancellationToken ct)
    {
        var row = await _db.MarketFuturesDaily.AsNoTracking()
            .Where(x => x.Underlying == "NIFTY" && x.InstrumentKind == "IDF" && x.ExpiryDate == expiry)
            .OrderByDescending(x => x.Date)
            .FirstOrDefaultAsync(ct);
        return row is null ? (null, null) : (row.Close, row.Date);
    }

    public async Task<MarketEventsResponse> EventsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var events = await _db.MarketEvents.AsNoTracking().Where(e => e.Date >= from && e.Date <= to).ToListAsync(ct);
        var holidays = await _db.MarketHolidays.AsNoTracking()
            .Where(h => h.Exchange == "NSE" && h.Date >= from && h.Date <= to && h.Closure == MarketClosure.FullDay)
            .ToListAsync(ct);

        // Expiries of the weekly index options still listed in the master.
        var expiries = await _db.Instruments.AsNoTracking()
            .Where(i => (i.Underlying == "NIFTY" || i.Underlying == "SENSEX") && i.ExpiryDate != null
                        && i.ExpiryDate >= from && i.ExpiryDate <= to && (i.InstrumentType == "CE" || i.InstrumentType == "PE"))
            .Select(i => new { i.Underlying, NextFifty = i.Symbol.Contains("NIFTYNXT"), i.ExpiryDate })
            .Distinct()
            .ToListAsync(ct);

        var list = events.Select(e => new MarketEventDto
        {
            Id = e.Id,
            Date = e.Date,
            TimeIst = e.TimeIst?.ToString("HH:mm", CultureInfo.InvariantCulture),
            Region = e.Region,
            Category = e.Category,
            Title = e.Title,
            Importance = e.Importance,
            Notes = e.Notes,
            Source = e.Source,
            Kind = "event",
        }).ToList();

        list.AddRange(holidays.Select(h => new MarketEventDto
        {
            Date = h.Date,
            Region = "IN",
            Category = "Holiday",
            Title = $"NSE closed: {h.Name}",
            Importance = 1,
            Source = h.Source,
            Kind = "holiday",
        }));

        foreach (var group in expiries
                     .Where(x => !x.NextFifty)
                     .GroupBy(x => (x.Underlying, x.ExpiryDate!.Value)))
        {
            list.Add(new MarketEventDto
            {
                Date = group.Key.Value,
                TimeIst = "15:30",
                Region = "IN",
                Category = "Expiry",
                Title = $"{group.Key.Underlying} options expiry",
                Importance = 1,
                Kind = "expiry",
            });
        }

        return new MarketEventsResponse
        {
            ServerUtc = DateTime.UtcNow,
            From = from,
            To = to,
            Events = list.OrderBy(e => e.Date).ThenBy(e => e.TimeIst ?? "99:99").ThenByDescending(e => e.Importance).ToList(),
        };
    }
}
