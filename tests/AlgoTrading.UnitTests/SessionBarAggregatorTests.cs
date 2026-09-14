using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Candles must be the ones a chart draws: anchored on the exchange's open,
/// closed only when their last minute has closed, and honest about gaps.
/// </summary>
public class SessionBarAggregatorTests
{
    /// <summary>IST wall time → UTC.</summary>
    private static DateTime Ist(int h, int m, int s = 0) => new DateTime(2026, 9, 15, h, m, s, DateTimeKind.Utc).AddMinutes(-330);

    private static readonly SessionWindow Nse = new(Ist(9, 15), Ist(15, 30));
    private static readonly SessionWindow Mcx = new(Ist(9, 0), Ist(23, 30));

    /// <summary>One bar per minute from <paramref name="from"/> for <paramref name="count"/> minutes; price rises 1 a minute.</summary>
    private static List<MinuteBar> Minutes(DateTime from, int count, decimal start = 100m)
        => Enumerable.Range(0, count)
            .Select(i => new MinuteBar(from.AddMinutes(i), start + i, start + i + 0.5m, start + i - 0.5m, start + i + 0.25m))
            .ToList();

    [Fact]
    public void Nse_fifteen_minute_candles_start_at_0915_0930_and_carry_ohlc_of_their_minutes()
    {
        var bars = SessionBarAggregator.Aggregate(Minutes(Ist(9, 15), 30), Nse, 15, Ist(9, 45));

        Assert.Equal(2, bars.Count);
        Assert.Equal(Ist(9, 15), bars[0].StartUtc);
        Assert.Equal(Ist(9, 30), bars[0].EndUtc);
        Assert.Equal(Ist(9, 30), bars[1].StartUtc);
        Assert.Equal(100m, bars[0].Open);          // first minute's open
        Assert.Equal(114.5m, bars[0].High);        // minute 14's high
        Assert.Equal(99.5m, bars[0].Low);          // minute 0's low
        Assert.Equal(114.25m, bars[0].Close);      // minute 14's close
        Assert.Equal(15, bars[0].MinutesWithData);
        Assert.All(bars, b => Assert.True(b.IsClosed));
    }

    [Fact]
    public void Nse_thirty_and_sixty_minute_candles_anchor_on_the_open_not_on_the_utc_clock()
    {
        var minutes = Minutes(Ist(9, 15), 120);
        var thirty = SessionBarAggregator.Aggregate(minutes, Nse, 30, Ist(11, 15));
        Assert.Equal([Ist(9, 15), Ist(9, 45), Ist(10, 15), Ist(10, 45)], thirty.Select(b => b.StartUtc).ToArray());

        var sixty = SessionBarAggregator.Aggregate(minutes, Nse, 60, Ist(11, 15));
        Assert.Equal([Ist(9, 15), Ist(10, 15)], sixty.Select(b => b.StartUtc).ToArray());
        Assert.Equal(Ist(10, 15), sixty[0].EndUtc);
    }

    [Fact]
    public void Mcx_candles_anchor_on_0900()
    {
        var minutes = Minutes(Ist(9, 0), 60);
        Assert.Equal([Ist(9, 0), Ist(9, 15), Ist(9, 30), Ist(9, 45)],
            SessionBarAggregator.Aggregate(minutes, Mcx, 15, Ist(10, 0)).Select(b => b.StartUtc).ToArray());
        Assert.Equal([Ist(9, 0)], SessionBarAggregator.Aggregate(minutes, Mcx, 60, Ist(10, 0)).Select(b => b.StartUtc).ToArray());
        Assert.Equal([Ist(9, 0), Ist(9, 3), Ist(9, 6)],
            SessionBarAggregator.Aggregate(Minutes(Ist(9, 0), 9), Mcx, 3, Ist(9, 9)).Select(b => b.StartUtc).ToArray());
    }

    [Fact]
    public void The_last_nse_hourly_candle_is_the_fifteen_minutes_left_before_the_close()
    {
        var bars = SessionBarAggregator.Aggregate(Minutes(Ist(14, 15), 75), Nse, 60, Ist(15, 30));

        Assert.Equal(2, bars.Count);
        var last = bars[1];
        Assert.Equal(Ist(15, 15), last.StartUtc);
        Assert.Equal(Ist(15, 30), last.EndUtc);
        Assert.Equal(15, last.MinutesExpected);
        Assert.True(last.IsClosed);
        Assert.Equal(6, last.Index); // 09:15, 10:15, … 15:15
    }

    [Fact]
    public void A_candle_is_forming_until_the_clock_reaches_the_end_of_its_last_minute()
    {
        var minutes = Minutes(Ist(9, 15), 15);

        var justBefore = SessionBarAggregator.Aggregate(minutes, Nse, 15, Ist(9, 30).AddTicks(-1));
        Assert.False(justBefore.Single().IsClosed);
        Assert.Equal(14, justBefore.Single().MinutesElapsed(Ist(9, 30).AddTicks(-1)));

        var atClose = SessionBarAggregator.Aggregate(minutes, Nse, 15, Ist(9, 30));
        Assert.True(atClose.Single().IsClosed);
        Assert.Equal(15, atClose.Single().MinutesElapsed(Ist(9, 30)));
    }

    [Fact]
    public void A_forming_candle_reports_the_minutes_elapsed_so_far()
    {
        var bars = SessionBarAggregator.Aggregate(Minutes(Ist(10, 30), 8), Nse, 15, Ist(10, 37, 30));
        var forming = bars.Single();
        Assert.False(forming.IsClosed);
        Assert.Equal(7, forming.MinutesElapsed(Ist(10, 37, 30)));
        Assert.Equal(15, forming.MinutesExpected);
        Assert.Equal(8, forming.MinutesWithData); // the 10:37 minute has started and has a bar
    }

    [Fact]
    public void Missing_minutes_still_make_a_candle_and_are_counted()
    {
        var minutes = Minutes(Ist(9, 15), 15);
        minutes.RemoveAt(7);
        minutes.RemoveAt(3);

        var bar = SessionBarAggregator.Aggregate(minutes, Nse, 15, Ist(9, 30)).Single();
        Assert.Equal(13, bar.MinutesWithData);
        Assert.Equal(15, bar.MinutesExpected);
        Assert.True(bar.IsClosed);
    }

    [Fact]
    public void A_candle_with_no_minutes_is_absent_and_the_indices_show_the_hole()
    {
        var minutes = Minutes(Ist(9, 15), 15).Concat(Minutes(Ist(9, 45), 15)).ToList();
        var bars = SessionBarAggregator.Aggregate(minutes, Nse, 15, Ist(10, 0));
        Assert.Equal([0, 2], bars.Select(b => b.Index).ToArray());
    }

    [Fact]
    public void Pre_open_and_after_close_minutes_belong_to_no_candle()
    {
        var minutes = Minutes(Ist(9, 0), 15)          // NSE pre-open 09:00–09:14
            .Concat(Minutes(Ist(9, 15), 15))
            .Concat(Minutes(Ist(15, 30), 5))          // after the close
            .ToList();

        var bars = SessionBarAggregator.Aggregate(minutes, Nse, 15, Ist(16, 0));
        var only = Assert.Single(bars);
        Assert.Equal(Ist(9, 15), only.StartUtc);
        Assert.Equal(15, only.MinutesWithData);
    }

    [Fact]
    public void Current_bucket_is_known_even_before_its_first_minute_arrives()
    {
        var bucket = SessionBarAggregator.CurrentBucket(Nse, 15, Ist(10, 31));
        Assert.NotNull(bucket);
        Assert.Equal(Ist(10, 30), bucket!.Value.StartUtc);
        Assert.Equal(Ist(10, 45), bucket.Value.EndUtc);
        Assert.Null(SessionBarAggregator.CurrentBucket(Nse, 15, Ist(15, 30)));
        Assert.Null(SessionBarAggregator.CurrentBucket(Nse, 15, Ist(9, 14)));
    }

    [Fact]
    public void Session_windows_from_the_market_session_service_align_nse_at_0915_and_mcx_at_0900()
    {
        var sessions = new MarketSessionService(new EmptyCalendar());
        var nse = sessions.GetSessionInfo(Ist(10, 0), "NSE", "CM");
        var mcx = sessions.GetSessionInfo(Ist(10, 0), "MCX", "COM");

        Assert.Equal(Ist(9, 15), nse.SessionOpenUtc);
        Assert.Equal(Ist(15, 30), nse.SessionCloseUtc);
        Assert.Equal(Ist(9, 0), mcx.SessionOpenUtc);

        var window = new SessionWindow(nse.SessionOpenUtc, nse.SessionCloseUtc);
        Assert.Equal(Ist(9, 30), SessionBarAggregator.CurrentBucket(window, 15, Ist(9, 44))!.Value.StartUtc);
    }

    private sealed class EmptyCalendar : IMarketCalendar
    {
        public bool IsLoaded => true;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
