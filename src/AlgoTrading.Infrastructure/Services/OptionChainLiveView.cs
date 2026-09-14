// src/AlgoTrading.Infrastructure/Services/OptionChainLiveView.cs
using System.Text.Json;
using AlgoTrading.Contracts.OptionChain;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>One row of <c>live_quotes_latest</c>, reduced to what the chain view reads.</summary>
/// <param name="PreviousClose">The tick's own previous-close field (the table's Close column).</param>
/// <param name="PacketClose">The "close" field of the raw payload, when the feed sent one.</param>
public sealed record LiveQuoteSample(
    string Symbol,
    decimal? LastPrice,
    decimal? Bid,
    decimal? Ask,
    long? Volume,
    long? OpenInterest,
    decimal? PreviousClose,
    decimal? PacketClose,
    DateTime UpdatedUtc,
    string SourceKey);

/// <summary>
/// The live option chain: the newest per-minute snapshot with every strike that
/// has a fresh live quote brought up to the second, and the header strip read
/// off the result.
/// </summary>
/// <remarks>
/// Pure, so the rules that decide what is shown as live are pinned by tests
/// rather than discovered at 09:15. The two sources differ in cadence, not in
/// meaning: the recorder captures the whole chain once a minute (with IV and
/// greeks), the feed streams about a second-by-second quote for the strikes
/// around the money. A quote may only replace a snapshot figure when it is both
/// fresh and no older than the snapshot — otherwise a strike the feed stopped
/// hearing about would keep a price from before the last capture.
/// </remarks>
public static class OptionChainLiveView
{
    /// <summary>A quote older than this is never shown as live.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(2);

    /// <summary>A quote stamped this far ahead of the server clock is broken, not fresh.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromSeconds(30);

    public static bool IsFresh(DateTime updatedUtc, DateTime nowUtc)
        => nowUtc - updatedUtc <= FreshFor && updatedUtc - nowUtc <= FutureTolerance;

    // ------------------------------------------------------------------
    // Overlay
    // ------------------------------------------------------------------

    /// <summary>What the overlay did.</summary>
    public readonly record struct OverlayResult(int LiveLegs, int TotalLegs, DateTime? NewestUtc, string? SourceKey);

    /// <summary>
    /// Brings every leg with a fresh quote up to date: LTP, bid/ask, volume and
    /// OI from the quote; price change, OI change and build-up re-measured from
    /// the snapshot's own baselines. IV and greeks stay the snapshot's.
    /// </summary>
    public static OverlayResult Overlay(
        OptionChainResponse chain,
        IReadOnlyDictionary<string, LiveQuoteSample> quotes,
        DateTime snapshotUtc,
        DateTime nowUtc)
    {
        int live = 0, total = 0;
        DateTime? newest = null;
        string? source = null;

        foreach (var strike in chain.Strikes)
        {
            foreach (var leg in new[] { strike.Call, strike.Put })
            {
                if (leg is null) continue;
                total++;

                if (!quotes.TryGetValue(leg.Symbol, out var quote)) continue;
                if (!ApplyQuote(leg, quote, snapshotUtc, nowUtc)) continue;

                live++;
                if (newest is null || quote.UpdatedUtc > newest)
                {
                    newest = quote.UpdatedUtc;
                    source = quote.SourceKey;
                }
            }
        }

        return new OverlayResult(live, total, newest, source);
    }

    /// <summary>One leg. False when the quote is not fit to replace the snapshot.</summary>
    public static bool ApplyQuote(OptionChainLegResponse leg, LiveQuoteSample quote, DateTime snapshotUtc, DateTime nowUtc)
    {
        if (!IsFresh(quote.UpdatedUtc, nowUtc)) return false;
        if (quote.UpdatedUtc < snapshotUtc) return false;
        if (quote.LastPrice is not > 0) return false;

        // The baseline the snapshot measured its change from, whichever it was
        // (the broker's previous close, or the session's first capture): the
        // change was LTP minus that baseline, so the baseline is recoverable.
        decimal? priceBaseline = leg.LastTradedPrice is { } snapLtp && leg.PriceChange is { } snapChange
            ? snapLtp - snapChange
            : PositiveOrNull(quote.PreviousClose) ?? PositiveOrNull(quote.PacketClose);

        leg.LastTradedPrice = quote.LastPrice;

        // An empty book on the quote clears the snapshot's; a feed that carries
        // no book at all (both null) says nothing, and the snapshot's stands.
        if (quote.Bid is not null || quote.Ask is not null)
        {
            leg.BidPrice = quote.Bid;
            leg.AskPrice = quote.Ask;
        }

        if (quote.Volume is not null) leg.Volume = quote.Volume;

        if (quote.OpenInterest is > 0 && OpenInterestAgrees(leg.OpenInterest, quote.OpenInterest.Value, snapshotUtc, nowUtc))
            leg.OpenInterest = quote.OpenInterest;

        if (priceBaseline is > 0)
        {
            leg.PriceChange = quote.LastPrice - priceBaseline;
            leg.PriceChangePercent = leg.PriceChange / priceBaseline * 100m;
        }

        long? interestChange = leg.OpenInterest is not null && leg.OpenInterestBaseline is not null
            ? leg.OpenInterest - leg.OpenInterestBaseline
            : null;
        leg.OpenInterestChange = interestChange;
        leg.OpenInterestChangePercent = leg.OpenInterestBaseline is > 0 && leg.OpenInterest is not null
            ? OptionChainAnalytics.ChangePercent(leg.OpenInterest.Value, leg.OpenInterestBaseline.Value)
            : null;

        leg.BuildUp = OptionChainAnalytics.Classify(leg.PriceChange ?? 0m, interestChange ?? 0).ToString();
        leg.IsLive = true;
        leg.QuoteUpdatedUtc = quote.UpdatedUtc;
        return true;
    }

    /// <summary>
    /// False when a live OI is an order of magnitude away from a recent snapshot's.
    /// </summary>
    /// <remarks>
    /// Open interest does not move eightfold in a few minutes on a strike that
    /// already carries real size; a figure that does is a unit mismatch between
    /// the feed and the chain (lots against shares), and overlaying it would put
    /// a +2,900% OI change on exactly the strikes around the money.
    /// </remarks>
    public static bool OpenInterestAgrees(long? snapshotOi, long liveOi, DateTime snapshotUtc, DateTime nowUtc)
    {
        if (snapshotOi is not >= 500) return true;
        if (nowUtc - snapshotUtc > TimeSpan.FromMinutes(5)) return true;
        return liveOi * 8 >= snapshotOi.Value && liveOi <= snapshotOi.Value * 8;
    }

    // ------------------------------------------------------------------
    // Summary
    // ------------------------------------------------------------------

    /// <summary>
    /// Totals, ratios, max pain, heaviest strikes and the at-the-money row, from
    /// the strikes as they stand. Run after an overlay so they agree with it.
    /// </summary>
    public static void Summarise(OptionChainResponse chain)
    {
        chain.TotalCallOpenInterest = 0;
        chain.TotalPutOpenInterest = 0;
        chain.TotalCallOpenInterestChange = 0;
        chain.TotalPutOpenInterestChange = 0;

        var interests = new List<OptionChainAnalytics.StrikeInterest>(chain.Strikes.Count);
        foreach (var strike in chain.Strikes)
        {
            long callOi = strike.Call?.OpenInterest ?? 0;
            long putOi = strike.Put?.OpenInterest ?? 0;

            strike.PutCallRatio = OptionChainAnalytics.PutCallRatio(putOi, callOi);
            strike.PutCallRatioOfChange = OptionChainAnalytics.PutCallRatio(
                Math.Abs(strike.Put?.OpenInterestChange ?? 0),
                Math.Abs(strike.Call?.OpenInterestChange ?? 0));

            interests.Add(new OptionChainAnalytics.StrikeInterest(strike.StrikePrice, callOi, putOi));
            chain.TotalCallOpenInterest += callOi;
            chain.TotalPutOpenInterest += putOi;
            chain.TotalCallOpenInterestChange += strike.Call?.OpenInterestChange ?? 0;
            chain.TotalPutOpenInterestChange += strike.Put?.OpenInterestChange ?? 0;
        }

        chain.AtTheMoneyStrike = chain.SpotPrice > 0
            ? OptionChainAnalytics.AtTheMoney(chain.Strikes.Select(x => x.StrikePrice).ToList(), chain.SpotPrice)
            : null;
        foreach (var strike in chain.Strikes)
            strike.IsAtTheMoney = strike.StrikePrice == chain.AtTheMoneyStrike;

        chain.MaxPainStrike = OptionChainAnalytics.MaxPain(interests);
        chain.HeaviestCallStrike = OptionChainAnalytics.HeaviestStrike(interests, "CE");
        chain.HeaviestPutStrike = OptionChainAnalytics.HeaviestStrike(interests, "PE");
        chain.PutCallRatio = OptionChainAnalytics.PutCallRatio(chain.TotalPutOpenInterest, chain.TotalCallOpenInterest);
    }

    // ------------------------------------------------------------------
    // Header
    // ------------------------------------------------------------------

    /// <summary>
    /// The previous close to measure a day change from, and what it rests on.
    /// </summary>
    /// <remarks>
    /// The tick's own previous-close field first. Dhan's feed leaves that empty
    /// (no previous-close packet arrives in Full mode), but the "close" field of
    /// its quote packet carried the previous close on 2026-09-14: for three MCX
    /// options it matched Dhan's REST chain previous close to the paisa. After
    /// the bell that field may become today's close, so a packet close equal to
    /// the last price on a closed market is treated as unknown rather than
    /// shown as a flat day.
    /// </remarks>
    public static (decimal? Close, string? Basis) PreviousCloseOf(LiveQuoteSample quote, bool marketOpen)
    {
        if (quote.PreviousClose is > 0) return (quote.PreviousClose, "feed");

        if (string.Equals(quote.SourceKey, "dhan", StringComparison.OrdinalIgnoreCase) && quote.PacketClose is > 0)
        {
            if (!marketOpen && quote.PacketClose == quote.LastPrice) return (null, null);
            return (quote.PacketClose, "dhan-close-field");
        }

        return (null, null);
    }

    /// <summary>A header price from a quote; null when the quote has no price at all.</summary>
    public static OptionChainQuoteResponse? QuoteOf(LiveQuoteSample? quote, DateTime nowUtc, bool marketOpen)
    {
        if (quote?.LastPrice is not > 0) return null;

        bool fresh = IsFresh(quote.UpdatedUtc, nowUtc);
        var (close, basis) = PreviousCloseOf(quote, marketOpen);
        var result = new OptionChainQuoteResponse
        {
            Symbol = quote.Symbol,
            LastPrice = quote.LastPrice,
            AsOfUtc = quote.UpdatedUtc,
            SourceKey = quote.SourceKey,
            IsLive = fresh,
            Basis = fresh ? "live-quote" : "last-quote",
        };
        SetChange(result, close, basis);
        return result;
    }

    /// <summary>
    /// The spot to read the chain against: a fresh quote; else the last quote
    /// when it is newer than the snapshot and from the same IST day (after the
    /// bell); else the snapshot's own spot, measured against the quote's
    /// previous close when one is known.
    /// </summary>
    /// <remarks>
    /// The same-day rule keeps a chain captured on one session from being
    /// shaded and centred against a price from another: a Tuesday chain read
    /// against Thursday's close puts the ATM row and the spot line wherever
    /// Thursday was.
    /// </remarks>
    public static OptionChainQuoteResponse? ChooseSpot(
        OptionChainQuoteResponse? quote,
        string symbol,
        decimal snapshotSpot,
        DateTime? snapshotUtc,
        string? snapshotSource)
    {
        if (quote is { IsLive: true }) return quote;
        if (quote is not null && (snapshotUtc is null || SameSessionAfter(quote.AsOfUtc, snapshotUtc.Value))) return quote;
        if (snapshotUtc is null || snapshotSpot <= 0) return quote;

        var spot = new OptionChainQuoteResponse
        {
            Symbol = string.IsNullOrEmpty(symbol) ? quote?.Symbol ?? string.Empty : symbol,
            LastPrice = snapshotSpot,
            AsOfUtc = snapshotUtc,
            SourceKey = snapshotSource,
            IsLive = false,
            Basis = "snapshot",
        };
        // A quote from another day carries another day's previous close.
        if (quote?.AsOfUtc is { } quoteUtc && IstTime.DateOf(quoteUtc) == IstTime.DateOf(snapshotUtc.Value))
            SetChange(spot, quote.PreviousClose, quote.PreviousCloseBasis);
        return spot;
    }

    /// <summary>True when a quote time is at or after the capture and on the same IST day.</summary>
    public static bool SameSessionAfter(DateTime? quoteUtc, DateTime snapshotUtc)
        => quoteUtc is { } q && q >= snapshotUtc && IstTime.DateOf(q) == IstTime.DateOf(snapshotUtc);

    /// <summary>
    /// The replay spot: the snapshot's, and nothing from the live table, whose
    /// rows describe now rather than the moment being replayed.
    /// </summary>
    public static OptionChainQuoteResponse? SnapshotSpot(string symbol, decimal snapshotSpot, DateTime snapshotUtc, string? source)
        => snapshotSpot > 0
            ? new OptionChainQuoteResponse
            {
                Symbol = symbol,
                LastPrice = snapshotSpot,
                AsOfUtc = snapshotUtc,
                SourceKey = source,
                Basis = "snapshot",
            }
            : null;

    private static void SetChange(OptionChainQuoteResponse quote, decimal? previousClose, string? basis)
    {
        if (previousClose is not > 0 || quote.LastPrice is null) return;
        quote.PreviousClose = previousClose;
        quote.PreviousCloseBasis = basis;
        quote.Change = quote.LastPrice - previousClose;
        quote.ChangePercent = quote.Change / previousClose * 100m;
    }

    /// <summary>A future with its premium over the spot.</summary>
    public static OptionChainFutureResponse? FutureOf(OptionChainQuoteResponse? quote, DateOnly? expiry, decimal? spot)
    {
        if (quote is null) return null;
        var future = new OptionChainFutureResponse
        {
            Symbol = quote.Symbol,
            LastPrice = quote.LastPrice,
            Change = quote.Change,
            ChangePercent = quote.ChangePercent,
            PreviousClose = quote.PreviousClose,
            PreviousCloseBasis = quote.PreviousCloseBasis,
            AsOfUtc = quote.AsOfUtc,
            SourceKey = quote.SourceKey,
            IsLive = quote.IsLive,
            Basis = quote.Basis,
            ExpiryDate = expiry,
        };
        if (quote.LastPrice is { } price && spot is > 0)
        {
            future.PremiumOverSpot = price - spot;
            future.PremiumPercent = future.PremiumOverSpot / spot * 100m;
        }
        return future;
    }

    /// <summary>The two strikes the spot lies between: highest at or below, lowest above.</summary>
    public static (decimal? Lower, decimal? Upper) SpotBetween(IEnumerable<decimal> strikes, decimal spot)
    {
        decimal? lower = null, upper = null;
        if (spot <= 0) return (null, null);
        foreach (var strike in strikes)
        {
            if (strike <= spot)
            {
                if (lower is null || strike > lower) lower = strike;
            }
            else if (upper is null || strike < upper)
            {
                upper = strike;
            }
        }
        return (lower, upper);
    }

    /// <summary>IST calendar days from the moment to expiry.</summary>
    public static int DaysToExpiry(DateOnly expiry, DateTime momentUtc)
        => expiry.DayNumber - IstTime.DateOf(momentUtc).DayNumber;

    /// <summary>Mean of the ATM call and put IV; either one alone when the other is unpriced.</summary>
    public static decimal? AtTheMoneyIv(OptionChainResponse chain)
    {
        var atm = chain.Strikes.FirstOrDefault(x => x.IsAtTheMoney);
        if (atm is null) return null;
        var ivs = new[] { atm.Call?.ImpliedVolatility, atm.Put?.ImpliedVolatility }
            .Where(x => x is > 0)
            .Select(x => x!.Value)
            .ToList();
        return ivs.Count == 0 ? null : ivs.Average();
    }

    /// <summary>Put OI change over call OI change, only when both sides added contracts.</summary>
    public static decimal? PutCallRatioOfChange(long callChange, long putChange)
        => callChange > 0 && putChange >= 0 ? (decimal)putChange / callChange : null;

    /// <summary>
    /// Everything in the header that is read off the chain itself — support,
    /// resistance, totals, ratios, ATM IV, days to expiry, the spot's position.
    /// Prices, lot size and session state are the caller's to add.
    /// </summary>
    public static OptionChainHeaderResponse Header(OptionChainResponse chain, DateTime momentUtc, DateTime nowUtc)
    {
        var header = new OptionChainHeaderResponse
        {
            ServerUtc = nowUtc,
            AtTheMoneyStrike = chain.AtTheMoneyStrike,
            MaxPainStrike = chain.MaxPainStrike,
            PutCallRatio = chain.PutCallRatio,
            PutCallRatioOfChange = PutCallRatioOfChange(chain.TotalCallOpenInterestChange, chain.TotalPutOpenInterestChange),
            SupportStrike = chain.HeaviestPutStrike,
            ResistanceStrike = chain.HeaviestCallStrike,
            TotalCallOpenInterest = chain.TotalCallOpenInterest,
            TotalPutOpenInterest = chain.TotalPutOpenInterest,
            TotalCallOpenInterestChange = chain.TotalCallOpenInterestChange,
            TotalPutOpenInterestChange = chain.TotalPutOpenInterestChange,
            AtTheMoneyIv = AtTheMoneyIv(chain),
            FreshSeconds = (int)FreshFor.TotalSeconds,
        };

        if (chain.HeaviestPutStrike is { } support)
            header.SupportOpenInterest = chain.Strikes.FirstOrDefault(x => x.StrikePrice == support)?.Put?.OpenInterest;
        if (chain.HeaviestCallStrike is { } resistance)
            header.ResistanceOpenInterest = chain.Strikes.FirstOrDefault(x => x.StrikePrice == resistance)?.Call?.OpenInterest;

        if (chain.Strikes.Count > 0 && chain.ExpiryDate != default)
            header.DaysToExpiry = DaysToExpiry(chain.ExpiryDate, momentUtc);

        (header.SpotBetweenLower, header.SpotBetweenUpper) = SpotBetween(chain.Strikes.Select(x => x.StrikePrice), chain.SpotPrice);
        return header;
    }

    /// <summary>The "close" number in a raw tick payload, or null.</summary>
    public static decimal? PacketCloseOf(string? rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload) || rawPayload[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            return doc.RootElement.TryGetProperty("close", out var close)
                   && close.ValueKind == JsonValueKind.Number
                   && close.TryGetDecimal(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? PositiveOrNull(decimal? value) => value is > 0 ? value : null;
}
