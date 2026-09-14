using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Domain.ValueObjects;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// When each exchange trades: its timetable, weekends, and the holidays and
/// special sessions in the exchange's own calendar (<see cref="IMarketCalendar"/>).
/// Feeds, runners and the morning job ask this before treating a quiet market
/// as a broken one.
/// </summary>
/// <remarks>
/// Until 2026-09-14 this knew only weekends. That day was Ganesh Chaturthi: the
/// morning job restarted the API and waited for a broker sign-in until 14:30,
/// and every feed reported a closed market as an open one that had gone silent.
/// </remarks>
public class MarketSessionService : IMarketSessionService
{
    private static readonly TimeOnly EquityOpen = new(9, 15);
    private static readonly TimeOnly EquityClose = new(15, 30);

    // MCX runs from 09:00 into the night. Its close tracks the US energy and
    // metals markets, so it moves with American daylight saving: 23:30 IST while
    // New York is on standard time, 23:55 while it is on DST. Agricultural
    // contracts close at 17:00 and are not modelled; nothing here trades them.
    private static readonly TimeOnly McxOpen = new(9, 0);
    private static readonly TimeOnly McxCloseStandard = new(23, 30);
    private static readonly TimeOnly McxCloseDaylight = new(23, 55);

    // MCX publishes its holidays per session: a holiday can close the morning
    // half and still trade the evening, or the other way round.
    private static readonly TimeOnly McxSessionSplit = new(17, 0);

    // Longer than any run of weekends and holidays an Indian exchange has had.
    private const int NextOpenSearchDays = 30;

    private readonly IMarketCalendar _calendar;

    public MarketSessionService(IMarketCalendar calendar)
    {
        _calendar = calendar;
    }

    public MarketSessionInfo GetSessionInfo(
        DateTime utcNow,
        string exchange,
        string segment)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            throw new ArgumentException("Exchange is required.", nameof(exchange));

        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("Segment is required.", nameof(segment));

        var market = exchange.Trim().ToUpperInvariant();
        var seg = segment.Trim().ToUpperInvariant();

        bool supported = market switch
        {
            // Cash and equity derivatives trade the same hours and share one list.
            "NSE" or "BSE" => seg is "CM" or "FO",
            // COM is what MCX calls the segment; CM is accepted too because the
            // console's default query string carries it.
            "MCX" => true,
            _ => false,
        };

        if (!supported)
            throw new NotSupportedException(
                $"Market session rules are not configured yet for exchange '{market}' and segment '{seg}'.");

        var zone = IstTime.Zone;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        var today = DateOnly.FromDateTime(localNow);

        var windows = WindowsOn(market, today);
        var holiday = _calendar.HolidayOn(market, today);
        var special = _calendar.SpecialSessionOn(market, today);

        // A closed day still reports its usual hours, so "opens at" and "closes
        // at" read sensibly on a page; IsTradingDay says whether they apply.
        var (dayOpen, dayClose) = windows.Count > 0
            ? (windows[0].Open, windows[^1].Close)
            : NominalHours(market, today);

        return new MarketSessionInfo
        {
            Exchange = market,
            Segment = seg,
            UtcNow = utcNow,
            LocalNow = localNow,
            IsTradingDay = windows.Count > 0,
            IsMarketOpen = windows.Any(w => localNow >= w.Open && localNow < w.Close),
            SessionOpenUtc = TimeZoneInfo.ConvertTimeToUtc(dayOpen, zone),
            SessionCloseUtc = TimeZoneInfo.ConvertTimeToUtc(dayClose, zone),
            NextMarketOpenUtc = TimeZoneInfo.ConvertTimeToUtc(NextOpen(market, localNow), zone),
            TimeZoneId = zone.Id,
            IsHoliday = holiday is not null,
            HolidayName = holiday?.Name,
            HolidayClosure = holiday?.Closure.ToString(),
            SpecialSessionName = special?.Name,
            CalendarWarning = CalendarWarning(market, today),
        };
    }

    public bool IsMarketOpen(
        DateTime utcNow,
        string exchange,
        string segment)
    {
        return GetSessionInfo(utcNow, exchange, segment).IsMarketOpen;
    }

    public DateTime GetNextMarketOpenUtc(
        DateTime utcNow,
        string exchange,
        string segment)
    {
        return GetSessionInfo(utcNow, exchange, segment).NextMarketOpenUtc;
    }

    /// <summary>
    /// The IST windows the exchange trades on a date, in order. Empty when it does
    /// not trade at all. A special session replaces everything else that day.
    /// </summary>
    private List<(DateTime Open, DateTime Close)> WindowsOn(string market, DateOnly date)
    {
        var special = _calendar.SpecialSessionOn(market, date);
        if (special is not null)
            return [(date.ToDateTime(special.OpenIst), date.ToDateTime(special.CloseIst))];

        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return [];

        var closure = _calendar.HolidayOn(market, date)?.Closure;
        if (closure == MarketClosure.FullDay)
            return [];

        if (market != "MCX")
            return [(date.ToDateTime(EquityOpen), date.ToDateTime(EquityClose))];

        var close = McxCloseFor(date);
        return closure switch
        {
            MarketClosure.MorningSession => [(date.ToDateTime(McxSessionSplit), date.ToDateTime(close))],
            MarketClosure.EveningSession => [(date.ToDateTime(McxOpen), date.ToDateTime(McxSessionSplit))],
            _ => [(date.ToDateTime(McxOpen), date.ToDateTime(close))],
        };
    }

    private static (DateTime Open, DateTime Close) NominalHours(string market, DateOnly date)
        => market == "MCX"
            ? (date.ToDateTime(McxOpen), date.ToDateTime(McxCloseFor(date)))
            : (date.ToDateTime(EquityOpen), date.ToDateTime(EquityClose));

    /// <summary>The first session start after <paramref name="localNow"/>, today included.</summary>
    private DateTime NextOpen(string market, DateTime localNow)
    {
        var today = DateOnly.FromDateTime(localNow);
        for (int offset = 0; offset <= NextOpenSearchDays; offset++)
        {
            foreach (var (open, _) in WindowsOn(market, today.AddDays(offset)))
            {
                if (open > localNow) return open;
            }
        }

        // A month with no session at all is a broken calendar, not a real one.
        // Answer with the weekday rule rather than with nothing.
        var next = today.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return next.ToDateTime(market == "MCX" ? McxOpen : EquityOpen);
    }

    /// <summary>
    /// Why today's answer may be wrong: the calendar did not load, or has no list
    /// for this year. In December it also warns that next year's list is missing,
    /// while there is still time to add it.
    /// </summary>
    private string? CalendarWarning(string market, DateOnly today)
    {
        if (!_calendar.IsLoaded)
            return "The holiday calendar could not be loaded; only weekends are known to be closed.";

        if (!_calendar.HasYear(market, today.Year))
            return $"No {market} holiday calendar is loaded for {today.Year}; only weekends are known to be closed.";

        if (today.Month == 12 && !_calendar.HasYear(market, today.Year + 1))
            return $"The {market} holiday calendar for {today.Year + 1} is not loaded yet.";

        return null;
    }

    /// <summary>
    /// MCX's evening close on a date, which follows New York rather than India.
    /// </summary>
    /// <remarks>
    /// Asked of the OS instead of hardcoding "second Sunday in March": the rule
    /// has been changed by legislation before and the timezone database is the
    /// thing that gets updated when it is. If the zone is missing the answer
    /// falls back to the earlier close, which can only ever say "closed" a few
    /// minutes early - the safe direction to be wrong in.
    /// </remarks>
    private static TimeOnly McxCloseFor(DateOnly date)
    {
        try
        {
            TimeZoneInfo newYork;
            try
            {
                newYork = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            }
            catch (TimeZoneNotFoundException)
            {
                newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            }

            var middayUtc = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(12, 0)), IstTime.Zone);
            return newYork.IsDaylightSavingTime(TimeZoneInfo.ConvertTimeFromUtc(middayUtc, newYork))
                ? McxCloseDaylight
                : McxCloseStandard;
        }
        catch (TimeZoneNotFoundException)
        {
            return McxCloseStandard;
        }
    }
}
