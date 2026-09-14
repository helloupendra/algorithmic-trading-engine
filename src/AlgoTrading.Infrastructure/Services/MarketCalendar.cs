using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// <see cref="IMarketCalendar"/> over the <c>market_holidays</c> and
/// <c>market_special_sessions</c> tables. One immutable snapshot is swapped in
/// whole on every refresh, so a reader never sees half a reload.
/// </summary>
public sealed class MarketCalendar : IMarketCalendar
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MarketCalendar> _logger;
    private volatile Snapshot _snapshot = Snapshot.Empty;

    public MarketCalendar(IServiceScopeFactory scopeFactory, ILogger<MarketCalendar> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public bool IsLoaded => _snapshot.Loaded;

    public MarketHoliday? HolidayOn(string exchange, DateOnly date)
        => _snapshot.Holidays.GetValueOrDefault((NormalizeExchange(exchange), date));

    public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date)
        => _snapshot.Sessions.GetValueOrDefault((NormalizeExchange(exchange), date));

    public bool HasYear(string exchange, int year)
        => _snapshot.Years.Contains((NormalizeExchange(exchange), year));

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var holidays = await db.MarketHolidays.AsNoTracking().ToListAsync(cancellationToken);
        var sessions = await db.MarketSpecialSessions.AsNoTracking().ToListAsync(cancellationToken);

        _snapshot = Snapshot.From(holidays, sessions);
        _logger.LogInformation(
            "Market calendar loaded: {Holidays} holiday(s), {Sessions} special session(s), years {Years}.",
            holidays.Count, sessions.Count,
            string.Join(", ", _snapshot.Years.OrderBy(y => y.Exchange).ThenBy(y => y.Year).Select(y => $"{y.Exchange} {y.Year}")));
    }

    /// <summary>"nse " → "NSE". Every key in and out of the calendar goes through this.</summary>
    public static string NormalizeExchange(string exchange) => (exchange ?? string.Empty).Trim().ToUpperInvariant();

    private sealed record Snapshot(
        bool Loaded,
        IReadOnlyDictionary<(string Exchange, DateOnly Date), MarketHoliday> Holidays,
        IReadOnlyDictionary<(string Exchange, DateOnly Date), MarketSpecialSession> Sessions,
        IReadOnlySet<(string Exchange, int Year)> Years)
    {
        public static readonly Snapshot Empty = new(
            false,
            new Dictionary<(string, DateOnly), MarketHoliday>(),
            new Dictionary<(string, DateOnly), MarketSpecialSession>(),
            new HashSet<(string, int)>());

        public static Snapshot From(IEnumerable<MarketHoliday> holidays, IEnumerable<MarketSpecialSession> sessions)
        {
            var byDay = new Dictionary<(string, DateOnly), MarketHoliday>();
            foreach (var h in holidays) byDay[(NormalizeExchange(h.Exchange), h.Date)] = h;

            var sessionsByDay = new Dictionary<(string, DateOnly), MarketSpecialSession>();
            foreach (var s in sessions) sessionsByDay[(NormalizeExchange(s.Exchange), s.Date)] = s;

            var years = byDay.Keys.Select(k => (k.Item1, k.Item2.Year)).ToHashSet();
            return new Snapshot(true, byDay, sessionsByDay, years);
        }
    }
}
