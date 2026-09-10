using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The nightly archive builds 5 and 15 minute candles from live 1-minute
/// bars. The buckets must be the ones the broker uses, and a missing minute
/// must cost the minute, not the candle.
/// </summary>
public class LiveBarRollupTests
{
    private static LiveBar Bar(int hourUtc, int minute, decimal o, decimal h, decimal l, decimal c, long v = 10) => new()
    {
        Symbol = "NSE:NIFTY50-INDEX",
        Resolution = "1m",
        BarStartUtc = new DateTime(2026, 9, 10, hourUtc, minute, 0, DateTimeKind.Utc),
        Open = o, High = h, Low = l, Close = c, VolumeDelta = v,
    };

    [Fact]
    public void Five_minute_buckets_start_at_the_nse_open()
    {
        // 09:15 IST = 03:45 UTC. Bars 09:15..09:19 → one candle stamped 09:15; 09:20 starts the next.
        var bars = new[]
        {
            Bar(3, 45, 100, 101, 99, 100.5m),
            Bar(3, 46, 100.5m, 103, 100, 102),
            Bar(3, 47, 102, 102, 98, 99),
            Bar(3, 48, 99, 100, 98.5m, 99.5m),
            Bar(3, 49, 99.5m, 104, 99, 103),
            Bar(3, 50, 103, 105, 103, 104),
        };

        var rolled = LiveBarRollup.Roll(bars, 5);

        Assert.Equal(2, rolled.Count);
        var first = rolled[0];
        Assert.Equal(new DateTime(2026, 9, 10, 3, 45, 0, DateTimeKind.Utc), first.TimestampUtc);
        Assert.Equal(100m, first.Open);
        Assert.Equal(104m, first.High);
        Assert.Equal(98m, first.Low);
        Assert.Equal(103m, first.Close);
        Assert.Equal(50m, first.Volume);
        Assert.Equal(new DateTime(2026, 9, 10, 3, 50, 0, DateTimeKind.Utc), rolled[1].TimestampUtc);
    }

    [Fact]
    public void Fifteen_minute_buckets_align_with_both_the_nse_and_mcx_opens()
    {
        // 09:15 IST (03:45 UTC) and 09:00 IST (03:30 UTC) are both 15-minute boundaries.
        Assert.Equal(new DateTime(2026, 9, 10, 3, 45, 0, DateTimeKind.Utc),
            LiveBarRollup.BucketStart(new DateTime(2026, 9, 10, 3, 59, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15)));
        Assert.Equal(new DateTime(2026, 9, 10, 3, 30, 0, DateTimeKind.Utc),
            LiveBarRollup.BucketStart(new DateTime(2026, 9, 10, 3, 44, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void A_missing_minute_does_not_lose_the_candle()
    {
        var bars = new[] { Bar(3, 45, 100, 101, 99, 100), Bar(3, 49, 100, 106, 100, 105) };
        var rolled = LiveBarRollup.Roll(bars, 5);
        Assert.Single(rolled);
        Assert.Equal(100m, rolled[0].Open);
        Assert.Equal(105m, rolled[0].Close);
        Assert.Equal(106m, rolled[0].High);
    }

    [Fact]
    public void One_minute_rollup_is_the_bars_themselves()
    {
        var bars = new[] { Bar(3, 45, 1, 2, 0.5m, 1.5m, 7), Bar(3, 46, 1.5m, 3, 1, 2, 8) };
        var rolled = LiveBarRollup.Roll(bars, 1);
        Assert.Equal(2, rolled.Count);
        Assert.Equal(7m, rolled[0].Volume);
        Assert.Equal(2m, rolled[1].Close);
    }

    [Fact]
    public void Out_of_order_input_is_sorted_before_open_and_close_are_taken()
    {
        var bars = new[] { Bar(3, 47, 102, 102, 98, 99), Bar(3, 45, 100, 101, 99, 100.5m) };
        var rolled = LiveBarRollup.Roll(bars, 5);
        Assert.Equal(100m, rolled[0].Open);
        Assert.Equal(99m, rolled[0].Close);
    }
}
