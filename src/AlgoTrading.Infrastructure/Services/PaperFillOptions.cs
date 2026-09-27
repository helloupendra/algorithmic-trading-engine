namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// How a live paper fill is priced: the side of the book it crosses, and how
/// old a quote may be before it stops being a price. Section <c>PaperFills</c>.
/// </summary>
/// <remarks>
/// Until 28 Sep a paper fill took the last trade as it was: a sold straddle
/// sold at the LTP and bought back at the LTP, so every round trip was free of
/// the spread a real order pays twice. Net P&amp;L already took the statutory
/// charges off (<c>RunCharges</c>); the spread is the other half of what a
/// trade costs, and leaving it out flatters exactly the strategies that trade
/// most. Past fills are left as they were booked.
/// </remarks>
public sealed class PaperFillOptions
{
    public const string SectionName = "PaperFills";

    /// <summary>
    /// Fill a SELL at the bid and a BUY at the ask whenever the latest quote
    /// carries that side of the book. Off, every live fill is the last trade
    /// less (SELL) or plus (BUY) <see cref="HalfSpreadFraction"/>.
    /// </summary>
    public bool UseBidAsk { get; set; } = true;

    /// <summary>
    /// Half the bid-ask spread, as a fraction of the price, that a fill pays
    /// when the quote has no usable bid or ask (some SENSEX contracts quote
    /// none): a SELL fills at LTP × (1 − this), a BUY at LTP × (1 + this).
    /// 0.0015 is 0.15%, the 27 Sep audit's figure; set it from the spreads the
    /// desk actually sees.
    /// </summary>
    public decimal HalfSpreadFraction { get; set; } = 0.0015m;

    /// <summary>
    /// While the contract's market is open, the oldest quote a strategy's fill
    /// may be priced from, in seconds. An older one leaves the leg unpriced and
    /// the signal is refused with the quote's age. The square-offs a person or
    /// the close asks for still fill on an older quote, and say so.
    /// </summary>
    public int MaxQuoteAgeSeconds { get; set; } = 60;
}
