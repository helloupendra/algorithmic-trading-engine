using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketCalendar;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The exchanges' holiday calendars. Everyone signed in can read them; admins
/// change them when an exchange amends its list mid-year (an election day, a
/// cancelled holiday). Every change reloads the in-memory calendar at once, so
/// the next session check already answers with it.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MarketCalendarController : ControllerBase
{
    /// <summary>The exchanges the platform keeps a calendar for.</summary>
    public static readonly string[] Exchanges = ["NSE", "BSE", "MCX"];

    private readonly TradingDbContext _dbContext;
    private readonly IMarketCalendar _calendar;
    private readonly IMarketSessionService _sessions;

    public MarketCalendarController(TradingDbContext dbContext, IMarketCalendar calendar, IMarketSessionService sessions)
    {
        _dbContext = dbContext;
        _calendar = calendar;
        _sessions = sessions;
    }

    /// <summary>One year (default: this IST year) of holidays and special sessions, with each exchange's coverage.</summary>
    [HttpGet]
    public async Task<ActionResult<MarketCalendarResponse>> Get([FromQuery] int? year, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var today = IstTime.DateOf(nowUtc);
        int y = year ?? today.Year;
        if (y < 2000 || y > 2100) return BadRequest(new { message = "Year must be between 2000 and 2100." });

        // Small tables (tens of rows a year per exchange): read them whole and
        // shape in memory rather than lean on date-part translation.
        var holidays = await _dbContext.MarketHolidays.AsNoTracking().ToListAsync(cancellationToken);
        var sessions = await _dbContext.MarketSpecialSessions.AsNoTracking().ToListAsync(cancellationToken);

        var response = new MarketCalendarResponse
        {
            Year = y,
            Holidays = holidays.Where(x => x.Date.Year == y)
                .OrderBy(x => x.Date).ThenBy(x => x.Exchange).Select(ToDto).ToList(),
            SpecialSessions = sessions.Where(x => x.Date.Year == y)
                .OrderBy(x => x.Date).ThenBy(x => x.Exchange).Select(ToDto).ToList(),
        };

        foreach (var exchange in Exchanges)
        {
            var own = holidays.Where(x => MarketCalendar.NormalizeExchange(x.Exchange) == exchange).ToList();
            response.Exchanges.Add(new MarketCalendarExchangeStatus
            {
                Exchange = exchange,
                YearsLoaded = own.Select(x => x.Date.Year).Distinct().Order().ToList(),
                Warning = _sessions.GetSessionInfo(nowUtc, exchange, exchange == "MCX" ? "COM" : "CM").CalendarWarning,
                Today = own.Where(x => x.Date == today).Select(ToDto).FirstOrDefault(),
                Next = own.Where(x => x.Date > today).OrderBy(x => x.Date).Select(ToDto).FirstOrDefault(),
            });
        }

        return Ok(response);
    }

    [HttpPost("holidays")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<ActionResult<MarketHolidayDto>> SaveHoliday([FromBody] SaveMarketHolidayRequest? request, CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "A holiday is required." });

        var exchange = MarketCalendar.NormalizeExchange(request.Exchange);
        if (!Exchanges.Contains(exchange)) return BadRequest(new { message = $"Exchange must be one of {string.Join(", ", Exchanges)}." });
        if (!TryParseDate(request.Date, out var date)) return BadRequest(new { message = "Date must be yyyy-MM-dd." });

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 120) return BadRequest(new { message = "Name is required (at most 120 characters)." });

        var closure = MarketClosure.FullDay;
        if (!string.IsNullOrWhiteSpace(request.Closure) && !Enum.TryParse(request.Closure, ignoreCase: true, out closure))
            return BadRequest(new { message = "Closure must be FullDay, MorningSession or EveningSession." });
        // Only MCX splits its day; an equity exchange is open or it is not.
        if (closure != MarketClosure.FullDay && exchange != "MCX")
            return BadRequest(new { message = $"{exchange} closes whole days; only MCX has morning and evening closures." });

        var source = (request.Source ?? string.Empty).Trim();
        if (source.Length is 0 or > 200) return BadRequest(new { message = "Source is required: the circular this date comes from (at most 200 characters)." });

        var row = await _dbContext.MarketHolidays.FirstOrDefaultAsync(x => x.Exchange == exchange && x.Date == date, cancellationToken);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new MarketHoliday { Exchange = exchange, Date = date, CreatedUtc = now };
            _dbContext.MarketHolidays.Add(row);
        }

        row.Name = name;
        row.Closure = closure;
        row.Source = source;
        row.UpdatedBy = User.GetUserName() ?? "admin";
        row.UpdatedUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _calendar.RefreshAsync(cancellationToken);
        return Ok(ToDto(row));
    }

    [HttpDelete("holidays/{id:long}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> DeleteHoliday(long id, CancellationToken cancellationToken)
    {
        var row = await _dbContext.MarketHolidays.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null) return NotFound(new { message = $"Holiday {id} not found." });

        _dbContext.MarketHolidays.Remove(row);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _calendar.RefreshAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("special-sessions")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<ActionResult<MarketSpecialSessionDto>> SaveSpecialSession([FromBody] SaveMarketSpecialSessionRequest? request, CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest(new { message = "A special session is required." });

        var exchange = MarketCalendar.NormalizeExchange(request.Exchange);
        if (!Exchanges.Contains(exchange)) return BadRequest(new { message = $"Exchange must be one of {string.Join(", ", Exchanges)}." });
        if (!TryParseDate(request.Date, out var date)) return BadRequest(new { message = "Date must be yyyy-MM-dd." });

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 120) return BadRequest(new { message = "Name is required (at most 120 characters)." });

        if (!TimeOnly.TryParseExact(request.OpenIst, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var open) ||
            !TimeOnly.TryParseExact(request.CloseIst, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var close))
            return BadRequest(new { message = "Open and close must be HH:mm (IST)." });
        if (close <= open) return BadRequest(new { message = "The session must close after it opens." });

        var source = (request.Source ?? string.Empty).Trim();
        if (source.Length is 0 or > 200) return BadRequest(new { message = "Source is required: the circular announcing the session (at most 200 characters)." });

        var row = await _dbContext.MarketSpecialSessions.FirstOrDefaultAsync(x => x.Exchange == exchange && x.Date == date, cancellationToken);
        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new MarketSpecialSession { Exchange = exchange, Date = date, CreatedUtc = now };
            _dbContext.MarketSpecialSessions.Add(row);
        }

        row.Name = name;
        row.OpenIst = open;
        row.CloseIst = close;
        row.Source = source;
        row.UpdatedBy = User.GetUserName() ?? "admin";
        row.UpdatedUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _calendar.RefreshAsync(cancellationToken);
        return Ok(ToDto(row));
    }

    [HttpDelete("special-sessions/{id:long}")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> DeleteSpecialSession(long id, CancellationToken cancellationToken)
    {
        var row = await _dbContext.MarketSpecialSessions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (row is null) return NotFound(new { message = $"Special session {id} not found." });

        _dbContext.MarketSpecialSessions.Remove(row);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _calendar.RefreshAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Reloads the in-memory calendar, for a row changed outside the API.</summary>
    [HttpPost("reload")]
    [Authorize(Policy = AuthorizationPolicies.AdminOnly)]
    public async Task<IActionResult> Reload(CancellationToken cancellationToken)
    {
        await _calendar.RefreshAsync(cancellationToken);
        return Ok(new { loaded = _calendar.IsLoaded });
    }

    private static bool TryParseDate(string? text, out DateOnly date)
        => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    internal static MarketHolidayDto ToDto(MarketHoliday x) => new()
    {
        Id = x.Id,
        Exchange = x.Exchange,
        Date = x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Weekday = x.Date.DayOfWeek.ToString(),
        Name = x.Name,
        Closure = x.Closure.ToString(),
        Source = x.Source,
        UpdatedBy = x.UpdatedBy,
        UpdatedUtc = x.UpdatedUtc,
    };

    internal static MarketSpecialSessionDto ToDto(MarketSpecialSession x) => new()
    {
        Id = x.Id,
        Exchange = x.Exchange,
        Date = x.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Weekday = x.Date.DayOfWeek.ToString(),
        Name = x.Name,
        OpenIst = x.OpenIst.ToString("HH:mm", CultureInfo.InvariantCulture),
        CloseIst = x.CloseIst.ToString("HH:mm", CultureInfo.InvariantCulture),
        Source = x.Source,
        UpdatedBy = x.UpdatedBy,
        UpdatedUtc = x.UpdatedUtc,
    };
}
