using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// MCX's evening close follows New York: 23:30 IST while the US is on daylight
/// saving, 23:55 while it is on standard time (MCX circular for 9 Mar 2026).
/// Until 28 Sep the service had them the wrong way round.
/// </summary>
public class McxCloseTests
{
    private static readonly MarketSessionService Sessions = new(new OpenCalendar());

    private static DateTime Ist(int year, int month, int day, int hour, int minute) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    [Fact]
    public void In_the_US_summer_MCX_closes_at_23_30()
    {
        // Monday 28 Sep 2026: New York is on daylight saving until 1 Nov.
        Assert.True(Sessions.IsMarketOpen(Ist(2026, 9, 28, 23, 29), "MCX", "COM"));
        Assert.False(Sessions.IsMarketOpen(Ist(2026, 9, 28, 23, 31), "MCX", "COM"));
    }

    [Fact]
    public void In_the_US_winter_MCX_trades_to_23_55()
    {
        // Monday 7 Dec 2026: New York is on standard time.
        Assert.True(Sessions.IsMarketOpen(Ist(2026, 12, 7, 23, 45), "MCX", "COM"));
        Assert.False(Sessions.IsMarketOpen(Ist(2026, 12, 7, 23, 56), "MCX", "COM"));
    }

    [Fact]
    public void The_change_follows_the_US_clocks_not_a_fixed_date()
    {
        // US daylight saving began on Sunday 8 Mar 2026: Friday 6 Mar still
        // closed at 23:55, Monday 9 Mar at 23:30.
        Assert.True(Sessions.IsMarketOpen(Ist(2026, 3, 6, 23, 45), "MCX", "COM"));
        Assert.False(Sessions.IsMarketOpen(Ist(2026, 3, 9, 23, 45), "MCX", "COM"));
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public bool IsLoaded => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
