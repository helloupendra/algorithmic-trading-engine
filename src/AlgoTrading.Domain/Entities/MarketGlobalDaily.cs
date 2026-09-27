namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One day's bar of an overseas index, commodity, currency or rate the Indian
/// open reacts to (S&amp;P 500, Nikkei, Brent, the dollar index, US 10-year...),
/// keyed by the desk's own symbol (SPX, N225, BRENT...).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Date"/> is the market's own trading date in its own time zone,
/// not an IST date: the S&amp;P 500's bar for Friday is dated Friday even
/// though it closes at 01:30 IST on Saturday.
/// </para>
/// <para>
/// Point in time: a forecast for an Indian session may use only rows with
/// <see cref="Date"/> earlier than that session's IST date. That rule is safe
/// for every symbol here. Every bar dated before the Indian date has closed by
/// 08:50 IST: the US cash close is 01:30–02:30 IST and the US futures close is
/// 02:30–03:30 IST, and Asia's bar for the same date is still trading, so it is
/// excluded. The recorder also stores only bars whose session is over
/// (see <c>GlobalDailyBars.IsFinal</c>), so no row holds a half-formed day.
/// Rows are upserted: a value the source revises later replaces the old one.
/// </para>
/// </remarks>
public class MarketGlobalDaily
{
    public long Id { get; set; }

    /// <summary>The desk's key, e.g. "SPX"; see <c>GlobalMarketKeys</c> for the source ticker of each.</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The market's own trading date.</summary>
    public DateOnly Date { get; set; }

    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal Close { get; set; }

    /// <summary>Null for indices, rates and currencies, which have none.</summary>
    public decimal? Volume { get; set; }

    /// <summary>Where the bar came from, e.g. "yahoo:^GSPC".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>When the row was last written.</summary>
    public DateTime FetchedUtc { get; set; }
}
