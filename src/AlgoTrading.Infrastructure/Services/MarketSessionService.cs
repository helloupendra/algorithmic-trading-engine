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
