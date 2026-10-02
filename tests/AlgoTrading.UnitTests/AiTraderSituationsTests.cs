using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.OptionHistory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI Trader's base rates: a moment's features and outcomes (one definition for the table and for now), the
/// nearest past moments with the leakage rule, the brief's line, and the builder that fills the table.
/// </summary>
public class AiTraderSituationsTests
{
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string Vix = "NSE:INDIAVIX-INDEX";

    // Monday 21 to Thursday 24 Sep 2026; Thursday is the day under test, the three before it its history.
    private static readonly DateOnly Monday = new(2026, 9, 21);
    private static readonly DateOnly Thursday = new(2026, 9, 24);
    private static readonly DateOnly Friday = new(2026, 9, 25);

    private static DateTime At(DateOnly day, int hour, int minute) => IstTime.FromIst(day.ToDateTime(new TimeOnly(hour, minute)));

    /// <summary>Thursday's NIFTY: opens at 102 and rises 0.1 a minute (bar i, from 09:15, closes at 102 + 0.1·i); the three days before are flat at 100.</summary>
    private static decimal Rising(int i) => 102m + 0.1m * i;

    private static List<LiveBarResponse> Bars(DateOnly day, Func<int, decimal> close, string symbol = Nifty, Func<int, bool>? keep = null)
    {
        var open = At(day, 9, 15);
        return Enumerable.Range(0, 375).Where(i => keep?.Invoke(i) ?? true).Select(i => new LiveBarResponse
        {
            Symbol = symbol, Resolution = "1m", BarStartUtc = open.AddMinutes(i), Open = close(i), High = close(i) + 0.5m, Low = close(i) - 0.5m, Close = close(i),
        }).ToList();
    }

    private static List<LiveBarResponse> Week(Func<int, bool>? keepThursday = null) =>
    [
        .. Bars(Monday, _ => 100m), .. Bars(Monday.AddDays(1), _ => 100m), .. Bars(Monday.AddDays(2), _ => 100m),
        .. Bars(Thursday, Rising, keep: keepThursday),
    ];

    private static List<LiveBarResponse> VixWeek(bool today = true) =>
    [
        .. Bars(Monday.AddDays(2), _ => 15m, Vix), .. (today ? Bars(Thursday, _ => 16.5m, Vix) : []),
    ];

    // ---------- the features ----------

    [Fact]
    public void The_features_are_computed_from_the_bars_up_to_the_moment()
    {
        var (f, why) = SituationMath.Features(At(Thursday, 10, 25), Week(), VixWeek(), daysToExpiry: 2);

        Assert.Null(why);
        Assert.NotNull(f);
        Assert.Equal(108.9m, f.Price);                     // the 10:24 bar's close: it ended at 10:25
        Assert.Equal(8.9, f.MovePrevClosePct);             // against Wednesday's 100
        Assert.Equal(6.7647, f.MoveOpenPct);               // against the 102 open
        Assert.Equal(2.8329, f.Last30MinPct);              // against 09:54's 105.9
        Assert.Equal(7.9, f.RangePct);                     // 109.4 − 101.5 of the previous close
        Assert.Equal(SituationMath.Above, f.EmaSide);
        Assert.True(f.EmaGapPct > 0);
        Assert.Equal(16.5, f.Vix);
        Assert.Equal(10.0, f.VixChangePct);
        Assert.Equal(70, f.MinutesSinceOpen);
        Assert.Equal(2, f.DaysToExpiry);
        Assert.Equal(4, f.Weekday);                        // Thursday
        Assert.Equal(new TimeOnly(10, 25), f.Time);
    }

    [Fact]
    public void A_bar_that_began_at_the_moment_or_later_is_never_read()
    {
        var bars = Week();
        var crash = bars.Where(b => b.BarStartUtc >= At(Thursday, 10, 25)).ToList();
        foreach (var b in crash) (b.Open, b.High, b.Low, b.Close) = (1m, 1m, 1m, 1m);

        Assert.Equal(SituationMath.Features(At(Thursday, 10, 25), Week(), VixWeek(), 2).Facts,
            SituationMath.Features(At(Thursday, 10, 25), bars, VixWeek(), 2).Facts);
    }

    [Fact]
    public void The_first_slot_reads_the_last_half_hour_from_the_open()
    {
        var (f, _) = SituationMath.Features(At(Thursday, 9, 25), Week(), VixWeek(), 2);

        Assert.NotNull(f);
        Assert.Equal(102.9m, f.Price);
        Assert.Equal(10, f.MinutesSinceOpen);
        Assert.Equal(f.MoveOpenPct, f.Last30MinPct);       // no bar of the day half an hour back: since the open
        Assert.Equal(0.8824, f.Last30MinPct);
    }

    [Fact]
    public void A_gap_of_over_ten_minutes_before_the_moment_leaves_it_undescribed_and_a_shorter_one_does_not()
    {
        // 10:10 to 10:24 missing: the last minute known at 10:25 is 10:09.
        var (none, why) = SituationMath.Features(At(Thursday, 10, 25), Week(i => i is < 55 or > 69), VixWeek(), 2);
        Assert.Null(none);
        Assert.Contains("10:09", why);

        // 10:20 to 10:24 missing: 10:19's close is the price.
        var (f, _) = SituationMath.Features(At(Thursday, 10, 25), Week(i => i is < 65 or > 69), VixWeek(), 2);
        Assert.NotNull(f);
        Assert.Equal(108.4m, f.Price);
    }

    [Fact]
    public void A_day_with_no_vix_keeps_the_moment_without_the_vix_features()
    {
        var (f, _) = SituationMath.Features(At(Thursday, 10, 25), Week(), [], 2);

        Assert.NotNull(f);
        Assert.Null(f.Vix);
        Assert.Null(f.VixChangePct);
        var v = SituationMath.Vector(f);
        Assert.True(double.IsNaN(v[5]) && double.IsNaN(v[6]));

        // VIX today but not before: its level without its change.
        var (g, _) = SituationMath.Features(At(Thursday, 10, 25), Week(), Bars(Thursday, _ => 16.5m, Vix), 2);
        Assert.Equal(16.5, g!.Vix);
        Assert.Null(g.VixChangePct);
    }

    [Fact]
    public void Without_the_previous_session_or_a_bar_of_today_there_is_nothing_to_describe()
    {
        Assert.Contains("previous session's close", SituationMath.Features(At(Thursday, 10, 25), Bars(Thursday, Rising), VixWeek(), 2).Why);

        // Wednesday's bars stop at 12:59: its last price is not its close.
        List<LiveBarResponse> stoppedEarly = [.. Bars(Monday.AddDays(2), _ => 100m, keep: i => i < 225), .. Bars(Thursday, Rising)];
        Assert.Contains("previous session's close", SituationMath.Features(At(Thursday, 10, 25), stoppedEarly, VixWeek(), 2).Why);
        Assert.Contains("no minute of today's session", SituationMath.Features(At(Friday, 10, 25), Week(), VixWeek(), 2).Why);
    }

    [Fact]
    public void Bars_outside_the_session_and_broken_prints_are_left_out()
    {
        var bars = Week();
        // A feed that kept quoting after Wednesday's close at another price, and a pre-open print on Thursday.
        bars.Add(new LiveBarResponse { Symbol = Nifty, BarStartUtc = At(Monday.AddDays(2), 16, 0), Open = 90, High = 90, Low = 90, Close = 90 });
        bars.Add(new LiveBarResponse { Symbol = Nifty, BarStartUtc = At(Thursday, 9, 5), Open = 80, High = 80, Low = 80, Close = 80 });
        // And a broken print of zero as Wednesday's last minute.
        bars.Add(new LiveBarResponse { Symbol = Nifty, BarStartUtc = At(Monday.AddDays(2), 15, 29), Open = 0, High = 0, Low = 0, Close = 0 });

        var (f, _) = SituationMath.Features(At(Thursday, 10, 25), bars, VixWeek(), 2);

        Assert.Equal(8.9, f!.MovePrevClosePct);
        Assert.Equal(7.9, f.RangePct);
    }

    // ---------- the outcomes ----------

    [Fact]
    public void The_outcomes_are_the_returns_to_30_and_60_minutes_and_1525_and_the_extremes_on_the_way()
    {
        var o = SituationMath.Outcomes(At(Thursday, 10, 25), Week(), 108.9m);

        Assert.Equal(2.7548, o.Return30MinPct);      // 10:54's 111.9
        Assert.Equal(5.5096, o.Return60MinPct);      // 11:24's 114.9
        Assert.Equal(27.5482, o.ReturnToClosePct);   // 15:24's 138.9
        Assert.Equal(28.0073, o.MaxUpPct);           // 15:24's high, 139.4
        Assert.Equal(-0.3673, o.MaxDownPct);         // 10:25's low, 108.5
    }

    [Fact]
    public void Late_in_the_day_the_horizons_end_at_1525()
    {
        var price = Rising(349);   // 15:04's close, the price at 15:05
        var o = SituationMath.Outcomes(At(Thursday, 15, 5), Week(), price);

        Assert.Equal(o.ReturnToClosePct, o.Return30MinPct);
        Assert.Equal(o.ReturnToClosePct, o.Return60MinPct);
    }

    [Fact]
    public void A_gap_before_1525_leaves_the_close_and_the_extremes_unknown()
    {
        // Nothing after 15:09.
        var o = SituationMath.Outcomes(At(Thursday, 10, 25), Week(i => i < 355), 108.9m);

        Assert.Equal(2.7548, o.Return30MinPct);
        Assert.Null(o.ReturnToClosePct);
        Assert.Null(o.MaxUpPct);
        Assert.Null(o.MaxDownPct);
    }

    [Fact]
    public void Days_to_expiry_count_trading_days_and_give_up_on_a_gap_in_the_calendar()
    {
        var expiry = new DateOnly(2026, 10, 6);   // Tuesday
        bool Trading(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && d != new DateOnly(2026, 10, 2);

        Assert.Equal(2, SituationMath.TradingDaysToExpiry(new DateOnly(2026, 10, 1), [expiry], Trading));   // Gandhi Jayanti and a weekend between
        Assert.Equal(0, SituationMath.TradingDaysToExpiry(expiry, [expiry], Trading));
        Assert.Null(SituationMath.TradingDaysToExpiry(new DateOnly(2026, 8, 1), [expiry], Trading));        // 66 days: a gap, not a wait
        Assert.Null(SituationMath.TradingDaysToExpiry(new DateOnly(2026, 10, 7), [expiry], Trading));
    }

    // ---------- the nearest past moments ----------

    private static SituationFacts Now(DateOnly day, int minutes = 70, double? vix = 15, string side = SituationMath.Above) =>
        new(day, new TimeOnly(9, 15).AddMinutes(minutes), 100m, 0.5, 0.3, 0.1, 0.8, side, 0.05, vix, 1.0, minutes, 3, 2);

    private static SituationPoint Point(DateOnly day, SituationFacts like, double movePrevClose = 0.5, int? minutes = null, string? side = null,
        double r60 = 0.1, double close = 0.2, double up = 0.5, double down = -0.4, double? vix = 15)
    {
        int m = minutes ?? like.MinutesSinceOpen;
        var f = like with { Day = day, MovePrevClosePct = movePrevClose, MinutesSinceOpen = m, EmaSide = side ?? like.EmaSide, Vix = vix };
        return new SituationPoint(day, new TimeOnly(9, 15).AddMinutes(m), f.EmaSide, SituationMath.Vector(f), 0.05, r60, close, up, down);
    }

    [Fact]
    public void The_same_day_and_later_days_are_never_read()
    {
        var day = new DateOnly(2026, 9, 16);
        var now = Now(day);
        var pool = new[] { day.AddDays(-2), day.AddDays(-1), day, day.AddDays(1) }.Select(d => Point(d, now)).ToList();

        var result = SimilarMoments.Nearest(pool, now, k: 4);

        Assert.Equal(2, result.PastRows);
        Assert.Equal(new[] { day.AddDays(-1), day.AddDays(-2) }, result.Nearest.Select(t => t.Point.Day));
        Assert.Equal("NIFTY: too little history for a base rate: 2 similar past days before today, 50 needed.",
            SimilarMoments.Describe("NIFTY", now, result));
    }

    [Fact]
    public void The_nearest_days_are_chosen_one_moment_each_on_the_same_side_of_the_emas_at_a_similar_time()
    {
        var now = Now(new DateOnly(2026, 9, 16));
        var first = new DateOnly(2025, 6, 2);
        var pool = new List<SituationPoint>();
        for (int d = 0; d < 60; d++)
        {
            var day = first.AddDays(d);
            pool.Add(Point(day, now, movePrevClose: 0.5 + d * 0.1));                     // nearer as d is smaller
            pool.Add(Point(day, now, movePrevClose: 5 + d * 0.1, minutes: 80));          // the same day, farther
            pool.Add(Point(day, now, side: SituationMath.Below));                         // closest of all, but below the EMAs
            pool.Add(Point(day, now, minutes: 190));                                       // the same features at 12:25
            pool.Add(Point(day, now, vix: null));                                          // VIX unknown, as now's is not
        }

        var result = SimilarMoments.Nearest(pool, now);

        Assert.Equal(50, result.Nearest.Count);
        Assert.Equal(Enumerable.Range(0, 50).Select(d => first.AddDays(d)), result.Nearest.Select(t => t.Point.Day));
        Assert.All(result.Nearest, t => Assert.Equal(new TimeOnly(10, 25), t.Point.Slot));
        Assert.All(result.Nearest, t => Assert.Equal(SituationMath.Above, t.Point.EmaSide));
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void A_feature_unknown_now_is_left_out_of_the_match_and_the_line_says_so()
    {
        var now = Now(new DateOnly(2026, 9, 16), vix: null) with { VixChangePct = null };
        var pool = Enumerable.Range(0, 60).Select(d => Point(new DateOnly(2025, 6, 2).AddDays(d), now, vix: d % 2 == 0 ? 15 : null)).ToList();

        var result = SimilarMoments.Nearest(pool, now);

        Assert.Equal(50, result.Nearest.Count);              // rows with and without VIX alike
        Assert.Equal(new[] { "India VIX", "VIX change" }, result.Unmatched);
        Assert.EndsWith("(matched without India VIX and VIX change: not known now)", SimilarMoments.Describe("NIFTY", now, result));
    }

    [Fact]
    public void The_line_prints_the_share_up_the_median_and_the_middle_half()
    {
        var now = Now(new DateOnly(2026, 9, 16));
        // Returns −1.00%, −0.96%, … +0.96%: 24 of 50 above zero, median −0.02%, quartiles −0.51% and +0.47%.
        var points = Enumerable.Range(0, 50)
            .Select(i => Point(new DateOnly(2021, 8, 2).AddDays(i), now, r60: (4 * i - 100) / 100.0, close: (4 * i - 100) / 100.0))
            .Select(p => (p, 0.0)).ToList();

        var line = SimilarMoments.Describe("NIFTY", now, new SimilarResult(50, points, []));

        Assert.Equal("NIFTY (50 days since Aug 2021, price above both EMAs as now): next hour up 48% of the time, median −0.02% (middle half −0.51% to +0.47%); "
                     + "to 15:25 up 48%, median −0.02% (middle half −0.51% to +0.47%); biggest move to the close: up median +0.50%, down median −0.40%", line);
        Assert.StartsWith("NIFTY (50 days since Aug 2021, price above both EMAs as now): next hour (cut at 15:25) up",
            SimilarMoments.Describe("NIFTY", now with { MinutesSinceOpen = 320 }, new SimilarResult(50, points, [])));
        Assert.Equal("NIFTY: no past moments stored yet, so no base rate.", SimilarMoments.Describe("NIFTY", now, new SimilarResult(0, [], [])));
    }

    // ---------- the builder ----------

    private static void SeedCandles(TradingDbContext db)
    {
        foreach (var b in Week().Concat(VixWeek()))
        {
            db.Candles.Add(new Candle
            {
                Symbol = b.Symbol, Resolution = "1", TimeStampUtc = b.BarStartUtc, Open = b.Open, High = b.High, Low = b.Low, Close = b.Close, SourceKey = "dhan",
            });
        }

        db.SystemSettings.Add(new SystemSetting { Key = NightlyArchiveService.LastArchivedDayKey, Value = "2026-09-24" });
        db.SaveChanges();
    }

    private static readonly IndexOptionExpiryCalendar Expiries =
        IndexOptionExpiryCalendar.From(new Dictionary<string, IEnumerable<DateOnly>> { ["NIFTY"] = [new DateOnly(2026, 9, 29)] });

    private static MarketSessionService Sessions(IMarketCalendar? calendar = null) => new(calendar ?? new MarketReplayTests.OpenCalendar());

    private static AiTraderSituationBuilder Builder(TradingDbContext db, TimeProvider clock, AiTraderSituationsStatus? status = null)
    {
        var sessions = Sessions();
        return new AiTraderSituationBuilder(db, new SituationCalendar(db, Expiries, sessions), sessions, status ?? new AiTraderSituationsStatus(),
            NullLogger<AiTraderSituationBuilder>.Instance, clock) { PauseBetweenDays = TimeSpan.Zero };
    }

    [Fact]
    public async Task A_day_is_built_with_the_same_features_the_brief_would_compute_and_rebuilt_in_place()
    {
        var db = NewDb();
        SeedCandles(db);
        var builder = Builder(db, new Clock(At(Friday, 20, 0)));

        Assert.Equal(35, await builder.BuildDayAsync(Thursday, default));
        Assert.Equal(35, await builder.BuildDayAsync(Thursday, default));

        var rows = await db.AiTraderSituations.AsNoTracking().Where(s => s.Day == Thursday).ToListAsync();
        Assert.Equal(35, rows.Count);                                  // replaced, not doubled
        Assert.Equal(SituationMath.Slots, rows.Select(r => r.Slot).Order());
        var row = rows.Single(r => r.Slot == new TimeOnly(10, 25));
        var (expected, _) = SituationMath.Features(At(Thursday, 10, 25), Week(), VixWeek(), 3);
        Assert.Equal(expected, SituationMath.Facts(row));               // Thursday to Tuesday 29 Sep: Friday, Monday, Tuesday
        Assert.Equal(27.5482, row.ReturnToClosePct);
        Assert.Equal("NIFTY", row.Underlying);
    }

    [Fact]
    public async Task The_backfill_carries_on_from_the_last_day_done_after_a_stop()
    {
        var name = Guid.NewGuid().ToString("N");
        SeedCandles(NewDb(name));
        var clock = new Clock(At(Friday, 20, 0));
        var status = new AiTraderSituationsStatus();

        Assert.Equal(Monday, await Builder(NewDb(name), clock, status).StartAsync(null, default));
        int allowed = 2;
        var first = await Builder(NewDb(name), clock, status).RunAsync(() => allowed-- > 0, default);

        // Stopped before Wednesday: Monday has no previous close (no rows), Tuesday is built.
        Assert.Equal(SituationRunState.Paused, first.State);
        Assert.Equal(Monday.AddDays(1), first.LastDay);
        Assert.Equal(Monday.AddDays(1), await Builder(NewDb(name), clock, status).LastDayAsync(default));
        Assert.Empty(NewDb(name).AiTraderSituations.Where(s => s.Day > Monday.AddDays(1)));
        var tuesdayBuilt = NewDb(name).AiTraderSituations.First(s => s.Day == Monday.AddDays(1)).BuiltUtc;

        clock.Now = At(Friday, 21, 0);
        var second = await Builder(NewDb(name), clock, status).RunAsync(() => true, default);

        Assert.Equal(SituationRunState.UpToDate, second.State);
        Assert.Equal(2, second.Days);                                   // Wednesday and Thursday only
        Assert.Equal(Thursday, second.LastDay);
        Assert.Equal(tuesdayBuilt, NewDb(name).AiTraderSituations.First(s => s.Day == Monday.AddDays(1)).BuiltUtc);
        Assert.Equal(35, NewDb(name).AiTraderSituations.Count(s => s.Day == Thursday));

        var third = await Builder(NewDb(name), clock, status).RunAsync(() => true, default);
        Assert.Equal((SituationRunState.UpToDate, 0), (third.State, third.Days));
    }

    [Fact]
    public async Task It_builds_only_to_the_archives_last_day_and_never_before_it_is_started()
    {
        var db = NewDb();
        SeedCandles(db);
        db.SystemSettings.Single(s => s.Key == NightlyArchiveService.LastArchivedDayKey).Value = "2026-09-23";
        await db.SaveChangesAsync();
        var builder = Builder(db, new Clock(At(Friday, 20, 0)));

        Assert.Equal(SituationRunState.NotStarted, (await builder.RunAsync(() => true, default)).State);
        await builder.StartAsync(null, default);
        var run = await builder.RunAsync(() => true, default);

        Assert.Equal(new DateOnly(2026, 9, 23), run.LastDay);
        Assert.Empty(db.AiTraderSituations.Where(s => s.Day == Thursday));
    }

    [Theory]
    [InlineData(8, 44, false)]
    [InlineData(8, 45, true)]
    [InlineData(12, 0, true)]
    [InlineData(15, 44, true)]
    [InlineData(15, 45, false)]
    [InlineData(23, 50, false)]
    public void It_does_not_run_from_0845_to_1545_on_a_trading_day(int hour, int minute, bool inSession)
    {
        Assert.Equal(inSession, AiTraderSituationBuilder.InSession(At(Friday, hour, minute), Sessions()));
    }

    [Fact]
    public void A_weekend_or_an_exchange_holiday_is_no_session()
    {
        Assert.False(AiTraderSituationBuilder.InSession(At(new DateOnly(2026, 9, 26), 11, 0), Sessions()));
        Assert.False(AiTraderSituationBuilder.InSession(At(new DateOnly(2026, 10, 2), 11, 0), Sessions(new Holiday(new DateOnly(2026, 10, 2)))));
    }

    [Fact]
    public async Task The_service_takes_a_start_at_once_but_builds_only_after_the_session()
    {
        var name = Guid.NewGuid().ToString("N");
        SeedCandles(NewDb(name));
        var clock = new Clock(At(Friday, 10, 0));
        var status = new AiTraderSituationsStatus();
        var library = new AiTraderSituationLibrary(Scopes(name, clock, status));
        var service = new AiTraderSituationsService(Scopes(name, clock, status), status, library, Sessions(), NullLogger<AiTraderSituationsService>.Instance, clock);

        status.Request(null);
        var during = await service.RunOnceAsync(default);

        Assert.Equal(SituationRunState.Waiting, during.State);
        Assert.Equal(Monday.AddDays(-1), during.LastDay);              // started: the last day done is set
        Assert.Empty(NewDb(name).AiTraderSituations);
        Assert.Equal("waiting", status.Snapshot().State);

        clock.Now = At(Friday, 16, 0);
        var after = await service.RunOnceAsync(default);

        Assert.Equal((SituationRunState.UpToDate, Thursday), (after.State, after.LastDay));
        Assert.Equal(35 * 3, NewDb(name).AiTraderSituations.Count());   // Tuesday, Wednesday, Thursday
        Assert.Equal(105, (await library.ForAsync("NIFTY", default)).Count);
    }

    [Fact]
    public async Task The_admin_endpoints_start_the_backfill_and_show_what_is_built()
    {
        var ai = Build(Settings());
        SeedSituations(ai.Db, new DateOnly(2026, 6, 1), 3);
        ai.Db.SystemSettings.Add(new SystemSetting { Key = AiTraderSituationBuilder.LastDayKey, Value = "2026-06-03" });
        await ai.Db.SaveChangesAsync();
        var controller = new AlgoTrading.Api.Controllers.AiTraderController(ai.Db, ai.Store, ai.Options);
        var status = new AiTraderSituationsStatus();

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(controller.StartSituations("3 June", status));
        Assert.False(status.HasRequest);
        Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(controller.StartSituations("2026-06-02", status));
        Assert.Equal((true, new DateOnly(2026, 6, 2)), status.TakeRequest());

        var view = Assert.IsType<AlgoTrading.Api.Controllers.AiTraderSituationsView>(
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Situations(status, default)).Value);
        Assert.Equal("2026-06-03", view.LastDay);
        var nifty = Assert.Single(view.Indices);
        Assert.Equal(("NIFTY", 3, 3, 3, new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 3)),
            (nifty.Underlying, nifty.Rows, nifty.WithOutcomes, nifty.Days, nifty.First, nifty.Last));
    }

    // ---------- the brief ----------

    private static void SeedLiveBars(TradingDbContext db)
    {
        foreach (var b in Week().Concat(VixWeek()))
        {
            db.LiveBars.Add(new LiveBar { Symbol = b.Symbol, Resolution = "1m", BarStartUtc = b.BarStartUtc, Open = b.Open, High = b.High, Low = b.Low,
                Close = b.Close, TickCount = 5, UpdatedUtc = b.BarStartUtc, SourceKey = "dhan" });
        }

        db.SaveChanges();
    }

    /// <summary>Sixty past days, each a moment at 10:25 above both EMAs like Thursday's, rising +0.10% the next hour on even days and falling on odd ones.</summary>
    private static void SeedSituations(TradingDbContext db, DateOnly from, int days, double r60 = double.NaN)
    {
        var (like, _) = SituationMath.Features(At(Thursday, 10, 25), Week(), VixWeek(), 3);
        for (int d = 0; d < days; d++)
        {
            var day = from.AddDays(d);
            double r = double.IsNaN(r60) ? (d % 2 == 0 ? 0.1 : -0.1) : r60;
            db.AiTraderSituations.Add(SituationMath.Row("NIFTY", like! with { Day = day, MovePrevClosePct = like.MovePrevClosePct + d * 0.01 },
                new SituationOutcomes(0.05, r, r, 0.3, -0.2), DateTime.UtcNow));
        }

        db.SaveChanges();
    }

    private static SimilarMomentsSection Section(TradingDbContext db, AiTraderSituationLibrary library)
    {
        var sessions = Sessions();
        return new SimilarMomentsSection(new LiveDataService(db, new NoProviders(), sessions), new SituationCalendar(db, Expiries, sessions), library,
            NullLogger<SimilarMomentsSection>.Instance);
    }

    [Fact]
    public async Task The_brief_prints_each_indexs_base_rate_from_days_before_its_own_only()
    {
        var name = Guid.NewGuid().ToString("N");
        var db = NewDb(name);
        SeedLiveBars(db);
        SeedSituations(db, new DateOnly(2026, 6, 1), 60);
        var clock = new Clock(At(Thursday, 10, 25));
        var section = Section(db, new AiTraderSituationLibrary(Scopes(name, clock, new AiTraderSituationsStatus())));

        var text = await section.BuildAsync(At(Thursday, 10, 25), default);
        var lines = text.Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.Equal("NIFTY (50 days since Jun 2026, price above both EMAs as now): next hour up 50% of the time, median 0.00% (middle half −0.10% to +0.10%); "
                     + "to 15:25 up 50%, median 0.00% (middle half −0.10% to +0.10%); biggest move to the close: up median +0.30%, down median −0.20%", lines[0]);
        Assert.Equal("BANKNIFTY: cannot be compared now: no minute of today's session recorded yet.", lines[1]);

        // Thursday and later, all rising: read, they would turn the base rate.
        SeedSituations(db, Thursday, 5, r60: 2.0);
        var later = await Section(db, new AiTraderSituationLibrary(Scopes(name, clock, new AiTraderSituationsStatus())))
            .BuildAsync(At(Thursday, 10, 25), default);
        Assert.Equal(text, later);
    }

    [Fact]
    public async Task The_section_fails_soft_and_the_brief_stands()
    {
        var db = NewDb();
        SeedLiveBars(db);
        var section = Section(db, new AiTraderSituationLibrary(new BrokenScopes()));

        var text = await section.BuildAsync(At(Thursday, 10, 25), default);
        Assert.StartsWith("NIFTY: not available just now.", text);

        var sessions = Sessions();
        var brief = await new MarketBriefBuilder(db, new LiveDataService(db, new NoProviders(), sessions), new OptionChainService(db, sessions, new MarketBriefBuilderTests.Lots(65)),
            null!, null!, sessions, NullLogger<MarketBriefBuilder>.Instance, null, section).BuildAsync(At(Thursday, 10, 25), replay: false, default);
        Assert.Contains(SimilarMomentsSection.Header + "\nNIFTY: not available just now.", brief.Text);
        Assert.Contains("OPTION CHAINS", brief.Text);
    }

    [Fact]
    public async Task The_library_reads_only_rows_whose_outcomes_are_all_known()
    {
        var db = NewDb();
        SeedSituations(db, new DateOnly(2026, 6, 1), 3);
        db.AiTraderSituations.First().Return60MinPct = null;
        await db.SaveChangesAsync();

        Assert.Equal(2, (await AiTraderSituationLibrary.LoadAsync(db, "NIFTY", default)).Count);
    }

    // ---------- fakes ----------

    private static IServiceScopeFactory Scopes(string name, TimeProvider clock, AiTraderSituationsStatus status)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb(name));
        services.AddScoped(sp => new AiTraderSituationBuilder(sp.GetRequiredService<TradingDbContext>(),
            new SituationCalendar(sp.GetRequiredService<TradingDbContext>(), Expiries, Sessions()), Sessions(), status,
            NullLogger<AiTraderSituationBuilder>.Instance, clock) { PauseBetweenDays = TimeSpan.Zero });
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private sealed class Clock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    private sealed class Holiday(DateOnly day) : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) =>
            date == day ? new MarketHoliday { Exchange = exchange, Date = date, Name = "Gandhi Jayanti", Closure = MarketClosure.FullDay } : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public bool IsLoaded => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class BrokenScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("The database is down.");
    }

    private sealed class NoProviders : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => [];

        public ProviderDescriptor? Find(string providerKey) => null;
    }
}
