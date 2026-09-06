// src/AlgoTrading.Infrastructure/Services/OptionChainService.cs
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Records the option chain, and reads it back as at any moment.
/// </summary>
/// <remarks>
/// Live and replay are the same query with a different clock. That is the whole
/// point of storing a time series rather than a current-value cache: the chain
/// a backtest shows at 10:15 is the chain that was true at 10:15, read the same
/// way the live screen reads "now".
/// </remarks>
public class OptionChainService
{
    private readonly TradingDbContext _dbContext;

    public OptionChainService(TradingDbContext dbContext) => _dbContext = dbContext;

    // ------------------------------------------------------------------
    // Write
    // ------------------------------------------------------------------

    /// <summary>Stores one poll's worth of strikes, all stamped the same moment.</summary>
    public async Task<int> StoreAsync(
        IReadOnlyList<OptionChainSnapshotRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows is null || rows.Count == 0) return 0;

        // One capture time for the batch. The strikes were read together and a
        // chart that spreads them across a few seconds would show open interest
        // stepping strike by strike, which never happens.
        var capturedUtc = Truncate(DateTime.UtcNow);
        var sessionStart = capturedUtc.Date;

        var symbols = rows.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();

        // What each contract's open interest was when the session began. Carried
        // forward rather than recomputed, because "OI change" means change since
        // the open and finding that row per strike per read would be a scan.
        var openingInterest = await _dbContext.OptionChainSnapshots
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol) && x.CapturedUtc >= sessionStart)
            .GroupBy(x => x.Symbol)
            .Select(g => new { Symbol = g.Key, AtOpen = g.Min(x => x.OpenInterestAtOpen) })
            .ToDictionaryAsync(x => x.Symbol, x => x.AtOpen, StringComparer.Ordinal, cancellationToken);

        // A retry re-sends the same batch; the unique index would reject it, so
        // the duplicates are dropped here rather than thrown.
        var already = await _dbContext.OptionChainSnapshots
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol) && x.CapturedUtc == capturedUtc)
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);

        var seen = new HashSet<string>(already, StringComparer.Ordinal);
        var batch = new List<OptionChainSnapshot>(rows.Count);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Symbol) || !seen.Add(row.Symbol)) continue;

            openingInterest.TryGetValue(row.Symbol, out var atOpen);

            batch.Add(new OptionChainSnapshot
            {
                Underlying = row.Underlying.Trim().ToUpperInvariant(),
                ExpiryDate = row.ExpiryDate,
                StrikePrice = row.StrikePrice,
                OptionType = row.OptionType.Trim().ToUpperInvariant(),
                Symbol = row.Symbol,
                CapturedUtc = capturedUtc,
                SpotPrice = row.SpotPrice,
                LastTradedPrice = row.LastTradedPrice,
                BidPrice = row.BidPrice,
                AskPrice = row.AskPrice,
                Volume = row.Volume,
                OpenInterest = row.OpenInterest,
                // First sighting today: this reading IS the open.
                OpenInterestAtOpen = atOpen ?? row.OpenInterest,
                SourceKey = string.IsNullOrWhiteSpace(row.SourceKey) ? "fyers" : row.SourceKey!,
            });
        }

        if (batch.Count == 0) return 0;

        await _dbContext.OptionChainSnapshots.AddRangeAsync(batch, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return batch.Count;
    }

    // ------------------------------------------------------------------
    // Read: the chain
    // ------------------------------------------------------------------

    /// <summary>
    /// The chain as at <paramref name="asOfUtc"/>, or the newest one when null.
    /// </summary>
    public async Task<OptionChainResponse> GetChainAsync(
        string underlying,
        DateOnly? expiry,
        DateTime? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        string key = (underlying ?? string.Empty).Trim().ToUpperInvariant();

        var query = _dbContext.OptionChainSnapshots.AsNoTracking().Where(x => x.Underlying == key);
        if (expiry is not null) query = query.Where(x => x.ExpiryDate == expiry);
        if (asOfUtc is not null) query = query.Where(x => x.CapturedUtc <= asOfUtc);

        // The moment to describe: the most recent capture at or before the clock.
        var momentUtc = await query
            .OrderByDescending(x => x.CapturedUtc)
            .Select(x => (DateTime?)x.CapturedUtc)
            .FirstOrDefaultAsync(cancellationToken);

        var response = new OptionChainResponse
        {
            Underlying = key,
            AsOfUtc = momentUtc ?? asOfUtc ?? DateTime.UtcNow,
        };

        if (momentUtc is null)
        {
            response.OpenInterestUnavailable = true;
            return response;
        }

        var rows = await query
            .Where(x => x.CapturedUtc == momentUtc)
            .ToListAsync(cancellationToken);

        // What each contract cost when the session opened.
        //
        // Open interest already carries its own opening value on every row, but
        // price does not — and the build-up reading is meaningless unless both
        // changes are measured from the same instant. Comparing an OI change
        // since 09:15 against a price change since the previous poll would
        // label strikes almost at random.
        var sessionOpenUtc = momentUtc.Value.Date;
        var openingPrice = await _dbContext.OptionChainSnapshots
            .AsNoTracking()
            .Where(x => x.Underlying == key
                        && x.CapturedUtc >= sessionOpenUtc
                        && x.CapturedUtc <= momentUtc)
            .GroupBy(x => x.Symbol)
            .Select(g => new
            {
                Symbol = g.Key,
                Open = g.OrderBy(x => x.CapturedUtc).Select(x => x.LastTradedPrice).FirstOrDefault(),
            })
            .ToDictionaryAsync(x => x.Symbol, x => x.Open, StringComparer.Ordinal, cancellationToken);

        if (rows.Count == 0)
        {
            response.OpenInterestUnavailable = true;
            return response;
        }

        response.ExpiryDate = rows[0].ExpiryDate;
        response.SpotPrice = rows[0].SpotPrice;

        var byStrike = rows.GroupBy(x => x.StrikePrice).OrderBy(g => g.Key);
        var interests = new List<OptionChainAnalytics.StrikeInterest>();

        foreach (var group in byStrike)
        {
            var call = group.FirstOrDefault(x => x.OptionType == "CE");
            var put = group.FirstOrDefault(x => x.OptionType == "PE");

            var strike = new OptionChainStrikeResponse
            {
                StrikePrice = group.Key,
                Call = ToLeg(call, Opening(call)),
                Put = ToLeg(put, Opening(put)),
            };

            long callOi = call?.OpenInterest ?? 0;
            long putOi = put?.OpenInterest ?? 0;

            strike.PutCallRatio = OptionChainAnalytics.PutCallRatio(putOi, callOi);
            strike.PutCallRatioOfChange = OptionChainAnalytics.PutCallRatio(
                Math.Abs(strike.Put?.OpenInterestChange ?? 0),
                Math.Abs(strike.Call?.OpenInterestChange ?? 0));

            response.Strikes.Add(strike);
            interests.Add(new OptionChainAnalytics.StrikeInterest(group.Key, callOi, putOi));

            response.TotalCallOpenInterest += callOi;
            response.TotalPutOpenInterest += putOi;
        }

        response.AtTheMoneyStrike = OptionChainAnalytics.AtTheMoney(
            response.Strikes.Select(x => x.StrikePrice).ToList(), response.SpotPrice);

        foreach (var strike in response.Strikes)
            strike.IsAtTheMoney = strike.StrikePrice == response.AtTheMoneyStrike;

        response.MaxPainStrike = OptionChainAnalytics.MaxPain(interests);
        response.HeaviestCallStrike = OptionChainAnalytics.HeaviestStrike(interests, "CE");
        response.HeaviestPutStrike = OptionChainAnalytics.HeaviestStrike(interests, "PE");
        response.PutCallRatio = OptionChainAnalytics.PutCallRatio(
            response.TotalPutOpenInterest, response.TotalCallOpenInterest);

        // Said plainly rather than implied by a column of zeroes: for any period
        // before the poller existed there is price and volume but no OI.
        response.OpenInterestUnavailable = rows.All(x => x.OpenInterest is null or 0);

        return response;

        decimal? Opening(OptionChainSnapshot? row)
            => row is not null && openingPrice.TryGetValue(row.Symbol, out var open) ? open : null;
    }

    private static OptionChainLegResponse? ToLeg(OptionChainSnapshot? row, decimal? priceAtOpen)
    {
        if (row is null) return null;

        long? change = row.OpenInterest is not null && row.OpenInterestAtOpen is not null
            ? row.OpenInterest - row.OpenInterestAtOpen
            : null;

        var leg = new OptionChainLegResponse
        {
            Symbol = row.Symbol,
            LastTradedPrice = row.LastTradedPrice,
            BidPrice = row.BidPrice,
            AskPrice = row.AskPrice,
            Volume = row.Volume,
            OpenInterest = row.OpenInterest,
            OpenInterestChange = change,
            OpenInterestChangePercent = row.OpenInterestAtOpen is > 0 && change is not null
                ? OptionChainAnalytics.ChangePercent(row.OpenInterest ?? 0, row.OpenInterestAtOpen.Value)
                : null,
            ImpliedVolatility = row.ImpliedVolatility,
            Delta = row.Delta,
        };

        if (row.LastTradedPrice is not null && priceAtOpen is not null)
        {
            leg.PriceChange = row.LastTradedPrice - priceAtOpen;
            leg.PriceChangePercent = priceAtOpen > 0
                ? (row.LastTradedPrice - priceAtOpen) / priceAtOpen * 100m
                : null;
        }

        // What the two directions say together. Both measured from the session
        // open, so the reading is about the day rather than about the last poll.
        leg.BuildUp = OptionChainAnalytics
            .Classify(leg.PriceChange ?? 0m, change ?? 0)
            .ToString();

        return leg;
    }

    // ------------------------------------------------------------------
    // Read: one strike's day
    // ------------------------------------------------------------------

    /// <summary>Every snapshot of one strike between two moments, oldest first.</summary>
    public async Task<OptionChainSeriesResponse> GetSeriesAsync(
        string underlying,
        DateOnly? expiry,
        decimal strike,
        DateTime? fromUtc,
        DateTime? toUtc,
        CancellationToken cancellationToken = default)
    {
        string key = (underlying ?? string.Empty).Trim().ToUpperInvariant();

        var query = _dbContext.OptionChainSnapshots.AsNoTracking()
            .Where(x => x.Underlying == key && x.StrikePrice == strike);

        if (expiry is not null) query = query.Where(x => x.ExpiryDate == expiry);
        if (fromUtc is not null) query = query.Where(x => x.CapturedUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(x => x.CapturedUtc <= toUtc);

        var rows = await query.OrderBy(x => x.CapturedUtc).ToListAsync(cancellationToken);

        var response = new OptionChainSeriesResponse
        {
            Underlying = key,
            StrikePrice = strike,
            ExpiryDate = expiry ?? (rows.Count > 0 ? rows[0].ExpiryDate : default),
            OpenInterestUnavailable = rows.Count == 0 || rows.All(x => x.OpenInterest is null or 0),
        };

        foreach (var group in rows.GroupBy(x => x.CapturedUtc).OrderBy(g => g.Key))
        {
            var call = group.FirstOrDefault(x => x.OptionType == "CE");
            var put = group.FirstOrDefault(x => x.OptionType == "PE");

            long? callChange = Change(call);
            long? putChange = Change(put);

            response.Points.Add(new OptionChainSeriesPointResponse
            {
                CapturedUtc = group.Key,
                SpotPrice = group.First().SpotPrice,
                CallLastTradedPrice = call?.LastTradedPrice,
                PutLastTradedPrice = put?.LastTradedPrice,
                CallOpenInterest = call?.OpenInterest,
                PutOpenInterest = put?.OpenInterest,
                CallOpenInterestChange = callChange,
                PutOpenInterestChange = putChange,
                // The blue line on the multi-OI page: how much more (or less)
                // is being written on the put side than the call side.
                OpenInterestChangeDifference = putChange is not null && callChange is not null
                    ? putChange - callChange
                    : null,
                CallVolume = call?.Volume,
                PutVolume = put?.Volume,
            });
        }

        return response;

        static long? Change(OptionChainSnapshot? row)
            => row?.OpenInterest is not null && row.OpenInterestAtOpen is not null
                ? row.OpenInterest - row.OpenInterestAtOpen
                : null;
    }

    /// <summary>Which expiries have been captured for an underlying.</summary>
    public Task<List<DateOnly>> GetExpiriesAsync(string underlying, CancellationToken cancellationToken = default)
    {
        string key = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        return _dbContext.OptionChainSnapshots.AsNoTracking()
            .Where(x => x.Underlying == key)
            .Select(x => x.ExpiryDate)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Snapshots are stamped to the second; a chain is not a tick.</summary>
    private static DateTime Truncate(DateTime value)
        => new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
