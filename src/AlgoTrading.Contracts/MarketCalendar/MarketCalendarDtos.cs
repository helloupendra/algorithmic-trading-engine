namespace AlgoTrading.Contracts.MarketCalendar;

/// <summary>One closed (or part-closed) day on an exchange's calendar.</summary>
public class MarketHolidayDto
{
    public long Id { get; set; }
    public string Exchange { get; set; } = string.Empty;

    /// <summary>IST calendar date, yyyy-MM-dd.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>"Monday" etc., so a list reads without a calendar at hand.</summary>
    public string Weekday { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>"FullDay", "MorningSession" or "EveningSession".</summary>
    public string Closure { get; set; } = "FullDay";

    public string? Source { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>A session on special hours (Muhurat trading, a special Saturday).</summary>
public class MarketSpecialSessionDto
{
    public long Id { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Weekday { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>IST, HH:mm.</summary>
    public string OpenIst { get; set; } = string.Empty;

    /// <summary>IST, HH:mm.</summary>
    public string CloseIst { get; set; } = string.Empty;

    public string? Source { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Whether an exchange's calendar can be trusted for today and the months ahead.</summary>
public class MarketCalendarExchangeStatus
{
    public string Exchange { get; set; } = string.Empty;

    /// <summary>Years with at least one holiday loaded.</summary>
    public List<int> YearsLoaded { get; set; } = new();

    /// <summary>Why today's answer may be wrong for this exchange; null when the calendar covers it.</summary>
    public string? Warning { get; set; }

    /// <summary>Today's closure on this exchange, if any.</summary>
    public MarketHolidayDto? Today { get; set; }

    /// <summary>The next closed or part-closed weekday from tomorrow on.</summary>
    public MarketHolidayDto? Next { get; set; }
}

/// <summary>GET /api/MarketCalendar — one year of every exchange's calendar.</summary>
public class MarketCalendarResponse
{
    public int Year { get; set; }
    public List<MarketHolidayDto> Holidays { get; set; } = new();
    public List<MarketSpecialSessionDto> SpecialSessions { get; set; } = new();
    public List<MarketCalendarExchangeStatus> Exchanges { get; set; } = new();
}

/// <summary>Adds or corrects the holiday on (exchange, date).</summary>
public class SaveMarketHolidayRequest
{
    public string Exchange { get; set; } = string.Empty;

    /// <summary>yyyy-MM-dd, IST.</summary>
    public string Date { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>"FullDay" (default); "MorningSession" or "EveningSession" for MCX only.</summary>
    public string? Closure { get; set; }

    /// <summary>The circular it comes from. Required: a date nobody can trace is a date nobody can check.</summary>
    public string? Source { get; set; }
}

/// <summary>Adds or corrects the special session on (exchange, date).</summary>
public class SaveMarketSpecialSessionRequest
{
    public string Exchange { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>HH:mm, IST.</summary>
    public string OpenIst { get; set; } = string.Empty;

    /// <summary>HH:mm, IST.</summary>
    public string CloseIst { get; set; } = string.Empty;

    public string? Source { get; set; }
}
