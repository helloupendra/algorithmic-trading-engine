// src/AlgoTrading.Api/Services/PositionGreeks.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.OptionHistory;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The rules for an open leg's greeks: which source wins, when a figure is
/// stale, and what it is worth to the position in rupees.
/// </summary>
/// <remarks>
/// <para>
/// 27 Sep, the owner: "if I have bought or sold an option, the Greeks' effect
/// should show too — like theta shows when you buy". The platform already had
/// greeks in two places and used neither for a position: the feed's enricher
/// writes them onto an option's live quote, and the chain recorder stores them
/// in every snapshot. A third source covers what those two miss (an MCX
/// option — the enricher knows no commodity spot — or a strike the chain
/// recorder does not follow): Black-Scholes with the IV solved from the
/// option's own last price.
/// </para>
/// <para>
/// Order: the live quote's greeks if fresh; else the chain snapshot's if fresh;
/// else computed; else the live quote's even though stale. Every result says
/// where it came from and how old it is, and anything older than
/// <see cref="FreshFor"/> is flagged: greeks from yesterday's last trade are
/// shown as yesterday's, never as now.
/// </para>
/// </remarks>
public static class PositionGreeks
{
    /// <summary>How old a figure can be and still be read as the market now.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    public const string SourceFeed = "feed";
    public const string SourceChain = "chain";
    public const string SourceComputed = "computed";
    public const string SourceDeltaOne = "delta-one";

    /// <summary>One source's per-unit figures for a leg.</summary>
    public sealed record Figures(
        string Source,
        DateTime AsOfUtc,
        decimal? IvPercent,
        decimal Delta,
        decimal Gamma,
        decimal Theta,
        decimal Vega,
        decimal? UnderlyingPrice = null);

    public static bool IsFresh(Figures figures, DateTime nowUtc) => nowUtc - figures.AsOfUtc <= FreshFor;

    /// <summary>
    /// Picks a leg's figures: fresh feed, fresh chain, computed, stale feed.
    /// <paramref name="compute"/> runs only when neither fresh source answers.
    /// </summary>
    public static Figures? Choose(Figures? feed, Figures? chain, Func<Figures?> compute, DateTime nowUtc)
    {
        if (feed is not null && IsFresh(feed, nowUtc)) return feed;
        if (chain is not null && IsFresh(chain, nowUtc)) return chain;
        return compute() ?? feed ?? chain;
    }

    /// <summary>
    /// Implied volatility in percent from a stored figure, to two places: the
    /// rule every reader of a stored IV shares (<see cref="OptionMath.IvPercent"/>),
    /// which the option chain responses use too.
    /// </summary>
    public static decimal? IvPercent(decimal? raw, bool vendorSendsPercent) =>
        OptionMath.IvPercent(raw, vendorSendsPercent) is { } percent ? Math.Round(percent, 2) : null;

    /// <summary>
    /// Black-Scholes figures for one option from its own price: IV solved from
    /// <paramref name="optionPrice"/>, the greeks from that IV. Measured at the
    /// OLDER of the two prices (the time to expiry is counted from there), so a
    /// computation on yesterday's last trade is yesterday's greeks. An option
    /// on a future is priced as one (Black-76). Null when the inputs cannot
    /// define an IV — no time left, or a price with no time value in it.
    /// </summary>
    public static Figures? Compute(
        bool isCall,
        decimal optionPrice,
        DateTime optionAsOfUtc,
        decimal underlyingPrice,
        DateTime underlyingAsOfUtc,
        decimal strike,
        DateTime expiryUtc,
        bool onFuture)
    {
        if (optionPrice <= 0m || underlyingPrice <= 0m || strike <= 0m) return null;

        var asOf = optionAsOfUtc < underlyingAsOfUtc ? optionAsOfUtc : underlyingAsOfUtc;
        double years = OptionMath.YearsBetween(asOf, expiryUtc);
        if (years <= 0) return null;

        double rate = OptionMath.LiveRiskFreeRate;
        double carry = onFuture ? rate : 0.0;
        double s = (double)underlyingPrice, k = (double)strike;

        var sigma = OptionMath.ImpliedVolatility(isCall, (double)optionPrice, s, k, years, rate, carry);
        if (sigma is null) return null;

        var g = OptionMath.Greeks(isCall, s, k, sigma.Value, years, rate, carry);
        if (g is null) return null;

        return new Figures(
            SourceComputed,
            asOf,
            IvPercent: Math.Round((decimal)(sigma.Value * 100.0), 2),
            Delta: (decimal)Math.Round(g.Delta, 4),
            Gamma: (decimal)Math.Round(g.Gamma, 6),
            Theta: (decimal)Math.Round(g.ThetaPerDay, 4),
            Vega: (decimal)Math.Round(g.VegaPerVolPoint, 4),
            UnderlyingPrice: underlyingPrice);
    }

    /// <summary>Figures of a future or a share: it moves one for one with itself, and nothing else.</summary>
    public static Figures DeltaOne(DateTime asOfUtc) =>
        new(SourceDeltaOne, asOfUtc, null, 1m, 0m, 0m, 0m);

    /// <summary>
    /// The figures, with what they are worth to THIS position: × quantity
    /// (lots × lot size) × side, +1 long and −1 short. A bought option's theta
    /// is a cost (negative ₹/day); a written one's is income.
    /// </summary>
    public static PositionGreeksResponse ToResponse(Figures f, bool isLong, int quantity, DateTime nowUtc)
    {
        decimal signed = (isLong ? 1m : -1m) * quantity;
        bool deltaOne = f.Source == SourceDeltaOne;
        return new PositionGreeksResponse
        {
            Source = f.Source,
            AsOfUtc = deltaOne ? null : f.AsOfUtc,
            // A future's delta is 1 however old its price is.
            Stale = !deltaOne && !IsFresh(f, nowUtc),
            IvPercent = f.IvPercent,
            Delta = f.Delta,
            Gamma = f.Gamma,
            Theta = f.Theta,
            Vega = f.Vega,
            UnderlyingPrice = f.UnderlyingPrice,
            DeltaQuantity = Math.Round(f.Delta * signed, 2),
            DeltaRupeesPerPoint = Math.Round(f.Delta * signed, 2),
            ThetaRupeesPerDay = Math.Round(f.Theta * signed, 2),
            VegaRupeesPerIvPoint = Math.Round(f.Vega * signed, 2)
        };
    }

    /// <summary>
    /// The book's sums. Theta and vega are rupees and add across
    /// underlyings; delta is in units of each underlying and is only summed
    /// within one (the book-wide figure is given only when there is one).
    /// </summary>
    public static RunGreeksTotals? Totals(IEnumerable<(string Underlying, PositionGreeksResponse Greeks)> legs, int unpriced)
    {
        var list = legs.ToList();
        if (list.Count == 0) return null;

        var byUnderlying = list
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Underlying) ? "?" : x.Underlying, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnderlyingDeltaTotal
            {
                Underlying = g.Key,
                DeltaQuantity = g.Sum(x => x.Greeks.DeltaQuantity),
                DeltaRupeesPerPoint = g.Sum(x => x.Greeks.DeltaRupeesPerPoint)
            })
            .OrderByDescending(x => Math.Abs(x.DeltaRupeesPerPoint))
            .ThenBy(x => x.Underlying, StringComparer.Ordinal)
            .ToList();

        var dated = list.Where(x => x.Greeks.AsOfUtc.HasValue).Select(x => x.Greeks.AsOfUtc!.Value).ToList();

        return new RunGreeksTotals
        {
            ThetaRupeesPerDay = list.Sum(x => x.Greeks.ThetaRupeesPerDay),
            VegaRupeesPerIvPoint = list.Sum(x => x.Greeks.VegaRupeesPerIvPoint),
            NetDeltaQuantity = byUnderlying.Count == 1 ? byUnderlying[0].DeltaQuantity : null,
            ByUnderlying = byUnderlying,
            Legs = list.Count,
            Unpriced = unpriced,
            Stale = list.Any(x => x.Greeks.Stale),
            OldestAsOfUtc = dated.Count > 0 ? dated.Min() : null
        };
    }

    /// <summary>
    /// Whether the feed's greeks for <paramref name="symbol"/> were priced
    /// against its real expiry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The enricher reads a weekly symbol's expiry from the symbol itself,
    /// exactly. A MONTHLY symbol (NIFTY26SEP…) carries only the month, and the
    /// enricher dates it by <c>monthly_expiry</c> in core/option_symbol.py: the
    /// last Tuesday on NSE (the last Thursday before Sep 2025), the last
    /// Thursday on BSE, moved back over a holiday in the calendar it reads.
    /// Until 27 Sep 2026 it took the last Thursday everywhere, so every NSE
    /// monthly's feed IV and theta were computed two days off; this check was
    /// written to catch that, and now mirrors the fixed rule.
    /// </para>
    /// <para>
    /// A holiday-shifted expiry is not trusted (NIFTY's November 2026 monthly
    /// expires on the 23rd because the 24th is Guru Nanak Jayanti). The
    /// enricher gets it right only when its seed calendar lists that holiday,
    /// and a year not seeded yet does not. Such a leg is priced from the chain
    /// or computed against the master's expiry instead, which loses nothing
    /// but the feed's freshness.
    /// </para>
    /// </remarks>
    public static bool FeedExpiryMatches(string symbol, DateOnly? expiry)
    {
        if (expiry is null) return true;
        var parsed = UnderlyingCatalog.ParseOptionSymbol(symbol);
        if (parsed is null || parsed.IsWeekly || parsed.Expiry is not { } monthEnd) return true;
        return MonthlyRuleDay(ExchangeOfSymbol(symbol), monthEnd.Year, monthEnd.Month) == expiry.Value;
    }

    /// <summary>SEBI's one expiry day per exchange, for contracts expiring from this date: NSE Tuesday, BSE Thursday.</summary>
    private static readonly DateOnly NseTuesdayFrom = new(2025, 9, 1);

    /// <summary>The weekday rule of <c>monthly_expiry</c>, before any holiday shift.</summary>
    private static DateOnly MonthlyRuleDay(string exchange, int year, int month)
    {
        var weekday = exchange == "NSE" && new DateOnly(year, month, 1) >= NseTuesdayFrom
            ? DayOfWeek.Tuesday
            : DayOfWeek.Thursday;
        var day = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        while (day.DayOfWeek != weekday) day = day.AddDays(-1);
        return day;
    }

    private static string ExchangeOfSymbol(string symbol)
    {
        int colon = symbol.IndexOf(':');
        return colon > 0 ? symbol[..colon].Trim().ToUpperInvariant() : "NSE";
    }
}

/// <summary>
/// Loads what <see cref="PositionGreeks"/> needs for a set of open legs — the
/// legs' quotes and greeks, the chain recorder's latest snapshots, the
/// underlyings' prices and each contract's expiry time — and returns each
/// leg's greeks.
/// </summary>
/// <remarks>
/// A recap run of the day being replayed (<see cref="IMarketReplayBook.Prices"/>)
/// is priced from the replay instead: the book's quotes and spot, on the
/// replay's clock. Its legs are that day's contracts, often expired since, and
/// today's live quotes and chain have nothing to say about them.
/// </remarks>
public sealed class PositionGreeksBuilder
{
    private readonly TradingDbContext _dbContext;
    private readonly IMarketSessionService _sessions;
    private readonly IMarketReplayBook? _replayBook;

    public PositionGreeksBuilder(TradingDbContext dbContext, IMarketSessionService sessions, IMarketReplayBook? replayBook = null)
    {
        _dbContext = dbContext;
        _sessions = sessions;
        _replayBook = replayBook;
    }

    /// <summary>One open leg, as the position view already decoded it.</summary>
    /// <param name="Ltp">The leg's mark: its live quote, else its stored mark.</param>
    /// <param name="LtpAsOfUtc">When that mark was written.</param>
    public sealed record OpenLeg(
        long PositionId,
        string Symbol,
        ContractInfo Contract,
        string? Exchange,
        string? InstrumentType,
        bool IsLong,
        int Quantity,
        decimal? Ltp,
        DateTime? LtpAsOfUtc);

    public sealed record Built(Dictionary<long, PositionGreeksResponse> ByPosition, RunGreeksTotals? Totals);

    /// <param name="replayPriced">
    /// The legs are a recap run's, of the day the market replay is playing:
    /// priced from the replay book on the replay's clock, never from the live
    /// quotes or today's chain.
    /// </param>
    public async Task<Built> BuildAsync(IReadOnlyList<OpenLeg> legs, DateTime nowUtc, CancellationToken cancellationToken,
        bool replayPriced = false)
    {
        var result = new Dictionary<long, PositionGreeksResponse>();
        if (legs.Count == 0) return new Built(result, null);

        var replay = replayPriced && _replayBook?.Day is not null ? _replayBook : null;
        if (replay is not null)
        {
            // "Now" is the replayed moment: the time to expiry is counted from
            // it, and a figure is fresh or stale by it, as live figures are by
            // the wall clock.
            nowUtc = replay.ClockUtc ?? nowUtc;
            legs = legs.Select(leg => AtReplayPrice(leg, replay)).ToList();
        }

        var options = legs.Where(IsOption).ToList();
        var optionSymbols = options.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();

        // (a) greeks the feed wrote onto each option's quote. In a replay, the
        // replayed quote's. The player sends prices only (live_ticks keeps no
        // greeks), so a replayed option normally has none, and its greeks are
        // computed from its replayed price and spot below: PositionGreeks.Compute,
        // the same Black-Scholes a live leg falls back to.
        var quotes = optionSymbols.Count == 0
            ? new Dictionary<string, QuoteGreeks>(StringComparer.Ordinal)
            : replay is not null
                ? ReplayQuoteGreeks(replay, optionSymbols)
                : (await _dbContext.LiveQuotesLatest.AsNoTracking()
                    .Where(x => optionSymbols.Contains(x.Symbol))
                    .Select(x => new QuoteGreeks(x.Symbol, x.UpdatedUtc, x.ImpliedVolatility, x.Delta, x.Gamma, x.Theta, x.Vega))
                    .ToListAsync(cancellationToken))
                  .GroupBy(x => x.Symbol, StringComparer.Ordinal)
                  .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // (b) the chain recorder's newest snapshot of each, only if fresh. None
        // in a replay: a snapshot fresh now is today's, not the replayed day's.
        var since = nowUtc - PositionGreeks.FreshFor;
        var snapshots = optionSymbols.Count == 0 || replay is not null
            ? new Dictionary<string, SnapshotGreeks>(StringComparer.Ordinal)
            : (await _dbContext.OptionChainSnapshots.AsNoTracking()
                .Where(x => optionSymbols.Contains(x.Symbol) && x.CapturedUtc >= since)
                .Select(x => new SnapshotGreeks(x.Symbol, x.CapturedUtc, x.SourceKey, x.SpotPrice, x.ImpliedVolatility, x.Delta, x.Gamma, x.Theta, x.Vega))
                .ToListAsync(cancellationToken))
              .GroupBy(x => x.Symbol, StringComparer.Ordinal)
              .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CapturedUtc).First(), StringComparer.Ordinal);

        // (c) the underlying each option is written on, and its price.
        var references = new Dictionary<long, (string Symbol, bool OnFuture)>();
        foreach (var leg in options)
        {
            var reference = await ReferenceAsync(leg, cancellationToken);
            if (reference is not null) references[leg.PositionId] = reference.Value;
        }
        var referenceSymbols = references.Values.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();
        var underlyingQuotes = referenceSymbols.Count == 0
            ? new Dictionary<string, (decimal? Ltp, DateTime At)>(StringComparer.Ordinal)
            : replay is not null
                ? ReplayPrices(replay, referenceSymbols)
                : (await _dbContext.LiveQuotesLatest.AsNoTracking()
                    .Where(x => referenceSymbols.Contains(x.Symbol))
                    .Select(x => new { x.Symbol, x.LastTradedPrice, x.UpdatedUtc })
                    .ToListAsync(cancellationToken))
                  .GroupBy(x => x.Symbol, StringComparer.Ordinal)
                  .ToDictionary(g => g.Key, g => (g.First().LastTradedPrice, g.First().UpdatedUtc), StringComparer.Ordinal);

        var totals = new List<(string Underlying, PositionGreeksResponse Greeks)>();
        int unpriced = 0;

        foreach (var leg in legs)
        {
            PositionGreeks.Figures? figures;
            if (IsOption(leg))
            {
                figures = FiguresFor(leg, quotes, snapshots, references, underlyingQuotes, nowUtc);
                if (figures is null)
                {
                    unpriced++;
                    continue;
                }
            }
            else if (IsDeltaOne(leg))
            {
                figures = PositionGreeks.DeltaOne(leg.LtpAsOfUtc ?? nowUtc);
            }
            else
            {
                continue;
            }

            var greeks = PositionGreeks.ToResponse(figures, leg.IsLong, leg.Quantity, nowUtc);
            result[leg.PositionId] = greeks;
            totals.Add((leg.Contract.Underlying, greeks));
        }

        return new Built(result, PositionGreeks.Totals(totals, unpriced));
    }

    private PositionGreeks.Figures? FiguresFor(
        OpenLeg leg,
        Dictionary<string, QuoteGreeks> quotes,
        Dictionary<string, SnapshotGreeks> snapshots,
        Dictionary<long, (string Symbol, bool OnFuture)> references,
        Dictionary<string, (decimal? Ltp, DateTime At)> underlyingQuotes,
        DateTime nowUtc)
    {
        PositionGreeks.Figures? feed = null;
        if (quotes.TryGetValue(leg.Symbol, out var q)
            && q.Delta.HasValue && q.Theta.HasValue && q.Vega.HasValue
            && PositionGreeks.FeedExpiryMatches(leg.Symbol, leg.Contract.ExpiryDate))
        {
            feed = new PositionGreeks.Figures(
                PositionGreeks.SourceFeed, q.UpdatedUtc,
                PositionGreeks.IvPercent(q.Iv, vendorSendsPercent: false),
                q.Delta.Value, q.Gamma ?? 0m, q.Theta.Value, q.Vega.Value);
        }

        PositionGreeks.Figures? chain = null;
        if (snapshots.TryGetValue(leg.Symbol, out var s)
            && s.Delta.HasValue && s.Theta.HasValue && s.Vega.HasValue)
        {
            chain = new PositionGreeks.Figures(
                PositionGreeks.SourceChain, s.CapturedUtc,
                PositionGreeks.IvPercent(s.Iv, vendorSendsPercent: OptionMath.ChainSourceStoresPercent(s.SourceKey)),
                s.Delta.Value, s.Gamma ?? 0m, s.Theta.Value, s.Vega.Value,
                s.SpotPrice > 0m ? s.SpotPrice : null);
        }

        return PositionGreeks.Choose(feed, chain, () =>
        {
            if (leg.Ltp is not > 0m || leg.LtpAsOfUtc is null) return null;
            if (leg.Contract.Strike is not > 0m || leg.Contract.ExpiryDate is null) return null;
            if (!references.TryGetValue(leg.PositionId, out var reference)) return null;
            if (!underlyingQuotes.TryGetValue(reference.Symbol, out var u) || u.Ltp is not > 0m) return null;

            var expiryUtc = ExpirySettler.ExpiryCloseUtc(_sessions, ExchangeOf(leg), SegmentOf(leg), leg.Contract.ExpiryDate.Value);
            return PositionGreeks.Compute(
                isCall: leg.Contract.OptionType == "CE",
                optionPrice: leg.Ltp.Value,
                optionAsOfUtc: leg.LtpAsOfUtc.Value,
                underlyingPrice: u.Ltp.Value,
                underlyingAsOfUtc: u.At,
                strike: leg.Contract.Strike.Value,
                expiryUtc: expiryUtc,
                onFuture: reference.OnFuture);
        }, nowUtc);
    }

    /// <summary>
    /// The leg at the replay's price, as of that price's market time (Dhan
    /// stamps a quote with its last trade). With no replayed quote there is no
    /// price of that day to compute from, so the leg counts as unpriced rather
    /// than being computed off a stored mark whose market time is not known.
    /// </summary>
    private static OpenLeg AtReplayPrice(OpenLeg leg, IMarketReplayBook replay)
    {
        var quote = replay.Quote(leg.Symbol);
        return quote?.LastTradedPrice is > 0m
            ? leg with { Ltp = quote.LastTradedPrice, LtpAsOfUtc = quote.ExchangeTimestampUtc ?? replay.ClockUtc }
            : leg with { Ltp = null, LtpAsOfUtc = null };
    }

    /// <summary>The replayed quotes' greeks, when a quote carries any, as of the quote's market time.</summary>
    private static Dictionary<string, QuoteGreeks> ReplayQuoteGreeks(IMarketReplayBook replay, IEnumerable<string> symbols)
    {
        var result = new Dictionary<string, QuoteGreeks>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (replay.Quote(symbol) is not { } q || (q.ExchangeTimestampUtc ?? replay.ClockUtc) is not DateTime at) continue;
            result[symbol] = new QuoteGreeks(symbol, at, q.ImpliedVolatility, q.Delta, q.Gamma, q.Theta, q.Vega);
        }

        return result;
    }

    /// <summary>The replayed prices of the underlyings, as of each quote's market time.</summary>
    private static Dictionary<string, (decimal? Ltp, DateTime At)> ReplayPrices(IMarketReplayBook replay, IEnumerable<string> symbols)
    {
        var result = new Dictionary<string, (decimal? Ltp, DateTime At)>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (replay.Quote(symbol) is not { } q || (q.ExchangeTimestampUtc ?? replay.ClockUtc) is not DateTime at) continue;
            result[symbol] = (q.LastTradedPrice, at);
        }

        return result;
    }

    /// <summary>The spot an index or stock option is priced off, or the future an MCX option is written on.</summary>
    private async Task<(string Symbol, bool OnFuture)?> ReferenceAsync(OpenLeg leg, CancellationToken cancellationToken)
    {
        string underlying = leg.Contract.Underlying;
        if (string.IsNullOrWhiteSpace(underlying)) return null;

        if (ExchangeOf(leg) == "MCX")
        {
            var expiry = leg.Contract.ExpiryDate;
            var future = await _dbContext.Instruments.AsNoTracking()
                .Where(x => x.Underlying == underlying
                            && x.InstrumentType == "FUT"
                            && x.Exchange == "MCX"
                            && x.ExpiryDate != null
                            && (expiry == null || x.ExpiryDate >= expiry))
                .OrderBy(x => x.ExpiryDate)
                .Select(x => x.Symbol)
                .FirstOrDefaultAsync(cancellationToken);
            return future is null ? null : (future, true);
        }

        string spot = UnderlyingCatalog.SpotSymbolFor(underlying);
        return string.IsNullOrWhiteSpace(spot) ? null : (spot, false);
    }

    private static bool IsOption(OpenLeg leg) =>
        leg.Contract.OptionType is "CE" or "PE" && leg.Contract.Strike is > 0m;

    private static bool IsDeltaOne(OpenLeg leg)
    {
        string type = (leg.InstrumentType ?? string.Empty).Trim().ToUpperInvariant();
        if (type == "FUT") return true;
        if (leg.Symbol.EndsWith("FUT", StringComparison.OrdinalIgnoreCase)) return true;
        // A share: "NSE:SBIN-EQ". An index is not something a book can hold.
        return leg.Symbol.EndsWith("-EQ", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExchangeOf(OpenLeg leg)
    {
        if (!string.IsNullOrWhiteSpace(leg.Exchange)) return leg.Exchange.Trim().ToUpperInvariant();
        int colon = leg.Symbol.IndexOf(':');
        return colon > 0 ? leg.Symbol[..colon].Trim().ToUpperInvariant() : "NSE";
    }

    private static string SegmentOf(OpenLeg leg) => ExchangeOf(leg) == "MCX" ? "COM" : "FO";

    private sealed record QuoteGreeks(string Symbol, DateTime UpdatedUtc, decimal? Iv, decimal? Delta, decimal? Gamma, decimal? Theta, decimal? Vega);

    private sealed record SnapshotGreeks(string Symbol, DateTime CapturedUtc, string SourceKey, decimal SpotPrice, decimal? Iv, decimal? Delta, decimal? Gamma, decimal? Theta, decimal? Vega);
}
