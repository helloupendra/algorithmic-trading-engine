using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Which expiry a chain read gets when it names none. Since 30 Oct 2026 the
/// recorder captures some underlyings' later expiries too, a few seconds after
/// the nearest in the same round, so "the newest capture" is no longer the
/// chain the strategies mean. Every read that names no expiry must get the
/// nearest one, and never add two expiries' open interest together.
/// </summary>
public class OptionChainDefaultExpiryTests
{
    // Friday 30 Oct 2026. NIFTY's nearest is Tuesday's weekly; the recorder
    // also captures the next weekly and the November monthly (the 23rd: the
    // 24th is Guru Nanak Jayanti).
    private static readonly DateOnly Nearest = new(2026, 11, 3);
    private static readonly DateOnly Next = new(2026, 11, 10);
    private static readonly DateOnly Monthly = new(2026, 11, 23);
    private static readonly DateTime Open = new(2026, 10, 30, 3, 45, 0, DateTimeKind.Utc); // 09:15 IST

    private static readonly decimal[] Strikes = [24000m, 24100m, 24200m];

    [Fact]
    public async Task A_chain_read_that_names_no_expiry_gets_the_nearest_even_when_a_later_one_was_captured_last()
    {
        await using var db = Db();
        Capture(db, Nearest, Open, oi: 1_000);
        Capture(db, Next, Open.AddSeconds(4), oi: 50_000);
        await db.SaveChangesAsync();
        var service = new OptionChainService(db);

        var chain = await service.GetChainAsync("NIFTY", null, null);

        Assert.Equal(Nearest, chain.ExpiryDate);
        Assert.Equal(Open, chain.AsOfUtc);
        Assert.Equal(3 * 1_000, chain.TotalCallOpenInterest);
        Assert.All(chain.Strikes, s => Assert.Contains("261103", s.Call!.Symbol));

        // Named, the later one is there to read, alone.
        var later = await service.GetChainAsync("NIFTY", Next, null);
        Assert.Equal(Next, later.ExpiryDate);
        Assert.Equal(3 * 50_000, later.TotalCallOpenInterest);
    }

    [Fact]
    public async Task The_live_view_reads_the_nearest_and_says_when_its_capture_was()
    {
        await using var db = Db();
        Capture(db, Nearest, Open, oi: 1_000);
        Capture(db, Next, Open.AddSeconds(4), oi: 50_000);
        Capture(db, Monthly, Open.AddSeconds(8), oi: 90_000);
        await db.SaveChangesAsync();

        var view = await new OptionChainService(db).GetViewAsync("NIFTY", null, null);

        Assert.Equal(Nearest, view.ExpiryDate);
        Assert.Equal(Open, view.Header!.SnapshotCapturedUtc);
        Assert.Equal(6, view.Header.TotalLegs);
    }

    [Fact]
    public async Task A_replay_reads_the_nearest_as_it_stood_at_the_clock()
    {
        await using var db = Db();
        Capture(db, Nearest, Open, oi: 1_000);
        Capture(db, Next, Open.AddSeconds(4), oi: 50_000);
        Capture(db, Nearest, Open.AddMinutes(1), oi: 1_100);
        await db.SaveChangesAsync();
        var service = new OptionChainService(db);

        var between = await service.GetViewAsync("NIFTY", null, Open.AddSeconds(30));
        Assert.Equal(Nearest, between.ExpiryDate);
        Assert.Equal(Open, between.AsOfUtc);

        var later = await service.GetChainAsync("NIFTY", null, Open.AddMinutes(1).AddSeconds(30));
        Assert.Equal(Open.AddMinutes(1), later.AsOfUtc);
        Assert.Equal(3 * 1_100, later.TotalCallOpenInterest);
    }

    [Fact]
    public async Task The_session_trend_counts_the_nearest_alone()
    {
        await using var db = Db();
        for (int minute = 0; minute < 5; minute++) Capture(db, Nearest, Open.AddMinutes(minute), oi: 1_000);
        Capture(db, Next, Open.AddSeconds(4), oi: 50_000);
        Capture(db, Monthly, Open.AddSeconds(8), oi: 90_000);
        await db.SaveChangesAsync();

        var trend = await new OptionChainService(db).GetTrendAsync("NIFTY", null, null);

        Assert.Equal(Nearest, trend.ExpiryDate);
        Assert.Equal(5, trend.Captures);
        Assert.All(trend.Points, p => Assert.Equal(3 * 1_000, p.CallOpenInterest));
        Assert.All(trend.Points, p => Assert.Equal(1m, p.PutCallRatio));
    }

    [Fact]
    public async Task One_strikes_series_is_each_days_nearest_expiry()
    {
        await using var db = Db();
        // Thursday 29 Oct: the nearest is 3 Nov, 10 Nov is a later expiry.
        var thursday = Open.AddDays(-1);
        Capture(db, Nearest, thursday, oi: 1_000);
        Capture(db, Next, thursday.AddSeconds(4), oi: 50_000, strikes: [24000m, 26000m]);
        Capture(db, Nearest, thursday.AddMinutes(1), oi: 1_100);
        // Wednesday 4 Nov: 3 Nov has expired and 10 Nov is the nearest; 17 Nov is the later one.
        var wednesday = new DateTime(2026, 11, 4, 3, 45, 0, DateTimeKind.Utc);
        Capture(db, Next, wednesday, oi: 60_000);
        Capture(db, new DateOnly(2026, 11, 17), wednesday.AddSeconds(4), oi: 80_000);
        await db.SaveChangesAsync();
        var service = new OptionChainService(db);

        var series = await service.GetSeriesAsync("NIFTY", null, 24000m, null, null);

        Assert.Equal(new[] { thursday, thursday.AddMinutes(1), wednesday }, series.Points.Select(p => p.CapturedUtc));
        Assert.Equal(new long?[] { 1_000, 1_100, 60_000 }, series.Points.Select(p => p.CallOpenInterest));

        // Listed only in Thursday's later expiry: no point that day rather than another contract's.
        Assert.Empty((await service.GetSeriesAsync("NIFTY", null, 26000m, null, null)).Points);

        // Named, an expiry's own series, whatever its role each day.
        Assert.Equal(new[] { thursday.AddSeconds(4), wednesday },
            (await service.GetSeriesAsync("NIFTY", Next, 24000m, null, null)).Points.Select(p => p.CapturedUtc));
    }

    [Fact]
    public async Task On_an_expiry_day_the_nearest_is_the_one_expiring_that_day()
    {
        await using var db = Db();
        var expiryDay = new DateTime(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc); // 09:30 IST on Tuesday 3 Nov
        Capture(db, Nearest, expiryDay, oi: 1_000);
        Capture(db, Next, expiryDay.AddSeconds(4), oi: 50_000);
        Capture(db, Monthly, expiryDay.AddSeconds(8), oi: 90_000);
        await db.SaveChangesAsync();
        var service = new OptionChainService(db);

        Assert.Equal(Nearest, await service.DefaultExpiryAsync("NIFTY", null));
        Assert.Equal(Nearest, (await service.GetChainAsync("NIFTY", null, null)).ExpiryDate);
    }

    [Fact]
    public async Task The_recorded_expiries_of_a_day_say_how_often_each_was_captured()
    {
        await using var db = Db();
        for (int minute = 0; minute < 10; minute++) Capture(db, Nearest, Open.AddMinutes(minute), oi: 1_000);
        Capture(db, Next, Open.AddSeconds(4), oi: 50_000);
        Capture(db, Next, Open.AddMinutes(5).AddSeconds(4), oi: 50_000);
        Capture(db, Monthly, Open.AddSeconds(8), oi: 90_000);
        // The day before, and another underlying: neither is this day's NIFTY.
        Capture(db, Nearest, Open.AddDays(-1), oi: 1_000);
        Capture(db, Nearest, Open, oi: 1_000, underlying: "BANKNIFTY");
        await db.SaveChangesAsync();
        var service = new OptionChainService(db);

        var recorded = await service.GetRecordedExpiriesAsync("nifty", null);

        Assert.Equal("NIFTY", recorded.Underlying);
        Assert.Equal(new DateOnly(2026, 10, 30), recorded.SessionDate);
        Assert.Equal(Nearest, recorded.DefaultExpiry);
        Assert.Equal(new[] { Nearest, Next, Monthly }, recorded.Expiries.Select(e => e.ExpiryDate));
        Assert.Equal(new[] { 10, 2, 1 }, recorded.Expiries.Select(e => e.Captures));
        Assert.Equal(new int?[] { 60, 300, null }, recorded.Expiries.Select(e => e.TypicalIntervalSeconds));
        Assert.Equal(new[] { true, false, false }, recorded.Expiries.Select(e => e.IsDefault));
        Assert.Equal(Open.AddMinutes(9), recorded.Expiries[0].LastCapturedUtc);

        var quiet = await service.GetRecordedExpiriesAsync("NIFTY", new DateOnly(2026, 10, 31));
        Assert.Empty(quiet.Expiries);
        Assert.Null(quiet.DefaultExpiry);
    }

    [Fact]
    public async Task Nothing_recorded_is_said_as_such_not_read_as_some_other_expiry()
    {
        await using var db = Db();
        var service = new OptionChainService(db);

        Assert.Null(await service.DefaultExpiryAsync("NIFTY", null));
        var chain = await service.GetChainAsync("NIFTY", null, null);
        Assert.Empty(chain.Strikes);
        Assert.True(chain.OpenInterestUnavailable);
        Assert.Empty((await service.GetTrendAsync("NIFTY", null, null)).Points);
        Assert.Null((await service.GetRecordedExpiriesAsync("NIFTY", null)).SessionDate);
    }

    [Fact]
    public void The_usual_spacing_of_captures_shrugs_off_an_outage()
    {
        var start = Open;
        var stamps = new[] { start, start.AddMinutes(1), start.AddMinutes(2), start.AddMinutes(3), start.AddMinutes(63) };

        Assert.Equal(60, OptionChainService.TypicalIntervalSeconds(stamps));
        Assert.Null(OptionChainService.TypicalIntervalSeconds(new[] { start }));
        Assert.Equal(270, OptionChainService.TypicalIntervalSeconds(new[] { start, start.AddMinutes(4), start.AddMinutes(9) }));
    }

    // ------------------------------------------------------------- helpers

    /// <summary>One capture: a CE and a PE at each strike, the same open interest on every leg.</summary>
    private static void Capture(TradingDbContext db, DateOnly expiry, DateTime capturedUtc, long oi,
        decimal[]? strikes = null, string underlying = "NIFTY")
    {
        foreach (var strike in strikes ?? Strikes)
        {
            foreach (var type in new[] { "CE", "PE" })
            {
                db.OptionChainSnapshots.Add(new OptionChainSnapshot
                {
                    Underlying = underlying,
                    ExpiryDate = expiry,
                    StrikePrice = strike,
                    OptionType = type,
                    Symbol = $"NSE:{underlying}{expiry:yyMMdd}{strike:0}{type}",
                    CapturedUtc = capturedUtc,
                    SpotPrice = 24105m,
                    LastTradedPrice = 120m,
                    PriceChange = 4m,
                    Volume = 900,
                    OpenInterest = oi,
                    OpenInterestAtOpen = oi - 100,
                    ImpliedVolatility = 14.2m,
                    SourceKey = "dhan",
                });
            }
        }
    }

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"chain-expiry-{Guid.NewGuid():N}").Options);
}
