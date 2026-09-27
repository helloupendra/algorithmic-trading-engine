using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Services;
using Xunit;
using static AlgoTrading.Api.Services.MarketCloseRules;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Which live runs the market-hours sweep squares off, and when.
///
/// Until 27 Sep it stopped every run at 15:30 IST, so the morning plan's
/// CrudeMomentum runs lost the whole MCX evening; and since it remembered
/// "done today" only in memory, an API process started after 15:30 squared off
/// a crude run started by hand for the evening (run 104, 22:40:15 on 10 Sep),
/// while one restarted during the evening never closed it at all. The owner's
/// decision that day: crude trades the MCX evening, until the MCX close.
/// </summary>
public class MarketCloseRulesTests
{
    private const string CrudeFuture = "MCX:CRUDEOIL26OCTFUT";
    private const string Nifty = "NSE:NIFTY50-INDEX";
    private const string Sensex = "BSE:SENSEX-INDEX";

    /// <summary>IST wall time → the UTC instant.</summary>
    private static DateTime Ist(int y, int mo, int d, int h, int mi = 0, int s = 0)
        => new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc).AddMinutes(-330);

    // Monday 28 Sep 2026, an ordinary day. New York is on daylight saving until
    // 1 Nov, so MCX closes at 23:30.
    private static DateTime Mon(int h, int mi = 0, int s = 0) => Ist(2026, 9, 28, h, mi, s);

    private static readonly DeskRun MorningNifty = new(301, "NIFTY", Nifty, Mon(9, 16));
    private static readonly DeskRun MorningSensex = new(302, "SENSEX", Sensex, Mon(9, 16));
    private static readonly DeskRun MorningCrude = new(313, "CRUDEOIL", CrudeFuture, Mon(9, 16));

    private static MarketHoliday Holiday(string exchange, DateOnly date, MarketClosure closure = MarketClosure.FullDay)
        => new() { Exchange = exchange, Date = date, Name = "test holiday", Closure = closure, Source = "test" };

    private static MarketSessionService Sessions(IEnumerable<MarketHoliday>? holidays = null, IEnumerable<MarketSpecialSession>? specials = null)
        => new(new FakeCalendar(holidays ?? [], specials ?? []));

    private static IReadOnlyList<RunToStop> Due(DateTime nowUtc, params DeskRun[] runs) => RunsToStop(Sessions(), nowUtc, runs);

    private static long[] Ids(IEnumerable<RunToStop> due) => due.Select(x => x.RunId).OrderBy(x => x).ToArray();

    [Fact]
    public void At_the_equity_close_index_runs_stop_and_the_crude_run_trades_on()
    {
        Assert.Empty(Due(Mon(15, 29, 59), MorningNifty, MorningSensex, MorningCrude));

        var due = Due(Mon(15, 30), MorningNifty, MorningSensex, MorningCrude);

        Assert.Equal(new long[] { 301, 302 }, Ids(due));
        Assert.All(due, x => Assert.Equal("Market closed (15:30 IST)", x.Reason));
        Assert.Equal(Bse, due.Single(x => x.RunId == 302).Exchange);
    }

    [Fact]
    public void The_crude_run_is_squared_off_at_the_MCX_close_with_that_close_in_its_reason()
    {
        Assert.Empty(Due(Mon(23, 29, 59), MorningCrude));

        var due = Assert.Single(Due(Mon(23, 30), MorningCrude));

        Assert.Equal(313, due.RunId);
        Assert.Equal(Mcx, due.Exchange);
        Assert.Equal("MCX closed (23:30 IST)", due.Reason);
        Assert.Equal(Mon(23, 30), due.ClosedAtUtc);
    }

    [Fact]
    public void In_the_US_winter_crude_trades_past_23_35_to_23_55()
    {
        // Monday 7 Dec 2026: New York is on standard time. 23:35 is when the desk's
        // market-close.sh used to stop every run.
        var crude = MorningCrude with { StartedUtc = Ist(2026, 12, 7, 9, 16) };

        Assert.Empty(Due(Ist(2026, 12, 7, 23, 35), crude));
        Assert.Equal("MCX closed (23:55 IST)", Assert.Single(Due(Ist(2026, 12, 7, 23, 55), crude)).Reason);
    }

    [Fact]
    public void An_API_started_after_the_equity_close_stops_the_index_runs_and_leaves_crude_trading()
    {
        // Adopted at 18:00 with the morning's start times, as ReconcileOrphanedRunsAsync rebuilds them.
        var due = Due(Mon(18, 0), MorningNifty, MorningCrude);

        Assert.Equal(new long[] { 301 }, Ids(due));
    }

    [Fact]
    public void A_crude_run_started_by_hand_for_the_evening_lives_to_the_MCX_close()
    {
        // Run 104 on 10 Sep: started at 19:25 and squared off at 22:40:15 by an API
        // process that had just started, with 50 minutes of MCX left.
        var evening = new DeskRun(104, "CRUDEOIL", CrudeFuture, Mon(19, 25));

        Assert.Empty(Due(Mon(22, 40, 15), evening));
        Assert.Equal(new long[] { 104 }, Ids(Due(Mon(23, 30), evening)));
    }

    [Fact]
    public void A_crude_run_adopted_after_the_MCX_close_is_stopped_at_once_with_the_close_it_missed()
    {
        Assert.Equal("MCX closed (23:30 IST)", Assert.Single(Due(Mon(23, 40), MorningCrude)).Reason);

        // The API was down across the close and came back after midnight.
        var afterMidnight = Assert.Single(Due(Ist(2026, 9, 29, 0, 10), MorningCrude));
        Assert.Equal("MCX closed (23:30 IST)", afterMidnight.Reason);
        Assert.Equal(Mon(23, 30), afterMidnight.ClosedAtUtc);
    }

    [Fact]
    public void A_run_started_after_its_market_closed_waits_for_the_next_close()
    {
        // An evening replay of the day's NSE session: the old sweep, which fired once
        // at 15:30, left such a run alone, and so does this — past midnight too.
        var replay = new DeskRun(401, "NIFTY", Nifty, Mon(18, 0));

        Assert.Empty(Due(Mon(23, 0), replay));
        Assert.Empty(Due(Ist(2026, 9, 29, 0, 10), replay));
        Assert.Equal(new long[] { 401 }, Ids(Due(Ist(2026, 9, 29, 15, 30), replay)));
    }

    [Fact]
    public void On_an_NSE_holiday_with_an_MCX_evening_each_run_stops_at_its_own_close()
    {
        // Dussehra, Tuesday 20 Oct 2026: NSE and BSE shut all day; MCX shut in the
        // morning and trading from 17:00 to 23:30 (the 2026 circulars).
        var dussehra = new DateOnly(2026, 10, 20);
        var sessions = Sessions([
            Holiday("NSE", dussehra), Holiday("BSE", dussehra), Holiday("MCX", dussehra, MarketClosure.MorningSession)]);
        var handNifty = new DeskRun(501, "NIFTY", Nifty, Ist(2026, 10, 20, 10, 0));
        var eveningCrude = new DeskRun(502, "CRUDEOIL", CrudeFuture, Ist(2026, 10, 20, 17, 5));

        // A weekday on the holiday list keeps its usual 15:30, as the weekday sweep always had.
        var atEquityClose = RunsToStop(sessions, Ist(2026, 10, 20, 15, 30), [handNifty]);
        Assert.Equal("Market closed (15:30 IST)", Assert.Single(atEquityClose).Reason);

        Assert.Equal(new long[] { 501 }, Ids(RunsToStop(sessions, Ist(2026, 10, 20, 23, 29), [handNifty, eveningCrude])));
        Assert.Equal(new long[] { 501, 502 }, Ids(RunsToStop(sessions, Ist(2026, 10, 20, 23, 30), [handNifty, eveningCrude])));
    }

    [Fact]
    public void A_morning_closure_on_MCX_leaves_the_crude_run_for_the_evening()
    {
        // MCX shut until 17:00 while NSE trades: the crude run the morning job
        // started waits for the evening session instead of being cut at 15:30.
        var sessions = Sessions([Holiday("MCX", new DateOnly(2026, 9, 28), MarketClosure.MorningSession)]);

        Assert.Equal(new long[] { 301 }, Ids(RunsToStop(sessions, Mon(15, 30), [MorningNifty, MorningCrude])));
        Assert.Equal(new long[] { 301, 313 }, Ids(RunsToStop(sessions, Mon(23, 30), [MorningNifty, MorningCrude])));
    }

    [Fact]
    public void When_MCX_shuts_its_evening_the_crude_run_stops_at_17_00()
    {
        // New Year's Day 2026, a Thursday: NSE trades; MCX closes after its morning session.
        var sessions = Sessions([Holiday("MCX", new DateOnly(2026, 1, 1), MarketClosure.EveningSession)]);
        var nifty = MorningNifty with { StartedUtc = Ist(2026, 1, 1, 9, 16) };
        var crude = MorningCrude with { StartedUtc = Ist(2026, 1, 1, 9, 16) };

        Assert.Equal(new long[] { 301 }, Ids(RunsToStop(sessions, Ist(2026, 1, 1, 15, 30), [nifty, crude])));
        var atMcxClose = RunsToStop(sessions, Ist(2026, 1, 1, 17, 0), [nifty, crude]);
        Assert.Equal("MCX closed (17:00 IST)", atMcxClose.Single(x => x.RunId == 313).Reason);
    }

    [Fact]
    public void A_weekend_has_no_close_unless_the_calendar_holds_a_session()
    {
        var saturdayRun = new DeskRun(601, "NIFTY", Nifty, Ist(2026, 9, 26, 10, 0));
        Assert.Empty(Due(Ist(2026, 9, 26, 16, 0), saturdayRun));
        Assert.Equal(new long[] { 601 }, Ids(Due(Mon(15, 30), saturdayRun)));

        // Sunday 1 Feb 2026: the Union Budget's live session, 09:15 to 15:30.
        var budget = new DateOnly(2026, 2, 1);
        var sessions = Sessions(specials: [
            new MarketSpecialSession { Exchange = "NSE", Date = budget, Name = "Union Budget", OpenIst = new TimeOnly(9, 15), CloseIst = new TimeOnly(15, 30) }]);
        var budgetRun = new DeskRun(602, "NIFTY", Nifty, Ist(2026, 2, 1, 9, 16));
        Assert.Empty(RunsToStop(sessions, Ist(2026, 2, 1, 15, 29), [budgetRun]));
        Assert.Single(RunsToStop(sessions, Ist(2026, 2, 1, 15, 30), [budgetRun]));
    }

    [Theory]
    [InlineData("CRUDEOIL", "MCX:CRUDEOIL26OCTFUT", Mcx)]
    [InlineData("CRUDEOILM", "MCX:CRUDEOILM26OCTFUT", Mcx)]
    [InlineData("CRUDEOIL", "", Mcx)]                       // a commodity with no symbol stored yet
    [InlineData("", "MCX:NATURALGAS26OCTFUT", Mcx)]         // the symbol alone is enough
    [InlineData("NIFTY", "NSE:NIFTY50-INDEX", Nse)]
    [InlineData("BANKNIFTY", "NSE:NIFTYBANK-INDEX", Nse)]
    [InlineData("SENSEX", "BSE:SENSEX-INDEX", Bse)]
    [InlineData("RELIANCE", null, Nse)]
    public void A_run_trades_with_the_exchange_of_its_price_symbol(string underlying, string? spot, string expected)
    {
        Assert.Equal(expected, ExchangeOf(underlying, spot));
    }

    [Fact]
    public void The_reasons_are_the_words_the_console_and_Sentinel_already_read()
    {
        // Sentinel's trading agent counts "market closed" and "mcx closed" stops by
        // market-hours as deliberate; the console shortens both at the bracket.
        Assert.Equal(MarketHoursService.MarketClosedReason, ReasonFor(Nse, Mon(15, 30)));
        Assert.StartsWith(MarketHoursService.McxClosedReason + " (", ReasonFor(Mcx, Mon(23, 30)));
    }

    private sealed class FakeCalendar(IEnumerable<MarketHoliday> holidays, IEnumerable<MarketSpecialSession> sessions) : IMarketCalendar
    {
        private readonly List<MarketHoliday> _holidays = holidays.ToList();
        private readonly List<MarketSpecialSession> _sessions = sessions.ToList();
        public bool IsLoaded => true;
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => _holidays.FirstOrDefault(h => h.Exchange == exchange && h.Date == date);
        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => _sessions.FirstOrDefault(s => s.Exchange == exchange && s.Date == date);
        public bool HasYear(string exchange, int year) => true;
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
