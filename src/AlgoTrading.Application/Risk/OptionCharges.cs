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
/// The statutory rates of one kind of contract. Percentages are of turnover
/// (premium × units for an option, price × units for a future).
/// </summary>
/// <remarks>
/// Three schedules, from the brokers' published charge sheets (Zerodha,
/// checked 27 Sep 2026): NSE/BSE index options, MCX options and MCX futures.
/// Until then crude was charged as an NSE index option: STT at 0.15% where
/// MCX's commodity transaction tax on options is 0.05%, and the exchange's
/// 0.03503% where MCX takes 0.0418%. The same three live in the Python
/// engine's <c>core/charges.py</c>, so a backtest is charged alike.
/// </remarks>
public sealed record ChargeSchedule(
    string Name,
    decimal BrokeragePerOrder,
    decimal SttSellPct,
    decimal ExchangeTxnPct,
    decimal SebiFeePct,
    decimal StampBuyPct,
    decimal GstPct)
{
    /// <summary>NSE and BSE index options. BSE's exchange charge differs a little; not modelled.</summary>
    public static readonly ChargeSchedule IndexOptions = new("index options", 20m, 0.15m, 0.03503m, 0.0001m, 0.003m, 18m);

    /// <summary>MCX options on commodity futures: CTT 0.05% on the sell side, MCX 0.0418% of premium.</summary>
    public static readonly ChargeSchedule McxOptions = new("MCX options", 20m, 0.05m, 0.0418m, 0.0001m, 0.003m, 18m);

    /// <summary>
    /// MCX non-agricultural futures: CTT 0.01% on the sell side, MCX 0.0021%,
    /// stamp 0.002% on the buy side. Brokerage is the lower of ₹20 and 0.03% of
    /// the order; ₹20 is taken, which overstates only mini contracts.
    /// </summary>
    public static readonly ChargeSchedule McxFutures = new("MCX futures", 20m, 0.01m, 0.0021m, 0.0001m, 0.002m, 18m);

    /// <summary>
    /// The schedule a symbol trades under: "MCX:…CE/PE" is an MCX option, any
    /// other "MCX:" symbol an MCX future, everything else an index option.
    /// </summary>
    public static ChargeSchedule ForSymbol(string? symbol)
    {
        string s = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (!s.StartsWith("MCX:", StringComparison.Ordinal)) return IndexOptions;
        return s.EndsWith("CE", StringComparison.Ordinal) || s.EndsWith("PE", StringComparison.Ordinal)
            ? McxOptions
            : McxFutures;
    }
}

/// <summary>
/// What option and futures fills cost beyond the price — the same model as the
/// Python engine's <c>core/charges.py</c>, constant for constant, so a live run
/// and a backtest of it are charged alike.
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
    /// executed orders, at index-option rates. Turnovers are in rupees;
    /// negative inputs are treated as 0.
    /// </summary>
    public static OptionChargeBreakdown For(decimal buyTurnover, decimal sellTurnover, int orders)
        => For(buyTurnover, sellTurnover, orders, ChargeSchedule.IndexOptions);

    /// <summary>The same, at the given schedule's rates.</summary>
    public static OptionChargeBreakdown For(decimal buyTurnover, decimal sellTurnover, int orders, ChargeSchedule schedule)
    {
        buyTurnover = Math.Max(0m, buyTurnover);
        sellTurnover = Math.Max(0m, sellTurnover);
        decimal turnover = buyTurnover + sellTurnover;

        decimal brokerage = schedule.BrokeragePerOrder * Math.Max(0, orders);
        decimal stt = sellTurnover * schedule.SttSellPct / 100m;
        decimal exchange = turnover * schedule.ExchangeTxnPct / 100m;
        decimal sebi = turnover * schedule.SebiFeePct / 100m;
        decimal stamp = buyTurnover * schedule.StampBuyPct / 100m;
        decimal gst = (brokerage + exchange + sebi) * schedule.GstPct / 100m;

        return new OptionChargeBreakdown(
            Round(brokerage), Round(stt), Round(exchange), Round(sebi), Round(stamp), Round(gst));
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
