using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// The exchanges' official holidays and special sessions, held in memory so
/// that session checks on the tick and heartbeat paths never touch the database.
/// </summary>
public interface IMarketCalendar
{
    /// <summary>
    /// False until the first load succeeds. A calendar that could not be read is
    /// reported as such, never taken to mean "no holidays".
    /// </summary>
    bool IsLoaded { get; }

    /// <summary>The holiday on this IST date, or null on an ordinary day.</summary>
    MarketHoliday? HolidayOn(string exchange, DateOnly date);

    /// <summary>The special session on this IST date, or null.</summary>
    MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date);

    /// <summary>
    /// Whether any holiday is loaded for this exchange in this year — the sign
    /// that the year's circular is in. Every exchange closes on some weekday
    /// each year, so a year with none is a year nobody has loaded.
    /// </summary>
    bool HasYear(string exchange, int year);

    /// <summary>Reloads everything from the database; called at startup and after every change.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
