namespace AlgoTrading.Application.Risk;

/// <summary>The statutory cost of a set of option fills, line by line.</summary>
public sealed record OptionChargeBreakdown(
    decimal Brokerage,
    decimal Stt,
    decimal Exchange,
    decimal Sebi,
    decimal Stamp,
    decimal Gst)
{
    public decimal Total => Brokerage + Stt + Exchange + Sebi + Stamp + Gst;
}

/// <summary>
/// What NSE/BSE index option fills cost beyond the premium — the same model as
/// the Python engine's <c>core/charges.py</c>, constant for constant, so a live
/// run and a backtest of it are charged alike.
/// </summary>
/// <remarks>
/// <para>
/// Live paper runs used to report their gross as "Net P&amp;L". On 25 Sep the
/// six Fulcrum runs showed +₹25,923; their brokerage and statutory charges came
/// to about ₹1,76,500, so the real result was a large loss the console called a
/// profit. Charges alone exceeded gross on 11 of 12 Fulcrum runs over 24–25 Sep.
/// </para>
/// <para>
/// Slippage and the bid–ask spread are not in here: paper fills are at the last
/// traded price, and the spread is a separate, measured cost to add later.
/// Rates are 2026 approximations (STT on premium 0.15% from 1 April 2026) — a
/// contract note is the authority for the last rupee.
/// </para>
/// </remarks>
public static class OptionCharges
{
    public const decimal BrokeragePerOrder = 20m;
    public const decimal SttSellPct = 0.15m;
    public const decimal ExchangeTxnPct = 0.03503m;
    public const decimal SebiFeePct = 0.0001m;
    public const decimal StampBuyPct = 0.003m;
    public const decimal GstPct = 18m;

    /// <summary>
    /// Charges on the given filled turnovers (premium × units) and number of
    /// executed orders. Turnovers are in rupees; negative inputs are treated as 0.
    /// </summary>
    public static OptionChargeBreakdown For(decimal buyTurnover, decimal sellTurnover, int orders)
    {
        buyTurnover = Math.Max(0m, buyTurnover);
        sellTurnover = Math.Max(0m, sellTurnover);
        decimal turnover = buyTurnover + sellTurnover;

        decimal brokerage = BrokeragePerOrder * Math.Max(0, orders);
        decimal stt = sellTurnover * SttSellPct / 100m;
        decimal exchange = turnover * ExchangeTxnPct / 100m;
        decimal sebi = turnover * SebiFeePct / 100m;
        decimal stamp = buyTurnover * StampBuyPct / 100m;
        decimal gst = (brokerage + exchange + sebi) * GstPct / 100m;

        return new OptionChargeBreakdown(
            Round(brokerage), Round(stt), Round(exchange), Round(sebi), Round(stamp), Round(gst));
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
