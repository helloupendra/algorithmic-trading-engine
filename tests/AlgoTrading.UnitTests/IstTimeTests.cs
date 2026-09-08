using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

public class IstTimeTests
{
    [Fact]
    public void Midnight_ist_is_a_date_only_stamp()
    {
        // 18:30 UTC is 00:00 IST the next day — what BSE sends before the first trade.
        var utc = new DateTime(2026, 9, 7, 18, 30, 0, DateTimeKind.Utc);
        Assert.True(IstTime.IsMidnightIst(utc));
    }

    [Theory]
    [InlineData(3, 45)]   // 09:15 IST, the open
    [InlineData(3, 30)]   // 09:00 IST, pre-open
    [InlineData(18, 29)]  // 23:59 IST
    [InlineData(18, 31)]  // 00:01 IST
    public void Any_other_minute_is_a_real_stamp(int utcHour, int utcMinute)
    {
        var utc = new DateTime(2026, 9, 8, utcHour, utcMinute, 0, DateTimeKind.Utc);
        Assert.False(IstTime.IsMidnightIst(utc));
    }
}
