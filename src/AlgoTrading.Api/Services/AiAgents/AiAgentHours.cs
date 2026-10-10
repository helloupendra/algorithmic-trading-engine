using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// When the offline agents may work: never in the live desk's session. The server is a small box the live desk
/// shares, and a research query or a model call has no business competing with the runners for it.
/// </summary>
public static class AiAgentHours
{
    /// <summary>The live desk's hours on a trading day: the morning job starts the runners before 09:00, and the close settles by 15:45.</summary>
    public static readonly TimeOnly QuietFrom = new(9, 0);

    public static readonly TimeOnly QuietUntil = new(15, 45);

    /// <summary>
    /// Whether <paramref name="nowUtc"/> is inside 09:00–15:45 IST on an NSE trading day, by the exchange calendar: a
    /// weekday holiday is not one, a weekend special session is.
    /// </summary>
    public static bool InSession(DateTime nowUtc, IMarketSessionService sessions)
    {
        var info = sessions.GetSessionInfo(nowUtc, "NSE", "FO");
        if (!info.IsTradingDay) return false;
        var clock = TimeOnly.FromDateTime(IstTime.ToIst(nowUtc));
        return clock >= QuietFrom && clock < QuietUntil;
    }

    /// <summary>Whether the IST day is an NSE trading day in the exchange calendar.</summary>
    public static bool IsTradingDay(DateOnly day, IMarketSessionService sessions) =>
        sessions.GetSessionInfo(IstTime.MiddayUtc(day), "NSE", "FO").IsTradingDay;

    /// <summary>An "HH:mm" setting, or <paramref name="fallback"/> when it cannot be read.</summary>
    public static TimeOnly Clock(string? value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : fallback;

    /// <summary>
    /// The latest <paramref name="weekday"/> at or before <paramref name="nowUtc"/>'s IST day whose
    /// <paramref name="at"/> has passed, as an IST day: the week a weekly agent's work is for.
    /// </summary>
    public static DateOnly LatestWeekly(DateTime nowUtc, DayOfWeek weekday, TimeOnly at)
    {
        var ist = IstTime.ToIst(nowUtc);
        var day = DateOnly.FromDateTime(ist);
        while (day.DayOfWeek != weekday) day = day.AddDays(-1);
        if (day == DateOnly.FromDateTime(ist) && TimeOnly.FromDateTime(ist) < at) day = day.AddDays(-7);
        return day;
    }
}
