using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The latest quote of one contract as a fill or a mark reads it, with the
/// time it was written — without that, a quote frozen by a stalled feed looks
/// exactly like a current one.
/// </summary>
public readonly record struct QuoteSnapshot(
    decimal? LastTradedPrice,
    decimal? BidPrice,
    decimal? AskPrice,
    DateTime UpdatedUtc)
{
    /// <summary>Seconds since the quote was written; never negative.</summary>
    public double AgeSeconds(DateTime nowUtc) => Math.Max(0d, (nowUtc - UpdatedUtc).TotalSeconds);
}

/// <summary>
/// One leg's fill price and how it was reached. <see cref="ToMetadataJson"/>
/// is what the order row keeps, so a run view can say why a fill is the price
/// it is.
/// </summary>
/// <param name="Rule">A stable code: <c>bid</c>, <c>ask</c>, <c>ltp-less-half-spread</c>, <c>signal</c>, ...</param>
/// <param name="Note">The same in a sentence, ready to show.</param>
/// <param name="Reference">The price the half-spread was taken from, when one was.</param>
/// <param name="QuoteAgeSeconds">Age of the quote the price came from, when it came from one.</param>
public sealed record PaperFill(
    decimal Price,
    string Rule,
    string Note,
    decimal? Reference = null,
    decimal? HalfSpreadFraction = null,
    double? QuoteAgeSeconds = null,
    bool StaleQuote = false)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToMetadataJson() => JsonSerializer.Serialize(new
    {
        rule = Rule,
        note = Note,
        reference = Reference,
        halfSpread = HalfSpreadFraction,
        quoteAgeSeconds = QuoteAgeSeconds is { } age ? Math.Round(age, 1) : (double?)null,
        staleQuote = StaleQuote ? true : (bool?)null,
    }, Json);
}

/// <summary>
/// Where a live paper fill lands: a SELL at the bid and a BUY at the ask when
/// the quote has them, otherwise the last trade less or plus half the spread
/// (<see cref="PaperFillOptions"/>). Pure, so the rules are tested without a
/// database; <see cref="PaperTradingService"/> decides which quote, and whether
/// its age allows it at all.
/// </summary>
public static class PaperFillPricing
{
    /// <summary>
    /// The fill for <paramref name="side"/> from a live quote, or null when the
    /// quote carries no usable price. A quote older than the configured limit
    /// still prices (the caller has already decided it may) but the fill says
    /// how old it was.
    /// </summary>
    public static PaperFill? FromQuote(string side, QuoteSnapshot quote, PaperFillOptions options, DateTime nowUtc)
    {
        bool isBuy = IsBuy(side);
        double age = quote.AgeSeconds(nowUtc);
        bool stale = age > options.MaxQuoteAgeSeconds;

        if (options.UseBidAsk && BookSide(quote, isBuy) is { } touch)
        {
            return new PaperFill(touch, isBuy ? "ask" : "bid",
                WithAge(isBuy ? "filled at the ask" : "filled at the bid", age, stale),
                QuoteAgeSeconds: age, StaleQuote: stale);
        }

        if (quote.LastTradedPrice is { } ltp && ltp > 0m)
        {
            return Crossed(isBuy, ltp, "ltp", $"LTP {Price(ltp)}", options, age, stale);
        }

        return null;
    }

    /// <summary>
    /// The fill from a price that is not a live quote — the one the signal
    /// carried, or the position's last mark or entry — less (SELL) or plus
    /// (BUY) the half-spread, like any other market fill.
    /// </summary>
    /// <param name="basis"><c>signal</c>, <c>mark</c> or <c>entry</c>.</param>
    public static PaperFill FromReference(string side, decimal reference, string basis, PaperFillOptions options)
    {
        string what = basis switch
        {
            "signal" => $"no live quote: the signal's price {Price(reference)}",
            "mark" => $"no live quote: the position's last mark {Price(reference)}",
            "entry" => $"no live quote or mark: the entry price {Price(reference)}",
            _ => $"{basis} {Price(reference)}",
        };
        return Crossed(IsBuy(side), reference, basis, what, options, age: null, stale: false);
    }

    /// <summary>A price filled exactly as given: a replay's bar close, the manual ticket's limit.</summary>
    public static PaperFill AsGiven(decimal price, string rule = "signal", string note = "filled at the price the signal carried")
        => new(price, rule, note);

    /// <summary>"priced on a stale quote (95 s old)", as the stale-quote refusal and fills word it.</summary>
    public static string StaleNote(double ageSeconds)
        => $"priced on a stale quote ({Seconds(ageSeconds)} s old)";

    public static string Seconds(double ageSeconds)
        => Math.Round(ageSeconds).ToString("0", CultureInfo.InvariantCulture);

    private static PaperFill Crossed(bool isBuy, decimal reference, string basis, string what,
        PaperFillOptions options, double? age, bool stale)
    {
        decimal half = Math.Max(0m, options.HalfSpreadFraction);
        decimal price = isBuy ? reference * (1m + half) : reference * (1m - half);
        string percent = (half * 100m).ToString("0.###", CultureInfo.InvariantCulture);
        string note = $"{what} {(isBuy ? "plus" : "less")} half-spread ({percent}%)";

        return new PaperFill(price, $"{basis}-{(isBuy ? "plus" : "less")}-half-spread",
            age is { } a ? WithAge(note, a, stale) : note,
            Reference: reference, HalfSpreadFraction: half, QuoteAgeSeconds: age, StaleQuote: stale);
    }

    /// <summary>
    /// The side a market order would meet: the ask for a BUY, the bid for a
    /// SELL. A crossed book (bid above ask) is not a market, so neither side of
    /// it is used.
    /// </summary>
    private static decimal? BookSide(QuoteSnapshot quote, bool isBuy)
    {
        decimal? bid = quote.BidPrice is > 0m ? quote.BidPrice : null;
        decimal? ask = quote.AskPrice is > 0m ? quote.AskPrice : null;
        if (bid is not null && ask is not null && bid > ask) return null;
        return isBuy ? ask : bid;
    }

    private static string WithAge(string note, double age, bool stale) => stale ? $"{note}; {StaleNote(age)}" : note;

    private static bool IsBuy(string side) => string.Equals(side?.Trim(), "BUY", StringComparison.OrdinalIgnoreCase);

    private static string Price(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);
}
