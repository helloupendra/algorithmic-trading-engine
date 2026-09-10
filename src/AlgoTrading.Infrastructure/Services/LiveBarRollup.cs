using System;
using System.Collections.Generic;
using System.Linq;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Builds coarser candles out of live 1-minute bars.
/// </summary>
/// <remarks>
/// Buckets are aligned on UTC wall-clock multiples of the resolution, which
/// lines up with every Indian session: the NSE/BSE open at 09:15 IST is
/// 03:45 UTC (225 minutes), the MCX open at 09:00 IST is 03:30 UTC (210), and
/// both divide by 5 and by 15. So a 5-minute candle stamped 09:15 covers
/// 09:15–09:19, the same bucket the broker's own candles use.
///
/// Open is the first bar's open, close the last bar's close, high and low the
/// extremes, and volume the sum of the bars' volume deltas. A bucket with a
/// gap in its 1-minute bars is still emitted from the bars that exist — a
/// feed hiccup should cost the missing minute, not the whole candle.
/// </remarks>
public static class LiveBarRollup
{
    public static IReadOnlyList<ProviderHistoryBar> Roll(IEnumerable<LiveBar> oneMinuteBars, int minutes)
    {
        if (minutes < 1) throw new ArgumentOutOfRangeException(nameof(minutes));

        var span = TimeSpan.FromMinutes(minutes);
        return oneMinuteBars
            .OrderBy(b => b.BarStartUtc)
            .GroupBy(b => BucketStart(b.BarStartUtc, span))
            .Select(g => new ProviderHistoryBar
            {
                TimestampUtc = g.Key,
                Open = g.First().Open,
                High = g.Max(b => b.High),
                Low = g.Min(b => b.Low),
                Close = g.Last().Close,
                Volume = g.Sum(b => b.VolumeDelta),
            })
            .ToList();
    }

    public static DateTime BucketStart(DateTime utc, TimeSpan span)
    {
        var ticks = utc.Ticks - utc.Ticks % span.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }
}
