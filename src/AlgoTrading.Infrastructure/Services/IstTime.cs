// src/AlgoTrading.Infrastructure/Services/IstTime.cs
using System.Globalization;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// India Standard Time conversions shared by the session-aware code paths
/// (backtest date ranges, daily P&amp;L buckets, coverage session counts).
/// Same zone lookup as MarketHoursService: "India Standard Time" on Windows,
/// "Asia/Kolkata" elsewhere, with a fixed +05:30 fallback when tzdata is missing.
/// </summary>
public static class IstTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(330);

    /// <summary>Regular NSE/BSE session bounds.</summary>
    public static readonly TimeSpan SessionOpen = new(9, 15, 0);
    public static readonly TimeSpan SessionClose = new(15, 30, 0);

    public static readonly TimeZoneInfo Zone = ResolveZone();

    public static DateTime ToIst(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>The IST calendar day a UTC instant falls on.</summary>
    public static DateOnly DateOf(DateTime utc) => DateOnly.FromDateTime(ToIst(utc));

    /// <summary>00:00 IST of the given day, as UTC.</summary>
    public static DateTime StartOfDayUtc(DateOnly istDate)
        => FromIst(istDate.ToDateTime(TimeOnly.MinValue));

    /// <summary>23:59:59 IST of the given day, as UTC.</summary>
    public static DateTime EndOfDayUtc(DateOnly istDate)
        => FromIst(istDate.ToDateTime(new TimeOnly(23, 59, 59)));

    /// <summary>An IST wall-clock time (Kind unspecified) to UTC.</summary>
    public static DateTime FromIst(DateTime istLocal)
    {
        var unspecified = DateTime.SpecifyKind(istLocal, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, Zone);
    }

    /// <summary>"yyyy-MM-dd" of the IST day.</summary>
    public static string DateString(DateTime utc)
        => DateOf(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"dd MMM HH:mm" IST, for log-style text.</summary>
    public static string ShortStamp(DateTime utc)
        => ToIst(utc).ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);

    private static TimeZoneInfo ResolveZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", Offset, "India Standard Time", "India Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("IST", Offset, "India Standard Time", "India Standard Time");
        }
    }
}
