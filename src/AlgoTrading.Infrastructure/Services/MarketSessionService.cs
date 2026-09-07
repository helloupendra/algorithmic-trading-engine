using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.ValueObjects;


namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Evaluates market trading hours and holidays (e.g., NSE Equity) using localized timezone arithmetic.
/// Vital for pausing live ingestion loops and avoiding unnecessary API calls during closed hours.
/// </summary>
public class MarketSessionService : IMarketSessionService
{
    private static readonly TimeOnly NseCmOpen = new(9, 15);
    private static readonly TimeOnly NseCmClose = new(15, 30);

    // MCX runs a single session from 09:00 into the night. Its close tracks the
    // US energy and metals markets, so it moves with American daylight saving:
    // 23:30 IST while New York is on standard time, 23:55 while it is on DST.
    // Both are the exchange's published non-agri timings; agricultural
    // contracts close at 17:00 and are not modelled here because nothing on this
    // platform trades them.
    private static readonly TimeOnly McxOpen = new(9, 0);
    private static readonly TimeOnly McxCloseStandard = new(23, 30);
    private static readonly TimeOnly McxCloseDaylight = new(23, 55);

    public MarketSessionInfo GetSessionInfo(
        DateTime utcNow,
        string exchange,
        string segment)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            throw new ArgumentException("Exchange is required.", nameof(exchange));

        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("Segment is required.", nameof(segment));

        var indiaTz = GetIndiaTimeZone();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, indiaTz);

        var normalizedExchange = exchange.Trim().ToUpperInvariant();
        var normalizedSegment = segment.Trim().ToUpperInvariant();

        if (normalizedExchange == "NSE" && normalizedSegment == "CM")
        {
            return BuildNseCmSessionInfo(utcNow, localNow, indiaTz);
        }

        // COM is what MCX calls the segment; CM is accepted too because the
        // console's default query string carries it and a commodity page asking
        // about MCX plainly means the commodity session.
        if (normalizedExchange == "MCX")
        {
            return BuildMcxSessionInfo(utcNow, localNow, indiaTz, normalizedSegment);
        }

        throw new NotSupportedException(
            $"Market session rules are not configured yet for exchange '{normalizedExchange}' and segment '{normalizedSegment}'.");
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

    private static MarketSessionInfo BuildNseCmSessionInfo(
        DateTime utcNow,
        DateTime localNow,
        TimeZoneInfo indiaTz)
    {
        bool isTradingDay = IsTradingDay(localNow.Date);

        DateTime sessionOpenLocal = localNow.Date.Add(NseCmOpen.ToTimeSpan());
        DateTime sessionCloseLocal = localNow.Date.Add(NseCmClose.ToTimeSpan());

        DateTime sessionOpenUtc = TimeZoneInfo.ConvertTimeToUtc(sessionOpenLocal, indiaTz);
        DateTime sessionCloseUtc = TimeZoneInfo.ConvertTimeToUtc(sessionCloseLocal, indiaTz);

        bool isMarketOpen =
            isTradingDay &&
            localNow >= sessionOpenLocal &&
            localNow < sessionCloseLocal;

        DateTime nextOpenLocal = CalculateNextMarketOpenLocal(localNow);
        DateTime nextOpenUtc = TimeZoneInfo.ConvertTimeToUtc(nextOpenLocal, indiaTz);

        return new MarketSessionInfo
        {
            Exchange = "NSE",
            Segment = "CM",
            UtcNow = utcNow,
            LocalNow = localNow,
            IsTradingDay = isTradingDay,
            IsMarketOpen = isMarketOpen,
            SessionOpenUtc = sessionOpenUtc,
            SessionCloseUtc = sessionCloseUtc,
            NextMarketOpenUtc = nextOpenUtc,
            TimeZoneId = indiaTz.Id
        };
    }

    private static MarketSessionInfo BuildMcxSessionInfo(
        DateTime utcNow,
        DateTime localNow,
        TimeZoneInfo indiaTz,
        string segment)
    {
        bool isTradingDay = IsTradingDay(localNow.Date);
        TimeOnly close = McxCloseFor(utcNow);

        DateTime sessionOpenLocal = localNow.Date.Add(McxOpen.ToTimeSpan());
        DateTime sessionCloseLocal = localNow.Date.Add(close.ToTimeSpan());

        bool isMarketOpen =
            isTradingDay &&
            localNow >= sessionOpenLocal &&
            localNow < sessionCloseLocal;

        DateTime nextOpenLocal;
        if (isTradingDay && localNow < sessionOpenLocal)
        {
            nextOpenLocal = sessionOpenLocal;
        }
        else
        {
            nextOpenLocal = NextTradingDay(localNow.Date).Add(McxOpen.ToTimeSpan());
        }

        return new MarketSessionInfo
        {
            Exchange = "MCX",
            Segment = string.IsNullOrWhiteSpace(segment) ? "COM" : segment,
            UtcNow = utcNow,
            LocalNow = localNow,
            IsTradingDay = isTradingDay,
            IsMarketOpen = isMarketOpen,
            SessionOpenUtc = TimeZoneInfo.ConvertTimeToUtc(sessionOpenLocal, indiaTz),
            SessionCloseUtc = TimeZoneInfo.ConvertTimeToUtc(sessionCloseLocal, indiaTz),
            NextMarketOpenUtc = TimeZoneInfo.ConvertTimeToUtc(nextOpenLocal, indiaTz),
            TimeZoneId = indiaTz.Id
        };
    }

    /// <summary>
    /// MCX's evening close, which follows New York rather than the calendar.
    /// </summary>
    /// <remarks>
    /// Asked of the OS instead of hardcoding "second Sunday in March": the rule
    /// has been changed by legislation before and the timezone database is the
    /// thing that gets updated when it is. If the zone is missing the answer
    /// falls back to the earlier close, which can only ever say "closed" a few
    /// minutes early - the safe direction to be wrong in.
    /// </remarks>
    private static TimeOnly McxCloseFor(DateTime utcNow)
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

            return newYork.IsDaylightSavingTime(
                TimeZoneInfo.ConvertTimeFromUtc(utcNow, newYork))
                ? McxCloseDaylight
                : McxCloseStandard;
        }
        catch (TimeZoneNotFoundException)
        {
            return McxCloseStandard;
        }
    }

    private static bool IsTradingDay(DateTime localDate)
    {
        return localDate.DayOfWeek != DayOfWeek.Saturday &&
               localDate.DayOfWeek != DayOfWeek.Sunday;
    }

    private static DateTime CalculateNextMarketOpenLocal(DateTime localNow)
    {
        DateTime todayOpen = localNow.Date.Add(NseCmOpen.ToTimeSpan());
        DateTime todayClose = localNow.Date.Add(NseCmClose.ToTimeSpan());

        // If today is a trading day and we are before session open,
        // next market open is today at 09:15 IST.
        if (IsTradingDay(localNow.Date) && localNow < todayOpen)
        {
            return todayOpen;
        }

        // If today is a trading day and session is already open,
        // next market open means the next trading day's session open.
        if (IsTradingDay(localNow.Date) && localNow >= todayOpen && localNow < todayClose)
        {
            return NextTradingDay(localNow.Date).Add(NseCmOpen.ToTimeSpan());
        }

        // If after session close or non-trading day,
        // move to the next trading day at 09:15 IST.
        return NextTradingDay(localNow.Date).Add(NseCmOpen.ToTimeSpan());
    }

    private static DateTime NextTradingDay(DateTime localDate)
    {
        DateTime next = localDate.AddDays(1);

        while (!IsTradingDay(next))
        {
            next = next.AddDays(1);
        }

        return next;
    }

    private static TimeZoneInfo GetIndiaTimeZone()
    {
        // Windows
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            // Linux/macOS
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }
}
