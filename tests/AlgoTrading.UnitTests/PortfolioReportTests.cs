using AlgoTrading.Api.Services.Portfolio;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Portfolio;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The portfolio report's arithmetic, against figures worked out by hand: holdings merged across brokers by ISIN,
/// weights and concentration, and the risk figures on a fixed price series.
/// </summary>
public class PortfolioReportTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc); // Friday 15:30 IST

    // ---------- merging ----------

    [Fact]
    public void Holdings_of_one_isin_at_two_brokers_are_one_holding_valued_at_the_broker_s_price()
    {
        var fyers = Snapshot("fyers", Holding("NSE:RELIANCE-EQ", "INE002A01018", qty: 20, avg: 2450, ltp: 2520));
        // Dhan sends no price: the same security is valued at FYERS's.
        var dhan = Snapshot("dhan", Holding("RELIANCE", "INE002A01018", qty: 10, avg: 2550, ltp: null) with { PriceSource = PriceSources.None });

        var report = Build(fyers, dhan);

        var h = Assert.Single(report.Holdings);
        Assert.Equal("RELIANCE", h.Symbol);
        Assert.Equal(["fyers", "dhan"], h.Brokers);
        Assert.Equal(30m, h.Qty);
        Assert.Equal(74500m, h.Invested);            // 20 × 2450 + 10 × 2550
        Assert.Equal(2483.33m, h.AvgPrice);           // 74500 / 30
        Assert.Equal(2520m, h.Ltp);
        Assert.Equal(75600m, h.Value);                // 30 × 2520
        Assert.Equal(1100m, h.Pnl);
        Assert.Equal(1.48m, h.PnlPct);                // 1100 / 74500, a percent
        Assert.Equal(1m, h.Weight);                   // the whole of the value, a fraction
        Assert.Equal(1, report.Totals.HoldingsCount);
        Assert.Equal((74500m, 75600m, 1100m), (report.Totals.Invested, report.Totals.Current, report.Totals.Pnl));

        // Each broker's share of the value, at the one price.
        Assert.Equal([("FYERS", 50400m), ("Dhan", 25200m)], report.Allocation.ByBroker.Select(s => (s.Name, s.Value)));
    }

    [Fact]
    public void A_holding_without_any_price_is_listed_but_never_valued_at_its_cost()
    {
        var report = Build(Snapshot("dhan",
            Holding("HDFCBANK", "INE040A01034", qty: 25, avg: 1600, ltp: null) with { PriceSource = PriceSources.None },
            Holding("NSE:TCS-EQ", "INE467B01029", qty: 2, avg: 3400, ltp: 3550)));

        var unpriced = report.Holdings.Single(h => h.Isin == "INE040A01034");
        Assert.Null(unpriced.Value);
        Assert.Null(unpriced.Weight);
        Assert.Equal(40000m, unpriced.Invested);
        // The totals cover the priced holding only, so the P&L compares like with like.
        Assert.Equal((6800m, 7100m, 300m), (report.Totals.Invested, report.Totals.Current, report.Totals.Pnl));
        Assert.Equal(2, report.Totals.HoldingsCount);
        Assert.Contains(report.DataNotes, n => n.Contains("1 holding has no price (HDFCBANK)"));
    }

    [Fact]
    public void Sector_and_market_cap_are_unknown_and_said_to_be_when_the_desk_has_no_classification()
    {
        var report = Build(Snapshot("fyers", Holding("NSE:TCS-EQ", "INE467B01029", qty: 2, avg: 3400, ltp: 3550)));

        Assert.Equal("Unknown", report.Holdings[0].Sector);
        Assert.Equal("Unknown", report.Holdings[0].MarketCapBand);
        Assert.Equal([("Unknown", 1m)], report.Allocation.BySector.Select(s => (s.Name, s.Weight)));
        Assert.Contains(report.DataNotes, n => n.StartsWith("Sector and market-cap band are Unknown for every holding"));
        Assert.Equal("Information about your own holdings, not investment advice.", report.Disclaimer);
    }

    // ---------- weights and concentration ----------

    [Fact]
    public void Weights_hhi_and_the_top_five_weight_are_fractions_of_current_value()
    {
        // Values 40,000 / 20,000 / 15,000 / 10,000 / 10,000 / 5,000 of 1,00,000.
        var report = Build(Snapshot("fyers",
            Holding("NSE:AAA-EQ", "INE000000001", qty: 1, avg: 30000, ltp: 40000),
            Holding("NSE:BBB-EQ", "INE000000002", qty: 1, avg: 20000, ltp: 20000),
            Holding("NSE:CCC-EQ", "INE000000003", qty: 1, avg: 10000, ltp: 15000),
            Holding("NSE:DDD-EQ", "INE000000004", qty: 1, avg: 12000, ltp: 10000),
            Holding("NSE:EEE-EQ", "INE000000005", qty: 1, avg: 10000, ltp: 10000),
            Holding("NSE:FFF-EQ", "INE000000006", qty: 1, avg: 5000, ltp: 5000)));

        Assert.Equal([0.4m, 0.2m, 0.15m, 0.1m, 0.1m, 0.05m], report.Holdings.Select(h => h.Weight!.Value));
        Assert.Equal(0.4m, report.Concentration.Top1Weight);
        Assert.Equal(0.95m, report.Concentration.Top5Weight);
        Assert.Equal(0.245m, report.Concentration.Hhi);  // 0.4² + 0.2² + 0.15² + 0.1² + 0.1² + 0.05², between 0 and 1
        Assert.Equal(["AAA", "BBB", "CCC", "DDD", "EEE"], report.Concentration.Largest);
        Assert.Equal(100000m, report.Totals.Current);
    }

    // ---------- risk ----------

    // NIFTY 50: +10%, −10%, +10%. A: +20%, −20%, +20%. B: flat.
    private static readonly decimal[] Nifty = [100m, 110m, 99m, 108.9m];
    private static readonly decimal[] A = [50m, 60m, 48m, 57.6m];
    private static readonly decimal[] Flat = [10m, 10m, 10m, 10m];

    [Fact]
    public void Beta_drawdown_and_volatility_of_one_holding_match_the_hand_computed_values()
    {
        var risk = PortfolioRiskMath.Compute([Risk("A", 100, A)], Index(Nifty), minSessions: 2);

        // r = [0.2, −0.2, 0.2] against [0.1, −0.1, 0.1]: cov 0.026667 / var 0.013333 = 2.
        Assert.Equal(2.00m, risk.BetaVsNifty);
        // 1 → 1.2 → 0.96: a fall of 0.24 from the 1.2 peak, 20%.
        Assert.Equal(20.00m, risk.MaxDrawdownPct);
        // Sample sd 0.2309401 × √252 = 3.6660606.
        Assert.Equal(366.61m, risk.VolatilityAnnualPct);
        Assert.Equal(3, risk.Sessions);
        Assert.Equal(1m, risk.CoveredWeight);
    }

    [Fact]
    public void Today_s_weights_scale_the_returns_of_the_holdings_they_weight()
    {
        // 60% in A, 40% flat: r = 0.6 × A's = [0.12, −0.12, 0.12].
        var risk = PortfolioRiskMath.Compute([Risk("A", 60, A), Risk("B", 40, Flat)], Index(Nifty), minSessions: 2);

        Assert.Equal(1.20m, risk.BetaVsNifty);
        Assert.Equal(12.00m, risk.MaxDrawdownPct);    // 1.12 → 0.9856
        Assert.Equal(219.96m, risk.VolatilityAnnualPct);
        Assert.Equal(1m, risk.CoveredWeight);
    }

    [Fact]
    public void A_holding_without_a_close_on_every_session_is_left_out_and_the_covered_weight_says_how_much_was_measured()
    {
        var gappy = new Dictionary<DateOnly, decimal> { [Day(0)] = 10m, [Day(1)] = 11m, [Day(3)] = 12m }; // no close on day 2
        var risk = PortfolioRiskMath.Compute(
            [Risk("A", 60, A), new RiskHolding("NEWLISTING", 40, gappy), new RiskHolding("NOHISTORY", 0, null)],
            Index(Nifty), minSessions: 2);

        Assert.Equal(0.6m, risk.CoveredWeight);
        Assert.Equal(2.00m, risk.BetaVsNifty);        // A alone, re-weighted to the whole of what is measured
        Assert.Contains(risk.Gaps, g => g.Contains("NEWLISTING"));
        Assert.Contains("60% of value", risk.Note);
    }

    [Fact]
    public void Pct_fields_are_percents_weights_are_fractions_and_the_largest_fall_is_a_positive_magnitude()
    {
        var risk = PortfolioRiskMath.Compute([Risk("A", 100, A)], Index(Nifty), minSessions: 2);

        Assert.True(risk.MaxDrawdownPct > 0);          // a 20% fall is 20, not -20 and not 0.2
        Assert.Equal(20.00m, risk.MaxDrawdownPct);
        Assert.InRange(risk.CoveredWeight, 0m, 1m);
        Assert.Contains("over 3 trading days", risk.Note);

        var report = Build(Snapshot("fyers",
            Holding("NSE:AAA-EQ", "INE000000001", qty: 1, avg: 800, ltp: 900),
            Holding("NSE:BBB-EQ", "INE000000002", qty: 1, avg: 100, ltp: 100)));
        Assert.Equal(11.11m, report.Totals.PnlPct);    // 100 / 900, 11.11 meaning 11.11%
        Assert.Equal(12.5m, report.Holdings[0].PnlPct); // 100 / 800
        Assert.Equal(0.9m, report.Holdings[0].Weight);  // 900 of 1,000
        Assert.All(report.Allocation.ByBroker, s => Assert.InRange(s.Weight, 0m, 1m));
    }

    [Fact]
    public void A_split_the_prices_do_not_adjust_for_is_never_read_as_a_crash()
    {
        var risk = PortfolioRiskMath.Compute([Risk("A", 50, A), Risk("SPLIT", 50, [1000m, 1000m, 100m, 100m])], Index(Nifty), minSessions: 2);

        Assert.Equal(0.5m, risk.CoveredWeight);
        Assert.Equal(20.00m, risk.MaxDrawdownPct);   // A's, not a 90% fall
        Assert.Contains(risk.Gaps, g => g.StartsWith("SPLIT is left out") && g.Contains("split or bonus"));
    }

    [Fact]
    public void Too_few_sessions_give_no_figures_rather_than_noisy_ones()
    {
        var risk = PortfolioRiskMath.Compute([Risk("A", 100, A)], Index(Nifty));

        Assert.Null(risk.VolatilityAnnualPct);
        Assert.Null(risk.BetaVsNifty);
        Assert.Null(risk.MaxDrawdownPct);
        Assert.Contains("fewer than the 20", risk.Note);
    }

    [Fact]
    public void The_report_measures_risk_from_each_holding_s_closes_under_the_desk_s_symbol()
    {
        // 25 sessions: NIFTY alternates +1% and -1%, AAA +2% and -2%; ZZZ has no closes stored at all.
        var nifty = Alternating(100m, 0.01m, 25);
        var aaa = Alternating(50m, 0.02m, 25);
        var closes = new Dictionary<string, IReadOnlyList<DailyClose>> { ["NSE:AAA-EQ"] = aaa };
        var report = PortfolioReportBuilder.Build(new PortfolioReportInputs(Now,
            [Snapshot("fyers",
                Holding("NSE:AAA-EQ", "INE000000001", qty: 1, avg: 50, ltp: 60) with { CanonicalSymbol = "NSE:AAA-EQ" },
                Holding("NSE:ZZZ-EQ", "INE000000009", qty: 1, avg: 40, ltp: 40))],
            closes, nifty));

        Assert.Equal(25, report.Risk.LookbackDays);
        Assert.Equal(0.6m, report.Risk.CoveredWeight);
        Assert.Equal(2.00m, report.Risk.BetaVsNifty);
        Assert.Contains(report.DataNotes, n => n.Contains("ZZZ") && n.Contains("left out of the risk figures"));
    }

    // ---------- pricing ----------

    [Fact]
    public void A_holding_dhan_sends_without_a_price_is_valued_at_the_desk_s_close_of_the_session()
    {
        var instruments = new Dictionary<string, (string, string)> { ["INE040A01034"] = ("NSE:HDFCBANK-EQ", "HDFC BANK LTD") };
        var friday = new DateOnly(2026, 10, 16);
        var thursday = new DateOnly(2026, 10, 15);
        var closes = new Dictionary<string, IReadOnlyList<DailyClose>>
        {
            ["NSE:HDFCBANK-EQ"] = [new(thursday, 1650m), new(friday, 1700m)],
        };
        var dhan = Holding("HDFCBANK", "INE040A01034", qty: 25, avg: 1600, ltp: null);

        var priced = PortfolioPrices.Enrich(dhan, instruments, closes, priceSession: friday, previousSession: thursday);

        Assert.Equal(("NSE:HDFCBANK-EQ", "HDFC BANK LTD"), (priced.CanonicalSymbol, priced.Name));
        Assert.Equal((1700m, PriceSources.DeskClose, friday), (priced.Ltp, priced.PriceSource, priced.PriceDate));
        Assert.Equal((1650m, thursday), (priced.PrevClose, priced.PrevCloseDate));

        // Mid-session on Monday the newest close is Friday's: valued at it, but with no day change, since Friday's close
        // is not Monday's price.
        var monday = new DateOnly(2026, 10, 19);
        var midSession = PortfolioPrices.Enrich(dhan, instruments, closes, priceSession: monday, previousSession: friday);
        Assert.Equal((1700m, friday), (midSession.Ltp, midSession.PriceDate));
        Assert.Null(midSession.PrevClose);
    }

    [Fact]
    public void The_day_change_is_the_price_against_the_previous_session_s_close()
    {
        var report = Build(Snapshot("fyers",
            Holding("NSE:TCS-EQ", "INE467B01029", qty: 10, avg: 3400, ltp: 3550) with { PrevClose = 3500m }));

        Assert.Equal(500m, report.Holdings[0].DayChange);
        Assert.Equal(500m, report.Totals.DayChange);
        Assert.Equal(1.43m, report.Totals.DayChangePct); // 500 / 35,000
    }

    [Fact]
    public void A_snapshot_from_an_earlier_session_adds_no_old_move_to_the_day_s_change()
    {
        var thursday = new DateOnly(2026, 10, 15);
        var wednesday = new DateOnly(2026, 10, 14);
        // Read on Friday: its previous close is Thursday's. Read on Thursday (a broker whose sync failed since): Wednesday's.
        var fresh = Snapshot("fyers", Holding("NSE:TCS-EQ", "INE467B01029", qty: 10, avg: 3400, ltp: 3550) with { PrevClose = 3500m, PrevCloseDate = thursday });
        var stale = Snapshot("angel", Holding("ITC-EQ", "INE154A01025", qty: 100, avg: 395, ltp: 410) with { PrevClose = 400m, PrevCloseDate = wednesday });
        var dhan = Snapshot("dhan", Holding("HDFCBANK", "INE040A01034", qty: 25, avg: 1600, ltp: 1700) with
        {
            PriceSource = PriceSources.DeskClose, PriceDate = thursday,
        });

        var report = PortfolioReportBuilder.Build(new PortfolioReportInputs(Now, [fresh, stale, dhan],
            new Dictionary<string, IReadOnlyList<DailyClose>>(), [], PriceSession: new DateOnly(2026, 10, 16), PreviousSession: thursday));

        Assert.Equal(500m, report.Totals.DayChange);                       // TCS only
        Assert.Null(report.Holdings.Single(h => h.Isin == "INE154A01025").DayChange);
        Assert.Contains(report.DataNotes, n => n.StartsWith("The day's change is left out for 1 holding (ITC)") && n.Contains("earlier session"));
        Assert.Contains(report.DataNotes, n => n.StartsWith("Today's change is not known yet for 1 holding (HDFCBANK)"));
        Assert.DoesNotContain(report.DataNotes, n => n.Contains("no stored close for the session before"));
    }

    [Fact]
    public void A_broken_index_row_is_skipped_rather_than_failing_every_report()
    {
        var index = Index(Nifty).Append(new DailyClose(Day(4), 0m)).ToList();

        var risk = PortfolioRiskMath.Compute([Risk("A", 100, A)], index, minSessions: 2);

        Assert.Equal(2.00m, risk.BetaVsNifty);
    }

    [Fact]
    public void No_data_note_uses_a_word_the_advice_guard_refuses()
    {
        // The model is asked to name the report's gaps; a note it copies must not fail the guard.
        var report = PortfolioReportBuilder.Build(new PortfolioReportInputs(Now,
            [Snapshot("dhan",
                Holding("HDFCBANK", "INE040A01034", qty: 25, avg: 1600, ltp: null) with { PriceSource = PriceSources.None },
                Holding("NSE:TCS-EQ", "INE467B01029", qty: 2, avg: 3400, ltp: 3550))],
            new Dictionary<string, IReadOnlyList<DailyClose>>(), Index(Nifty)));
        var risk = PortfolioRiskMath.Compute([Risk("A", 100, A)], Index(Nifty));

        Assert.All(report.DataNotes.Append(risk.Note).Append(report.Risk.Note), n => Assert.Empty(PortfolioAdviceGuard.Find(n)));
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3, 4 }, null)]           // every NIFTY session, through the last
    [InlineData(new[] { 0, 1, 2, 3 }, 4)]                // the newest session is missing
    [InlineData(new[] { 0, 1, 3, 4 }, 2)]                // a hole in the middle
    [InlineData(new[] { 2, 3, 4 }, 0)]                   // stored for a shorter span: fetched from the first session
    public void History_is_fetched_from_the_first_session_it_lacks(int[] stored, int? expected)
    {
        var have = stored.Select(i => new DailyClose(Day(i), 100m)).ToList();
        var nifty = Enumerable.Range(0, 5).Select(Day).ToList();

        Assert.Equal(expected is int e ? Day(e) : null, PortfolioPrices.FirstMissing(have, nifty, Day(0), Day(4)));
    }

    [Fact]
    public void A_symbol_with_nothing_stored_is_fetched_for_the_whole_window_and_one_starting_late_from_its_start()
    {
        var from = new DateOnly(2025, 10, 16);
        var through = new DateOnly(2026, 10, 15);

        Assert.Equal(from, PortfolioPrices.FirstMissing([], [], from, through));
        Assert.Equal(from, PortfolioPrices.FirstMissing([new DailyClose(new DateOnly(2026, 9, 1), 1m), new DailyClose(through, 1m)], [], from, through));
        Assert.Null(PortfolioPrices.FirstMissing([new DailyClose(from.AddDays(3), 1m), new DailyClose(through, 1m)], [], from, through));
    }

    [Theory]
    [InlineData("2026-10-17T06:30:00Z", "2026-10-16")] // Saturday: Friday's session
    [InlineData("2026-10-19T02:30:00Z", "2026-10-16")] // Monday 08:00 IST, before the open: still Friday's
    [InlineData("2026-10-19T04:30:00Z", "2026-10-19")] // Monday 10:00 IST: Monday's
    [InlineData("2026-10-23T04:30:00Z", "2026-10-21")] // Friday 23 Oct, a holiday here: Wednesday's, Thursday 22 is one too
    public void A_price_belongs_to_the_session_open_at_the_time_or_the_last_one_before(string nowUtc, string expected)
    {
        var sessions = new PortfolioSessions(new FakeCalendar(new DateOnly(2026, 10, 22), new DateOnly(2026, 10, 23)));

        Assert.Equal(DateOnly.Parse(expected), sessions.PriceSession(DateTimeOffset.Parse(nowUtc).UtcDateTime));
    }

    [Fact]
    public void Only_a_session_whose_close_is_settled_is_complete()
    {
        var sessions = new PortfolioSessions(new FakeCalendar());

        Assert.Equal(new DateOnly(2026, 10, 16), sessions.LastCompletedSession(new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Utc)));  // Mon 14:30 IST
        Assert.Equal(new DateOnly(2026, 10, 19), sessions.LastCompletedSession(new DateTime(2026, 10, 19, 11, 0, 0, DateTimeKind.Utc))); // Mon 16:30 IST
    }

    [Theory]
    [InlineData("NSE:BAJAJ-AUTO-EQ", "BAJAJ-AUTO")]
    [InlineData("TCS-EQ", "TCS")]
    [InlineData("RELIANCE", "RELIANCE")]
    public void A_symbol_is_shown_without_its_exchange_or_series(string raw, string shown)
    {
        Assert.Equal(shown, PortfolioReportBuilder.DisplaySymbol(raw));
    }

    // ---------- helpers ----------

    internal static BrokerHolding Holding(string symbol, string isin, decimal qty, decimal avg, decimal? ltp) => new()
    {
        Symbol = symbol,
        Exchange = symbol.StartsWith("NSE:", StringComparison.Ordinal) ? "NSE" : string.Empty,
        Isin = isin,
        Quantity = qty,
        AvgPrice = avg,
        Ltp = ltp,
    };

    internal static PortfolioBrokerSnapshot Snapshot(string broker, params BrokerHolding[] holdings) =>
        new(broker, 1, Now, new BrokerPortfolio(holdings, [], null));

    private static Contracts.Portfolio.PortfolioReport Build(params PortfolioBrokerSnapshot[] snapshots) =>
        PortfolioReportBuilder.Build(new PortfolioReportInputs(Now, snapshots, new Dictionary<string, IReadOnlyList<DailyClose>>(), []));

    private static DateOnly Day(int i) => new DateOnly(2026, 10, 12).AddDays(i);

    private static IReadOnlyList<DailyClose> Index(decimal[] closes) => closes.Select((c, i) => new DailyClose(Day(i), c)).ToList();

    private static IReadOnlyList<DailyClose> Alternating(decimal start, decimal step, int returns)
    {
        var list = new List<DailyClose> { new(Day(0), start) };
        for (int i = 1; i <= returns; i++) list.Add(new DailyClose(Day(i), list[^1].Close * (i % 2 == 1 ? 1 + step : 1 - step)));
        return list;
    }

    private static RiskHolding Risk(string symbol, decimal value, decimal[] closes) =>
        new(symbol, value, closes.Select((c, i) => (Day(i), c)).ToDictionary(x => x.Item1, x => x.c));

    /// <summary>NSE's calendar with the given holidays and no special sessions.</summary>
    internal sealed class FakeCalendar(params DateOnly[] holidays) : IMarketCalendar
    {
        public bool IsLoaded => true;

        public MarketHoliday? HolidayOn(string exchange, DateOnly date) =>
            holidays.Contains(date) ? new MarketHoliday { Exchange = exchange, Date = date, Name = "Holiday" } : null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
