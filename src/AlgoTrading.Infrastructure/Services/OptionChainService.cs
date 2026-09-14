// src/AlgoTrading.Infrastructure/Services/OptionChainService.cs
using AlgoTrading.Application.Interfaces;
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
    private readonly IMarketSessionService? _sessions;
    private readonly ILotSizeResolver? _lotSizes;

    /// <param name="sessions">Needed only by the live view, for the market's open/closed state.</param>
    /// <param name="lotSizes">Needed only by the live view, for the per-lot premium.</param>
    public OptionChainService(
        TradingDbContext dbContext,
        IMarketSessionService? sessions = null,
        ILotSizeResolver? lotSizes = null)
    {
        _dbContext = dbContext;
        _sessions = sessions;
        _lotSizes = lotSizes;
    }

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
                PriceChange = row.PriceChange,
                BidPrice = row.BidPrice,
                AskPrice = row.AskPrice,
                Volume = row.Volume,
                OpenInterest = row.OpenInterest,
                // The broker's previous-day close is the market's own baseline
                // and does not depend on when this poller started. Only when it
                // is absent does the session's first reading stand in.
                OpenInterestAtOpen = row.PreviousDayOpenInterest ?? atOpen ?? row.OpenInterest,
                ImpliedVolatility = row.ImpliedVolatility,
                Delta = row.Delta,
                Gamma = row.Gamma,
                Theta = row.Theta,
                Vega = row.Vega,
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
        => (await ReadChainAsync(underlying, expiry, asOfUtc, cancellationToken)).Chain;

    /// <summary>The chain, and which source recorded the capture it was read from.</summary>
    private async Task<(OptionChainResponse Chain, string? SourceKey)> ReadChainAsync(
        string underlying,
        DateOnly? expiry,
        DateTime? asOfUtc,
        CancellationToken cancellationToken)
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
            return (response, null);
        }

        var rows = await query
            .Where(x => x.CapturedUtc == momentUtc)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            response.OpenInterestUnavailable = true;
            return (response, null);
        }

        // What each contract cost when the session opened.
        //
        // Open interest already carries its own opening value on every row, but
        // price does not — and the build-up reading is meaningless unless both
        // changes are measured from the same instant. Comparing an OI change
        // since 09:15 against a price change since the previous poll would
        // label strikes almost at random.
        //
        // Only needed for rows without the broker's own day change, and it scans
        // the whole session — so it is not paid for when every row carries one
        // (every Dhan capture does). The live view reads this every few seconds.
        var openingPrice = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        if (rows.Any(x => x.PriceChange is null))
        {
            var sessionOpenUtc = momentUtc.Value.Date;
            openingPrice = await _dbContext.OptionChainSnapshots
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
        }

        response.ExpiryDate = rows[0].ExpiryDate;
        response.SpotPrice = rows[0].SpotPrice;

        foreach (var group in rows.GroupBy(x => x.StrikePrice).OrderBy(g => g.Key))
        {
            var call = group.FirstOrDefault(x => x.OptionType == "CE");
            var put = group.FirstOrDefault(x => x.OptionType == "PE");

            response.Strikes.Add(new OptionChainStrikeResponse
            {
                StrikePrice = group.Key,
                Call = ToLeg(call, Opening(call)),
                Put = ToLeg(put, Opening(put)),
            });
        }

        // Totals, ratios, max pain, heaviest strikes and the ATM row: one
        // implementation, shared with the live view, which re-runs it after
        // bringing strikes up to the second.
        OptionChainLiveView.Summarise(response);

        // Said plainly rather than implied by a column of zeroes: for any period
        // before the poller existed there is price and volume but no OI.
        response.OpenInterestUnavailable = rows.All(x => x.OpenInterest is null or 0);

        return (response, rows[0].SourceKey);

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
            OpenInterestBaseline = row.OpenInterestAtOpen,
            OpenInterestChangePercent = row.OpenInterestAtOpen is > 0 && change is not null
                ? OptionChainAnalytics.ChangePercent(row.OpenInterest ?? 0, row.OpenInterestAtOpen.Value)
                : null,
            ImpliedVolatility = row.ImpliedVolatility,
            Delta = row.Delta,
            Gamma = row.Gamma,
            Theta = row.Theta,
            Vega = row.Vega,
        };

        // The broker's own day-change when it sent one: it is measured from the
        // previous close, like the open-interest baseline, and it is right from
        // the poller's first round. Falling back to "since our first snapshot"
        // only when it is absent.
        if (row.PriceChange is not null)
        {
            leg.PriceChange = row.PriceChange;
            decimal? previousClose = row.LastTradedPrice - row.PriceChange;
            leg.PriceChangePercent = previousClose > 0
                ? row.PriceChange / previousClose * 100m
                : null;
        }
        else if (row.LastTradedPrice is not null && priceAtOpen is not null)
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
    // Read: the live view
    // ------------------------------------------------------------------

    public const string VixSymbol = "NSE:INDIAVIX-INDEX";

    /// <summary>
    /// The chain as a trader reads it: the newest capture brought up to the
    /// second from fresh live quotes, with the header strip — spot, future, VIX,
    /// support and resistance, totals, lot size and whether the market is open.
    /// </summary>
    /// <param name="asOfUtc">
    /// The replay clock. When set, snapshots only: the live quote table
    /// describes now, not the moment being replayed.
    /// </param>
    public async Task<OptionChainResponse> GetViewAsync(
        string underlying,
        DateOnly? expiry,
        DateTime? asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        bool replay = asOfUtc is not null;
        var (chain, snapshotSource) = await ReadChainAsync(underlying, expiry, asOfUtc, cancellationToken);
        string key = chain.Underlying;
        bool captured = chain.Strikes.Count > 0;
        DateTime? snapshotUtc = captured ? chain.AsOfUtc : null;

        string exchange = ExchangeOf(key, chain);
        bool marketOpen = IsOpen(nowUtc, exchange, exchange == "MCX" ? "COM" : "FO");
        bool commodity = exchange == "MCX";
        var today = IstTime.DateOf(replay ? asOfUtc!.Value : nowUtc);

        // The spot: the index, or for MCX the future the options are written on
        // (the nearest one expiring on or after the options). An index's
        // "future" is its own nearest contract.
        string spotSymbol = commodity
            ? await NearestFutureAsync(key, exchange, captured ? chain.ExpiryDate : today, cancellationToken) ?? string.Empty
            : UnderlyingCatalog.SpotSymbolFor(key);
        string? futureSymbol = commodity ? null : await NearestFutureAsync(key, exchange, today, cancellationToken);
        DateOnly? futureExpiry = null;

        OptionChainQuoteResponse? spot;
        OptionChainQuoteResponse? futureQuote = null;
        OptionChainQuoteResponse? vix = null;
        var overlay = new OptionChainLiveView.OverlayResult(0, chain.Strikes.Sum(s => (s.Call is null ? 0 : 1) + (s.Put is null ? 0 : 1)), null, null);

        if (replay)
        {
            spot = captured ? OptionChainLiveView.SnapshotSpot(spotSymbol, chain.SpotPrice, chain.AsOfUtc, snapshotSource) : null;

            // MCX: the capture's spot is not the future (see the live branch), so
            // a replay reads the future's own one-minute bar at that moment when
            // the feed recorded one.
            if (commodity && captured && !string.IsNullOrEmpty(spotSymbol))
            {
                var from = chain.AsOfUtc.AddMinutes(-10);
                var bar = await _dbContext.LiveBars.AsNoTracking()
                    .Where(b => b.Symbol == spotSymbol && b.Resolution == "1m" && b.BarStartUtc >= from && b.BarStartUtc <= chain.AsOfUtc)
                    .OrderByDescending(b => b.BarStartUtc)
                    .Select(b => new { b.BarStartUtc, b.Close, b.SourceKey })
                    .FirstOrDefaultAsync(cancellationToken);
                if (bar is not null && bar.Close > 0)
                {
                    spot = new OptionChainQuoteResponse
                    {
                        Symbol = spotSymbol,
                        LastPrice = bar.Close,
                        AsOfUtc = bar.BarStartUtc.AddMinutes(1),
                        SourceKey = bar.SourceKey,
                        Basis = "bar",
                    };
                }
            }

            if (spot?.LastPrice is > 0 && captured) chain.SpotPrice = spot.LastPrice.Value;
        }
        else
        {
            var headerSymbols = new[] { spotSymbol, futureSymbol, VixSymbol }
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var headerQuotes = await QuotesAsync(headerSymbols, withPacketClose: true, cancellationToken);

            if (captured)
            {
                var legSymbols = chain.Strikes
                    .SelectMany(s => new[] { s.Call?.Symbol, s.Put?.Symbol })
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Select(s => s!)
                    .ToList();
                var legQuotes = await QuotesAsync(legSymbols, withPacketClose: false, cancellationToken);
                overlay = OptionChainLiveView.Overlay(chain, legQuotes, chain.AsOfUtc, nowUtc);
            }

            var spotQuote = OptionChainLiveView.QuoteOf(headerQuotes.GetValueOrDefault(spotSymbol), nowUtc, marketOpen);
            // For MCX the future's own quote, however old, beats the capture's
            // spot: Dhan's chain reports an underlying price for commodities that
            // is not the future the options are written on (14 Sep: 9,577 against
            // a future at 9,971, which put-call parity on the same chain agreed
            // with). An old price of the right contract, labelled with its time,
            // is the lesser evil.
            spot = commodity && spotQuote is not null && (spotQuote.IsLive || snapshotUtc is null || IstTime.DateOf(spotQuote.AsOfUtc!.Value) == IstTime.DateOf(snapshotUtc.Value))
                ? spotQuote
                : OptionChainLiveView.ChooseSpot(spotQuote, spotSymbol, chain.SpotPrice, snapshotUtc, snapshotSource);
            if (futureSymbol is not null)
                futureQuote = OptionChainLiveView.QuoteOf(headerQuotes.GetValueOrDefault(futureSymbol), nowUtc, marketOpen);
            vix = OptionChainLiveView.QuoteOf(headerQuotes.GetValueOrDefault(VixSymbol), nowUtc, IsOpen(nowUtc, "NSE", "CM"));

            // The chain is read against the spot as it is now, not as it was at
            // the last capture: ATM, max pain's neighbourhood and the spot line
            // all move with it.
            if (spot?.LastPrice is > 0 && captured) chain.SpotPrice = spot.LastPrice.Value;
        }

        if (futureSymbol is not null && futureQuote is not null)
        {
            futureExpiry = await _dbContext.Instruments.AsNoTracking()
                .Where(i => i.Symbol == futureSymbol)
                .Select(i => i.ExpiryDate)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (captured) OptionChainLiveView.Summarise(chain);

        var header = OptionChainLiveView.Header(chain, replay ? chain.AsOfUtc : nowUtc, nowUtc);
        header.Exchange = exchange;
        header.MarketOpen = !replay && marketOpen;
        header.Spot = spot;
        header.SpotIsFuture = commodity;
        header.Future = OptionChainLiveView.FutureOf(futureQuote, futureExpiry, spot?.LastPrice);
        header.Vix = vix;
        header.SnapshotCapturedUtc = snapshotUtc;
        header.SnapshotSourceKey = snapshotSource;
        header.LiveLegs = overlay.LiveLegs;
        header.TotalLegs = overlay.TotalLegs;

        var liveTimes = new List<(DateTime Utc, string? Source)>();
        if (overlay.NewestUtc is { } legUtc) liveTimes.Add((legUtc, overlay.SourceKey));
        if (spot is { IsLive: true, AsOfUtc: { } spotUtc }) liveTimes.Add((spotUtc, spot.SourceKey));
        if (liveTimes.Count > 0)
        {
            var newest = liveTimes.MaxBy(x => x.Utc);
            header.LiveOverlayUtc = newest.Utc;
            header.LiveSourceKey = newest.Source;
        }

        header.Mode = replay ? "replay" : liveTimes.Count > 0 ? "live" : "snapshot";

        if (_lotSizes is not null)
        {
            var lot = await _lotSizes.ResolveForUnderlyingAsync(key, cancellationToken);
            header.LotSize = lot.Source == LotSizeInfo.SourceUnknown ? null : lot.LotSize;
            header.LotSizeSource = lot.Source;
        }

        chain.Header = header;
        return chain;
    }

    /// <summary>
    /// The chain's whole session, one point per capture: spot, total OI each
    /// side and the PCR. At most <paramref name="maxPoints"/>, thinned evenly
    /// with the newest always kept.
    /// </summary>
    public async Task<OptionChainTrendResponse> GetTrendAsync(
        string underlying,
        DateOnly? expiry,
        DateTime? toUtc,
        int maxPoints = 400,
        CancellationToken cancellationToken = default)
    {
        string key = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        var response = new OptionChainTrendResponse { Underlying = key };

        var query = _dbContext.OptionChainSnapshots.AsNoTracking().Where(x => x.Underlying == key);
        if (expiry is not null) query = query.Where(x => x.ExpiryDate == expiry);
        if (toUtc is not null) query = query.Where(x => x.CapturedUtc <= toUtc);

        var newest = await query
            .OrderByDescending(x => x.CapturedUtc)
            .Select(x => new { x.CapturedUtc, x.ExpiryDate })
            .FirstOrDefaultAsync(cancellationToken);
        if (newest is null) return response;

        var day = IstTime.DateOf(newest.CapturedUtc);
        var fromUtc = IstTime.StartOfDayUtc(day);
        var untilUtc = newest.CapturedUtc;
        var expiryDate = expiry ?? newest.ExpiryDate;

        response.ExpiryDate = expiryDate;
        response.SessionDate = day;

        var points = await _dbContext.OptionChainSnapshots.AsNoTracking()
            .Where(x => x.Underlying == key && x.ExpiryDate == expiryDate
                        && x.CapturedUtc >= fromUtc && x.CapturedUtc <= untilUtc)
            .GroupBy(x => x.CapturedUtc)
            .Select(g => new OptionChainTrendPointResponse
            {
                CapturedUtc = g.Key,
                SpotPrice = g.Max(x => x.SpotPrice),
                CallOpenInterest = g.Sum(x => x.OptionType == "CE" ? (x.OpenInterest ?? 0L) : 0L),
                PutOpenInterest = g.Sum(x => x.OptionType == "PE" ? (x.OpenInterest ?? 0L) : 0L),
                CallOpenInterestChange = g.Sum(x => x.OptionType == "CE" && x.OpenInterest != null && x.OpenInterestAtOpen != null
                    ? x.OpenInterest!.Value - x.OpenInterestAtOpen!.Value : 0L),
                PutOpenInterestChange = g.Sum(x => x.OptionType == "PE" && x.OpenInterest != null && x.OpenInterestAtOpen != null
                    ? x.OpenInterest!.Value - x.OpenInterestAtOpen!.Value : 0L),
            })
            .OrderBy(x => x.CapturedUtc)
            .ToListAsync(cancellationToken);

        response.Captures = points.Count;
        foreach (var point in Thin(points, maxPoints))
        {
            point.PutCallRatio = OptionChainAnalytics.PutCallRatio(point.PutOpenInterest, point.CallOpenInterest);
            response.Points.Add(point);
        }

        return response;
    }

    /// <summary>Every n-th item so at most <paramref name="max"/> remain, always ending on the last.</summary>
    public static IReadOnlyList<T> Thin<T>(IReadOnlyList<T> items, int max)
    {
        if (max <= 1 || items.Count <= max) return items;
        int stride = (int)Math.Ceiling(items.Count / (double)(max - 1));
        var kept = new List<T>(max);
        for (int i = 0; i < items.Count; i += stride) kept.Add(items[i]);
        if (!ReferenceEquals(kept[^1], items[^1])) kept.Add(items[^1]);
        return kept;
    }

    /// <summary>Live quotes by symbol, the raw payload's close parsed only where asked for.</summary>
    private async Task<Dictionary<string, LiveQuoteSample>> QuotesAsync(
        IReadOnlyCollection<string> symbols,
        bool withPacketClose,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LiveQuoteSample>(StringComparer.Ordinal);
        if (symbols.Count == 0) return result;

        if (withPacketClose)
        {
            var rows = await _dbContext.LiveQuotesLatest.AsNoTracking()
                .Where(x => symbols.Contains(x.Symbol))
                .Select(x => new { x.Symbol, x.LastTradedPrice, x.BidPrice, x.AskPrice, x.Volume, x.OpenInterest, x.Close, x.UpdatedUtc, x.SourceKey, x.RawPayload })
                .ToListAsync(cancellationToken);
            foreach (var r in rows)
            {
                result[r.Symbol] = new LiveQuoteSample(r.Symbol, r.LastTradedPrice, r.BidPrice, r.AskPrice, r.Volume, r.OpenInterest,
                    r.Close, OptionChainLiveView.PacketCloseOf(r.RawPayload), DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc), r.SourceKey);
            }
        }
        else
        {
            var rows = await _dbContext.LiveQuotesLatest.AsNoTracking()
                .Where(x => symbols.Contains(x.Symbol))
                .Select(x => new { x.Symbol, x.LastTradedPrice, x.BidPrice, x.AskPrice, x.Volume, x.OpenInterest, x.Close, x.UpdatedUtc, x.SourceKey })
                .ToListAsync(cancellationToken);
            foreach (var r in rows)
            {
                result[r.Symbol] = new LiveQuoteSample(r.Symbol, r.LastTradedPrice, r.BidPrice, r.AskPrice, r.Volume, r.OpenInterest,
                    r.Close, null, DateTime.SpecifyKind(r.UpdatedUtc, DateTimeKind.Utc), r.SourceKey);
            }
        }

        return result;
    }

    /// <summary>Whether an exchange is trading; false when there are no rules for it rather than a thrown request.</summary>
    private bool IsOpen(DateTime nowUtc, string exchange, string segment)
    {
        if (_sessions is null) return false;
        try
        {
            return _sessions.IsMarketOpen(nowUtc, exchange, segment);
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The nearest futures contract of an underlying expiring on or after a day.</summary>
    private Task<string?> NearestFutureAsync(string underlying, string exchange, DateOnly onOrAfter, CancellationToken cancellationToken)
        => _dbContext.Instruments.AsNoTracking()
            .Where(i => i.Underlying == underlying && i.Exchange == exchange && i.InstrumentType == "FUT"
                        && i.ExpiryDate != null && i.ExpiryDate >= onOrAfter)
            .OrderBy(i => i.ExpiryDate)
            .Select(i => (string?)i.Symbol)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The exchange whose hours gate the chain: from a captured contract when there is one.</summary>
    public static string ExchangeOf(string underlying, OptionChainResponse chain)
    {
        var symbol = chain.Strikes.Select(s => s.Call?.Symbol ?? s.Put?.Symbol).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        if (symbol is not null && symbol.IndexOf(':') is > 0 and var colon) return symbol[..colon].ToUpperInvariant();
        if (UnderlyingCatalog.IsCommodity(underlying)) return "MCX";
        var spot = UnderlyingCatalog.SpotSymbolFor(underlying);
        return spot.IndexOf(':') is > 0 and var c ? spot[..c] : "NSE";
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

    /// <summary>
    /// When the chain was last captured, for any underlying.
    /// </summary>
    /// <remarks>
    /// Reported next to the poller's process status because the two are not the
    /// same thing: a poller that started but cannot reach the broker is up,
    /// healthy-looking, and recording nothing.
    /// </remarks>
    public async Task<DateTime?> GetLastCaptureUtcAsync(CancellationToken cancellationToken = default)
        => await _dbContext.OptionChainSnapshots
            .AsNoTracking()
            .OrderByDescending(x => x.CapturedUtc)
            .Select(x => (DateTime?)x.CapturedUtc)
            .FirstOrDefaultAsync(cancellationToken);

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
