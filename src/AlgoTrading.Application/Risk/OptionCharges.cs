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
/// <para>
/// NSE/BSE index options, MCX options and MCX futures are from the brokers'
/// published charge sheets (Zerodha, checked 27 Sep 2026). Until then crude was
/// charged as an NSE index option: STT at 0.15% where MCX's commodity
/// transaction tax on options is 0.05%, and the exchange's 0.03503% where MCX
/// takes 0.0418%.
/// </para>
/// <para>
/// NSE and BSE futures and shares were added on 2 Nov 2026. Until then each of
/// those fills was charged as an index option too, about four to eight times too
/// much: STT at 0.15% of notional. Every rate is from Zerodha's charge list
/// (zerodha.com/charges) and its support articles on STT and on exchange
/// transaction charges, read 2 Nov 2026. The dates behind them are from the
/// onetradejournal.com charges changelog, which cites the NSE circulars: STT on
/// futures 0.02% to 0.05% from 1 Apr 2026 (Finance Act 2026, NSE/FATAX/73524), and
/// transaction charges with the IPFT contribution included from 1 Mar 2026
/// (NSE/FA/73061: ₹182.99 + ₹0.01 a crore on futures, ₹306.99 + ₹0.01 on cash).
/// The NSE circulars themselves could not be opened when this was written.
/// </para>
/// <para>
/// Every schedule lives in the Python engine's <c>core/charges.py</c> too, so a
/// backtest is charged alike, and <c>tests/test_charges.py</c> reads this file and
/// fails when the two differ.
/// </para>
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
    /// NSE index and stock futures: STT 0.05% on the sell side, NSE 0.00183%, stamp
    /// 0.002% on the buy side. Brokerage is the lower of ₹20 and 0.03% of the order;
    /// ₹20 is taken, which overstates an order under ₹66,667.
    /// </summary>
    public static readonly ChargeSchedule NseFutures = new("NSE futures", 20m, 0.05m, 0.00183m, 0.0001m, 0.002m, 18m);

    /// <summary>BSE index and stock futures: as <see cref="NseFutures"/>, and BSE takes no transaction charge on them.</summary>
    public static readonly ChargeSchedule BseFutures = new("BSE futures", 20m, 0.05m, 0m, 0.0001m, 0.002m, 18m);

    /// <summary>
    /// NSE shares as an intraday trade: STT 0.025% on the sell side, NSE 0.00307%,
    /// stamp 0.003% on the buy side.
    /// </summary>
    /// <remarks>
    /// Every share fill is charged as intraday, because a fill does not say how the
    /// shares ended up: the schedule is chosen from the symbol alone, and only a
    /// position knows whether it was carried. Intraday is the desk's usual case (a
    /// manual order or a strategy's leg is squared off at the close unless ticked to
    /// carry), and this undercharges the other: real delivery pays STT 0.1% on both
    /// sides and stamp 0.015% on the buy side, with no brokerage at a discount
    /// broker. Brokerage is the lower of ₹20 and 0.03% of the order; ₹20 is taken.
    /// </remarks>
    public static readonly ChargeSchedule NseEquityIntraday = new("NSE shares (intraday)", 20m, 0.025m, 0.00307m, 0.0001m, 0.003m, 18m);

    /// <summary>
    /// BSE shares as an intraday trade: as <see cref="NseEquityIntraday"/>, with
    /// BSE's 0.00375%. BSE charges some scrip groups differently; not modelled.
    /// </summary>
    public static readonly ChargeSchedule BseEquityIntraday = new("BSE shares (intraday)", 20m, 0.025m, 0.00375m, 0.0001m, 0.003m, 18m);

    /// <summary>
    /// The schedule a symbol trades under, read from its grammar. "MCX:…CE/PE" is an
    /// MCX option and any other "MCX:" symbol an MCX future. Elsewhere a symbol ending
    /// in CE or PE is an option, in FUT a future, and in a series ("-EQ", "-BE", "-A")
    /// a share. Anything else, an index or a symbol it cannot read, stays an index
    /// option.
    /// </summary>
    /// <remarks>
    /// Keep in step with <c>segment_of</c> in <c>core/charges.py</c>. A stock option
    /// stays on <see cref="IndexOptions"/>: its statutory rates are the same on NSE.
    /// </remarks>
    public static ChargeSchedule ForSymbol(string? symbol)
    {
        string s = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        bool option = s.EndsWith("CE", StringComparison.Ordinal) || s.EndsWith("PE", StringComparison.Ordinal);

        if (s.StartsWith("MCX:", StringComparison.Ordinal)) return option ? McxOptions : McxFutures;
        if (option) return IndexOptions;

        bool bse = s.StartsWith("BSE:", StringComparison.Ordinal);
        if (s.EndsWith("FUT", StringComparison.Ordinal)) return bse ? BseFutures : NseFutures;
        return HasShareSeries(s) ? (bse ? BseEquityIntraday : NseEquityIntraday) : IndexOptions;
    }

    /// <summary>
    /// The part after the last hyphen is a series of one to three characters:
    /// "BAJAJ-AUTO-EQ" is a share, "NIFTY50-INDEX" and "BAJAJ-AUTO" are not.
    /// </summary>
    private static bool HasShareSeries(string symbol)
    {
        int hyphen = symbol.LastIndexOf('-');
        if (hyphen < 0) return false;
        string series = symbol[(hyphen + 1)..];
        return series.Length is >= 1 and <= 3 && series.All(char.IsLetterOrDigit);
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
/// contract note is the authority for the last rupee. The index-option exchange
/// charge here is 0.03503%; Zerodha's list of 2 Nov 2026 shows 0.03553%, with the
/// IPFT contribution included (₹0.50 a lakh of premium more).
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
