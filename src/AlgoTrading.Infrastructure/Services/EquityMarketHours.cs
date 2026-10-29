// src/AlgoTrading.Infrastructure/Services/EquityMarketHours.cs
namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The regular hours of NSE and BSE, cash and derivatives, on any IST day: the
/// one place the desk keeps them. <see cref="MarketSessionService"/> answers from
/// these for the CM and FO segments, and the few places that need a clock time
/// rather than a session (a bar's day, an expiry's close) read them here.
/// </summary>
/// <remarks>
/// <para>
/// Until 6 Dec 2026 both segments close at 15:30. SEBI's circular
/// HO/47/11/11(3)2025-MRD-POD2/I/23843/2026 of 15 Oct 2026 keeps the cash
/// market's continuous session to 15:30, followed by a closing auction from
/// 15:30 to 15:40, and moves the derivatives close to 15:45 from 7 Dec 2026
/// (paras 4.2.1, 4.2.4–4.2.5 and 6.1). The cash indices are computed from cash
/// prices, so NIFTY 50 or SENSEX stop moving at about 15:30 while their options
/// and futures trade on to 15:45.
/// </para>
/// <para>
/// One exception, para 4.2.4: on an expiry day, the expiring index derivatives
/// stop at 15:30. Stock derivatives trade to 15:45 on every day, expiry days
/// included (para 4.2.5).
/// </para>
/// <para>
/// A day before <see cref="LaterDerivativesCloseFrom"/> is answered as it always
/// was, so backtests, replays and any reading of an earlier day keep 15:30. A
/// later circular is one change here.
/// </para>
/// </remarks>
public static class EquityMarketHours
{
    /// <summary>The open of both segments.</summary>
    public static readonly TimeOnly Open = new(9, 15);

    /// <summary>The cash market's continuous session ends here, on every day.</summary>
    public static readonly TimeOnly CashClose = new(15, 30);

    /// <summary>The first IST day the derivatives close at <see cref="LaterDerivativesClose"/>.</summary>
    public static readonly DateOnly LaterDerivativesCloseFrom = new(2026, 12, 7);

    /// <summary>The derivatives close from <see cref="LaterDerivativesCloseFrom"/>.</summary>
    public static readonly TimeOnly LaterDerivativesClose = new(15, 45);

    /// <summary>When NSE and BSE options and futures stop trading on <paramref name="istDay"/>.</summary>
    public static TimeOnly DerivativesCloseOn(DateOnly istDay)
        => istDay >= LaterDerivativesCloseFrom ? LaterDerivativesClose : CashClose;

    /// <summary>
    /// True on a day the derivatives trade past the cash close. Before
    /// <see cref="LaterDerivativesCloseFrom"/> never, and the code that has to
    /// look further on such a day (an expiry, a symbol's segment) looks no further.
    /// </summary>
    public static bool DerivativesCloseLater(DateOnly istDay) => DerivativesCloseOn(istDay) > CashClose;

    /// <summary>
    /// When one contract stops trading on <paramref name="istDay"/>: an index option
    /// or future expiring that day at the cash close (para 4.2.4), any other at the
    /// derivatives close.
    /// </summary>
    public static TimeOnly ContractCloseOn(DateOnly istDay, bool indexContractExpiringThatDay)
        => indexContractExpiringThatDay ? CashClose : DerivativesCloseOn(istDay);

    /// <summary>
    /// When an NSE or BSE symbol stops trading on <paramref name="istDay"/>: a share
    /// or an index at the cash close, an option or future at its contract's close.
    /// The expiry is <paramref name="expiry"/> when the caller has it from the
    /// instrument master, else what a weekly symbol says; a monthly symbol names
    /// only its month, so without the master it counts as not expiring that day.
    /// </summary>
    public static TimeOnly CloseOf(string symbol, DateOnly istDay, DateOnly? expiry = null)
    {
        if (!IsDerivative(symbol)) return CashClose;
        var option = UnderlyingCatalog.ParseOptionSymbol(symbol);
        var day = expiry ?? (option is { IsWeekly: true } ? option.Expiry : null);
        bool index = UnderlyingCatalog.IsIndex(option?.Underlying ?? UnderlyingCatalog.InferUnderlying(symbol));
        return ContractCloseOn(istDay, index && day == istDay);
    }

    /// <summary>
    /// True for an option or a future, in the FYERS symbol grammar the desk stores
    /// ("NSE:NIFTY2612224500CE", "NSE:BANKNIFTY26DECFUT"); false for a share, an
    /// index or anything unreadable.
    /// </summary>
    public static bool IsDerivative(string? symbol)
        => UnderlyingCatalog.ParseOptionSymbol(symbol) is not null || UnderlyingCatalog.IsFutureSymbol(symbol);

    /// <summary>
    /// The session segment a symbol trades in, as <see cref="MarketSessionService"/>
    /// names them: "COM" for MCX, "FO" for an NSE or BSE option or future, "CM" for
    /// the rest (shares and the indices, which are computed from cash prices).
    /// </summary>
    public static string SegmentOf(string? symbol)
    {
        var text = (symbol ?? string.Empty).Trim();
        if (text.StartsWith("MCX:", StringComparison.OrdinalIgnoreCase)) return "COM";
        return IsDerivative(text) ? "FO" : "CM";
    }
}
