using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>
/// Which NSE sessions a portfolio's prices belong to, from the exchange calendar the desk keeps: weekdays that are
/// not holidays, and any special session (Muhurat trading) on the day it is held.
/// </summary>
public sealed class PortfolioSessions(IMarketCalendar calendar)
{
    private const string Exchange = "NSE";

    /// <summary>A day's daily close is final by then: the session closes at 15:30 IST and the closing price follows.</summary>
    public static readonly TimeSpan CloseSettledIst = new(16, 0, 0);

    public bool IsSession(DateOnly date) =>
        calendar.SpecialSessionOn(Exchange, date) is not null
        || (date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
            && calendar.HolidayOn(Exchange, date) is not { Closure: AlgoTrading.Domain.Enums.MarketClosure.FullDay });

    /// <summary>The last session strictly before <paramref name="date"/>.</summary>
    public DateOnly PreviousSession(DateOnly date)
    {
        var d = date.AddDays(-1);
        for (int i = 0; i < 30 && !IsSession(d); i++) d = d.AddDays(-1);
        return d;
    }

    /// <summary>
    /// The session a price seen at <paramref name="nowUtc"/> belongs to: today once the market has opened on a session
    /// day, otherwise the last session before. So on a Saturday, or at 08:00 on a Monday, a holding's day change is
    /// the last session's, as a broker's own app shows it.
    /// </summary>
    public DateOnly PriceSession(DateTime nowUtc)
    {
        var ist = IstTime.ToIst(nowUtc);
        var today = DateOnly.FromDateTime(ist);
        return IsSession(today) && ist.TimeOfDay >= IstTime.SessionOpen ? today : PreviousSession(today);
    }

    /// <summary>The newest session whose daily close is final at <paramref name="nowUtc"/>.</summary>
    public DateOnly LastCompletedSession(DateTime nowUtc)
    {
        var ist = IstTime.ToIst(nowUtc);
        var today = DateOnly.FromDateTime(ist);
        return IsSession(today) && ist.TimeOfDay >= CloseSettledIst ? today : PreviousSession(today);
    }
}
