using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.Api.Services.MarketCloseRules;

namespace AlgoTrading.UnitTests;

/// <summary>
/// NSE and BSE close at 15:30 until 6 Dec 2026. SEBI's circular of 15 Oct 2026 keeps the cash market (and so the
/// indices, computed from it) to 15:30 and moves the derivatives close to 15:45 from 7 Dec 2026, except that
/// expiring index contracts still stop at 15:30 on their expiry day. One rule (<see cref="EquityMarketHours"/>), read
/// on both sides of the date: Friday 4 Dec and today, Friday 30 Oct, exactly as before; Monday 7 Dec and Tuesday
/// 8 Dec (NIFTY's expiry) by the new one.
/// </summary>
public class DerivativesCloseTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);
    private static readonly DateOnly FridayBefore = new(2026, 12, 4);
    private static readonly DateOnly MondayFrom = new(2026, 12, 7);
    private static readonly DateOnly NiftyExpiry = new(2026, 12, 8);

    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string NiftyWeeklyCall = "NSE:NIFTY26D0824500CE";    // expires Tue 8 Dec 2026
    private const string NiftyFuture = "NSE:NIFTY26DECFUT";
    private const string Share = "NSE:RELIANCE-EQ";
    private const string StockCall = "NSE:RELIANCE26DEC1500CE";

    private static DateTime Ist(DateOnly day, int h, int mi, int s = 0) => IstTime.FromIst(day.ToDateTime(new TimeOnly(h, mi, s)));

    private static MarketSessionService Sessions() => new(new OpenCalendar());

    // ---------- the rule ----------

    [Fact]
    public void The_derivatives_close_is_15_30_until_6_December_and_15_45_from_7_December()
    {
        Assert.Equal(new TimeOnly(15, 30), EquityMarketHours.DerivativesCloseOn(Today));
        Assert.Equal(new TimeOnly(15, 30), EquityMarketHours.DerivativesCloseOn(new DateOnly(2026, 12, 6)));
        Assert.Equal(new TimeOnly(15, 45), EquityMarketHours.DerivativesCloseOn(MondayFrom));
        Assert.Equal(new TimeOnly(15, 30), EquityMarketHours.CashClose);
        Assert.Equal(new TimeSpan(15, 30, 0), IstTime.SessionClose);
    }

    [Fact]
    public void A_symbol_is_read_as_cash_derivative_or_commodity()
    {
        Assert.Equal("CM", EquityMarketHours.SegmentOf(Nifty));
        Assert.Equal("CM", EquityMarketHours.SegmentOf(Share));
        Assert.Equal("FO", EquityMarketHours.SegmentOf(NiftyWeeklyCall));
        Assert.Equal("FO", EquityMarketHours.SegmentOf(NiftyFuture));
        Assert.Equal("FO", EquityMarketHours.SegmentOf(StockCall));
        Assert.Equal("COM", EquityMarketHours.SegmentOf("MCX:CRUDEOIL26DECFUT"));
    }

    [Fact]
    public void An_index_contract_stops_at_15_30_on_its_expiry_day_and_a_stock_contract_at_15_45()
    {
        Assert.Equal(new TimeOnly(15, 30), EquityMarketHours.CloseOf(NiftyWeeklyCall, NiftyExpiry));
        Assert.Equal(new TimeOnly(15, 45), EquityMarketHours.CloseOf(NiftyWeeklyCall, MondayFrom));
        Assert.Equal(new TimeOnly(15, 45), EquityMarketHours.CloseOf(StockCall, new DateOnly(2026, 12, 29), new DateOnly(2026, 12, 29)));
        Assert.Equal(new TimeOnly(15, 30), EquityMarketHours.CloseOf(Share, MondayFrom));
        Assert.Equal(new TimeOnly(15, 30), EquityMarketHours.CloseOf(NiftyWeeklyCall, FridayBefore));
    }

    // ---------- the session service ----------

    [Fact]
    public void The_FO_segment_trades_to_15_45_from_7_December_while_CM_closes_at_15_30()
    {
        var sessions = Sessions();

        Assert.Equal(Ist(MondayFrom, 15, 45), sessions.GetSessionInfo(Ist(MondayFrom, 12, 0), "NSE", "FO").SessionCloseUtc);
        Assert.Equal(Ist(MondayFrom, 15, 30), sessions.GetSessionInfo(Ist(MondayFrom, 12, 0), "NSE", "CM").SessionCloseUtc);
        Assert.True(sessions.IsMarketOpen(Ist(MondayFrom, 15, 40), "BSE", "FO"));
        Assert.False(sessions.IsMarketOpen(Ist(MondayFrom, 15, 40), "BSE", "CM"));

        foreach (var day in new[] { Today, FridayBefore })
        {
            Assert.Equal(Ist(day, 15, 30), sessions.GetSessionInfo(Ist(day, 12, 0), "NSE", "FO").SessionCloseUtc);
            Assert.Equal(Ist(day, 15, 30), sessions.GetSessionInfo(Ist(day, 12, 0), "NSE", "CM").SessionCloseUtc);
            Assert.False(sessions.IsMarketOpen(Ist(day, 15, 30), "NSE", "FO"));
        }
    }

    // ---------- the run sweep ----------

    private static readonly DeskRun MorningNifty = new(301, "NIFTY", Nifty, Ist(Today, 9, 16));
    private static readonly DeskRun MorningSensex = new(302, "SENSEX", "BSE:SENSEX-INDEX", Ist(Today, 9, 16));
    private static readonly DeskRun MorningReliance = new(303, "RELIANCE", Share, Ist(Today, 9, 16));

    private static DeskRun On(DeskRun run, DateOnly day) => run with { StartedUtc = Ist(day, 9, 16) };

    [Fact]
    public void Today_and_on_4_December_every_NSE_and_BSE_run_stops_at_15_30_with_the_same_reason()
    {
        foreach (var day in new[] { Today, FridayBefore })
        {
            var runs = new[] { On(MorningNifty, day), On(MorningSensex, day), On(MorningReliance, day) };
            Assert.Empty(RunsToStop(Sessions(), Ist(day, 15, 29, 59), runs));

            var due = RunsToStop(Sessions(), Ist(day, 15, 30), runs);
            Assert.Equal(3, due.Count);
            Assert.All(due, x => Assert.Equal("Market closed (15:30 IST)", x.Reason));
            Assert.All(due, x => Assert.Equal(Ist(day, 15, 30), x.ClosedAtUtc));
            Assert.Equal(MarketHoursService.MarketClosedReason, due[0].Reason);
        }
    }

    [Fact]
    public void From_7_December_the_runs_trade_to_15_45()
    {
        var runs = new[] { On(MorningNifty, MondayFrom), On(MorningSensex, MondayFrom), On(MorningReliance, MondayFrom) };

        Assert.Empty(RunsToStop(Sessions(), Ist(MondayFrom, 15, 30), runs));
        Assert.Empty(RunsToStop(Sessions(), Ist(MondayFrom, 15, 44, 59), runs));

        var due = RunsToStop(Sessions(), Ist(MondayFrom, 15, 45), runs);
        Assert.Equal(3, due.Count);
        Assert.All(due, x => Assert.Equal("Market closed (15:45 IST)", x.Reason));
    }

    [Fact]
    public void On_an_index_s_expiry_day_its_runs_stop_with_the_expiring_contracts_at_15_30()
    {
        var runs = new[] { On(MorningNifty, NiftyExpiry), On(MorningSensex, NiftyExpiry), On(MorningReliance, NiftyExpiry) };
        var expiring = new HashSet<string>(["nifty"], StringComparer.OrdinalIgnoreCase);

        var due = Assert.Single(RunsToStop(Sessions(), Ist(NiftyExpiry, 15, 30), runs, expiring));
        Assert.Equal(301, due.RunId);
        Assert.Equal("Market closed (15:30 IST)", due.Reason);

        // SENSEX (BSE's expiry is on Thursdays) and the stock run trade on to the derivatives close.
        Assert.Equal(new long[] { 302, 303 },
            RunsToStop(Sessions(), Ist(NiftyExpiry, 15, 45), runs.Skip(1), expiring).Select(x => x.RunId).Order().ToArray());
    }

    [Fact]
    public void A_run_started_before_an_expiry_day_is_still_stopped_at_the_derivatives_close_it_missed()
    {
        // Started Monday 15:40, after the cash close and before the derivatives': Monday's 15:45 stops it.
        var late = new DeskRun(401, "NIFTY", Nifty, Ist(MondayFrom, 15, 40));
        var expiring = new HashSet<string>(["NIFTY"], StringComparer.OrdinalIgnoreCase);

        var due = Assert.Single(RunsToStop(Sessions(), Ist(NiftyExpiry, 9, 20), [late], expiring));
        Assert.Equal(Ist(MondayFrom, 15, 45), due.ClosedAtUtc);
    }

    [Fact]
    public void The_feeds_poller_and_alerter_stop_with_the_same_words_as_the_runs()
    {
        Assert.Equal(MarketHoursService.MarketClosedReason, MarketHoursService.MarketClosedReasonOn(Today));
        Assert.Equal("Market closed (15:30 IST)", MarketHoursService.MarketClosedReasonOn(FridayBefore));
        Assert.Equal("Market closed (15:45 IST)", MarketHoursService.MarketClosedReasonOn(MondayFrom));
    }

    [Fact]
    public async Task Index_expiries_are_looked_up_only_once_one_can_bring_a_close_forward()
    {
        // Before 7 Dec, and before 15:30 on a day from it, no database is touched at all.
        Assert.False(ExpiryCanCloseFirst(Ist(Today, 15, 31)));
        Assert.False(ExpiryCanCloseFirst(Ist(NiftyExpiry, 15, 29)));
        Assert.True(ExpiryCanCloseFirst(Ist(NiftyExpiry, 15, 30)));
        Assert.Empty(await IndicesExpiringTodayAsync(null!, Ist(Today, 15, 31), ["NIFTY"], CancellationToken.None));

        using var db = Db();
        db.Instruments.AddRange(
            Option(NiftyWeeklyCall, "NIFTY", NiftyExpiry),
            Option("BSE:SENSEX26D1080000CE", "SENSEX", new DateOnly(2026, 12, 10)),
            Option(StockCall, "RELIANCE", NiftyExpiry));
        await db.SaveChangesAsync();

        var expiring = await IndicesExpiringTodayAsync(db, Ist(NiftyExpiry, 15, 31), ["NIFTY", "SENSEX", "RELIANCE", null], CancellationToken.None);
        Assert.Equal(new[] { "NIFTY" }, expiring.ToArray());
        Assert.Contains("nifty", expiring);
    }

    // ---------- the manual book ----------

    [Fact]
    public void From_7_December_a_manual_option_is_squared_off_at_15_45_and_a_share_at_15_30()
    {
        var opened = Ist(MondayFrom, 10, 0);
        var candidates = new[]
        {
            new ManualIntradaySquareOff.Candidate(1, 9, Share, opened, null),
            new ManualIntradaySquareOff.Candidate(2, 9, "NSE:NIFTY26D1524500CE", opened, null),
        };

        var at1531 = ManualIntradaySquareOff.DuePositions(Sessions(), Ist(MondayFrom, 15, 31), candidates);
        Assert.Equal(1, Assert.Single(at1531).PositionId);
        Assert.Equal("Intraday — squared off at the close (15:30 IST)", at1531[0].Reason);

        var at1545 = ManualIntradaySquareOff.DuePositions(Sessions(), Ist(MondayFrom, 15, 45), candidates);
        Assert.Equal("Intraday — squared off at the close (15:45 IST)", at1545.Single(x => x.PositionId == 2).Reason);
    }

    [Fact]
    public void A_manual_index_option_expiring_that_day_goes_at_15_30_and_today_everything_goes_at_15_30()
    {
        var candidates = new[] { new ManualIntradaySquareOff.Candidate(2, 9, NiftyWeeklyCall, Ist(NiftyExpiry, 10, 0), null) };
        var expiring = new HashSet<string>([NiftyWeeklyCall], StringComparer.Ordinal);
        Assert.Equal("Intraday — squared off at the close (15:30 IST)",
            Assert.Single(ManualIntradaySquareOff.DuePositions(Sessions(), Ist(NiftyExpiry, 15, 31), candidates, expiring)).Reason);

        var todays = new[]
        {
            new ManualIntradaySquareOff.Candidate(1, 9, Share, Ist(Today, 10, 0), null),
            new ManualIntradaySquareOff.Candidate(2, 9, "NSE:NIFTY26N0324500CE", Ist(Today, 10, 0), null),
        };
        var due = ManualIntradaySquareOff.DuePositions(Sessions(), Ist(Today, 15, 30), todays);
        Assert.Equal(2, due.Count);
        Assert.All(due, x => Assert.Equal("Intraday — squared off at the close (15:30 IST)", x.Reason));
    }

    [Fact]
    public void The_ticket_and_the_carry_tick_name_the_close_that_applies()
    {
        Assert.Equal("close (15:30 IST)", PositionCarryForward.CloseName(NiftyWeeklyCall, Today));
        Assert.Equal("close (15:30 IST)", PositionCarryForward.CloseName(Share, MondayFrom));
        Assert.Equal("close (15:45 IST)", PositionCarryForward.CloseName(NiftyWeeklyCall, MondayFrom));
        Assert.Equal("close (15:30 IST)", PositionCarryForward.CloseName(NiftyWeeklyCall, NiftyExpiry));
        Assert.Equal("MCX close", PositionCarryForward.CloseName("MCX:CRUDEOIL26DECFUT", MondayFrom));
    }

    // ---------- expiry ----------

    [Fact]
    public void An_expiring_index_contract_settles_from_15_30_and_a_stock_contract_from_7_December_at_15_45()
    {
        var sessions = Sessions();
        Assert.Equal(Ist(NiftyExpiry, 15, 30), ExpirySettler.ExpiryCloseUtc(sessions, "NSE", "FO", NiftyExpiry, "NIFTY"));
        Assert.Equal(Ist(new DateOnly(2026, 12, 29), 15, 45), ExpirySettler.ExpiryCloseUtc(sessions, "NSE", "FO", new DateOnly(2026, 12, 29), "RELIANCE"));
        Assert.Equal(Ist(new DateOnly(2026, 11, 24), 15, 30), ExpirySettler.ExpiryCloseUtc(sessions, "NSE", "FO", new DateOnly(2026, 11, 24), "RELIANCE"));
        Assert.Equal(Ist(new DateOnly(2026, 11, 3), 15, 30), ExpirySettler.ExpiryCloseUtc(sessions, "NSE", "FO", new DateOnly(2026, 11, 3), "NIFTY"));
    }

    // ---------- starting a run ----------

    [Fact]
    public void A_run_may_start_until_its_options_close_but_not_after_its_expiring_contracts_stopped()
    {
        var sessions = Sessions();

        Assert.Equal("NSE closed at 15:30 IST today: a live run started now would trade on stale prices. Start it on the next "
                     + "trading day, or as a recap to test it on today's session.",
            StrategyStartService.WhyMarketClosed(sessions, Ist(Today, 15, 35), "NIFTY", Nifty));
        Assert.Null(StrategyStartService.WhyMarketClosed(sessions, Ist(MondayFrom, 15, 35), "NIFTY", Nifty));

        var expiryDay = StrategyStartService.WhyMarketClosed(sessions, Ist(NiftyExpiry, 15, 35), "NIFTY", Nifty, indexExpiresToday: true);
        Assert.Contains("NIFTY's expiring contracts stopped trading at 15:30 IST today", expiryDay);
        Assert.Null(StrategyStartService.WhyMarketClosed(sessions, Ist(NiftyExpiry, 15, 25), "NIFTY", Nifty, indexExpiresToday: true));
    }

    // ---------- bars, archive, scanners, replay ----------

    [Fact]
    public async Task From_7_December_an_option_keeps_its_last_quarter_hour_of_bars_and_the_index_does_not()
    {
        using var db = Db();
        var service = new LiveDataService(db, new NoCatalog(), Sessions());

        await service.AppendLiveTickAsync(Tick(NiftyWeeklyCall, Ist(MondayFrom, 15, 40, 5), 101m));
        await service.AppendLiveTickAsync(Tick(Nifty, Ist(MondayFrom, 15, 40, 5), 25_000m));
        await service.AppendLiveTickAsync(Tick(NiftyWeeklyCall, Ist(FridayBefore, 15, 40, 5), 99m));
        await service.AppendLiveTickAsync(Tick(NiftyWeeklyCall, Ist(MondayFrom, 15, 45, 5), 102m));

        var bars = db.LiveBars.AsNoTracking().ToList();
        var bar = Assert.Single(bars);
        Assert.Equal(NiftyWeeklyCall, bar.Symbol);
        Assert.Equal(Ist(MondayFrom, 15, 40), bar.BarStartUtc);
    }

    [Fact]
    public void An_index_future_and_its_index_are_cut_by_their_own_closes()
    {
        Assert.Equal("NSE", CandlePatternRules.SessionKeyOf(Nifty));
        Assert.Equal("NSE FO", CandlePatternRules.SessionKeyOf(NiftyFuture));
        Assert.Equal("MCX", CandlePatternRules.SessionKeyOf("MCX:CRUDEOIL26DECFUT"));
    }

    [Fact]
    public void A_replay_s_progress_runs_to_its_day_s_close()
    {
        Assert.Equal(1, MarketReplayService.ProgressOf(Ist(FridayBefore, 15, 30), FridayBefore, MarketReplayService.StatePlaying));
        Assert.True(MarketReplayService.ProgressOf(Ist(MondayFrom, 15, 30), MondayFrom, MarketReplayService.StatePlaying) < 1);
        Assert.Equal(1, MarketReplayService.ProgressOf(Ist(MondayFrom, 15, 45), MondayFrom, MarketReplayService.StatePlaying));
    }

    // ---------- the quiet windows around the session ----------

    [Fact]
    public void The_jobs_that_wait_out_the_session_wait_15_minutes_longer_from_7_December()
    {
        Assert.Equal(new TimeSpan(15, 40, 0), MarketIntelligenceSchedule.QuietUntilOn(Today));
        Assert.Equal(new TimeSpan(15, 55, 0), MarketIntelligenceSchedule.QuietUntilOn(MondayFrom));
        Assert.Equal(new TimeOnly(15, 45), AiAgentHours.QuietUntilOn(FridayBefore));
        Assert.Equal(new TimeOnly(16, 0), AiAgentHours.QuietUntilOn(MondayFrom));

        Func<DateOnly, bool> tradingDay = _ => true;
        Assert.False(MarketIntelligenceSchedule.InQuietWindow(Ist(Today, 15, 41), tradingDay));
        Assert.True(MarketIntelligenceSchedule.InQuietWindow(Ist(MondayFrom, 15, 41), tradingDay));
        Assert.False(AiAgentHours.InSession(Ist(Today, 15, 50), Sessions()));
        Assert.True(AiAgentHours.InSession(Ist(MondayFrom, 15, 50), Sessions()));
    }

    // ---------- the AI Trader ----------

    [Fact]
    public void The_AI_Trader_s_live_book_names_its_close_and_its_shadow_book_keeps_15_30()
    {
        static string Live(DateOnly day) => AiTraderBookReader.Describe(
            new AiTraderBook(Ist(day, 11, 0), true, false, 0m, 0, [], []), new AiTraderRules(), AiTraderModes.Live);
        static string Shadow(DateOnly day) => AiTraderBookReader.Describe(
            new AiTraderBook(Ist(day, 11, 0), true, false, 0m, 0, [], []), new AiTraderRules(), AiTraderModes.Shadow);

        Assert.Contains("checked every few seconds, and squared off at 15:30; a Validator", Live(Today));
        Assert.Contains("and squared off at 15:45 (15:30 for a contract expiring today); a Validator", Live(MondayFrom));
        Assert.Contains("squared off at 15:30; nothing reaches a broker", Shadow(MondayFrom));
    }

    // ---------- helpers ----------

    private static TradingDbContext Db() => new(new DbContextOptionsBuilder<TradingDbContext>()
        .UseInMemoryDatabase($"derivatives-close-{Guid.NewGuid():N}")
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
        .Options);

    private static Instrument Option(string symbol, string underlying, DateOnly expiry) => new()
    {
        Symbol = symbol, Exchange = symbol[..3], Segment = "FO", InstrumentType = "CE", OptionType = "CE",
        Underlying = underlying, ExpiryDate = expiry, IsEnabled = true, StrikePrice = 1m,
    };

    private static UpsertLiveTickRequest Tick(string symbol, DateTime stampUtc, decimal price) => new()
    {
        Symbol = symbol, DataType = "symbolUpdate", SourceKey = "dhan", ExchangeTimestampUtc = stampUtc,
        LastTradedPrice = price, RawPayload = "{}",
    };

    private sealed class NoCatalog : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => Array.Empty<ProviderDescriptor>();
        public ProviderDescriptor? Find(string providerKey) => null;
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
