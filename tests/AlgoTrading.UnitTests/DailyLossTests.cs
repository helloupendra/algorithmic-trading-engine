using System.Globalization;
using AlgoTrading.Application.Exceptions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The risk gate's "max daily loss" (<see cref="DailyLoss"/>, <see cref="RiskManagementService"/>): the run's net
/// P&amp;L for the current IST day, after charges.
/// </summary>
/// <remarks>
/// Until 2 Nov 2026 it summed the run's whole life, before charges. A manual book or the AI Trader's book, which
/// live for weeks, were shut for good by old losses ("MAX DAILY LOSS EXCEEDED" on a day they had lost nothing),
/// a book up in total could lose more than the limit in a day, and a run whose charges drained the account while
/// its gross stayed near zero was never stopped.
/// </remarks>
public sealed class DailyLossTests
{
    private const decimal Limit = -10_000m;
    private const int Lot = 65;
    private const string Call = "NSE:NIFTY26N0324000CE";
    private const string Put = "NSE:NIFTY26N0323800PE";

    // Monday 2 Nov 2026, 11:00 IST; the session before it was Friday 30 Oct.
    private static readonly DateTime Now = IstTime.FromIst(new DateTime(2026, 11, 2, 11, 0, 0));
    private static readonly DateTime DayStart = IstTime.StartOfDayUtc(new DateOnly(2026, 11, 2));
    private static DateTime Today(int hour, int minute) => IstTime.FromIst(new DateTime(2026, 11, 2, hour, minute, 0));
    private static DateTime Friday(int hour, int minute) => IstTime.FromIst(new DateTime(2026, 10, 30, hour, minute, 0));

    // ------------------------------------------------------------ the gate

    [Fact]
    public async Task A_long_lived_book_is_not_locked_out_by_losses_from_earlier_days()
    {
        // The AI Trader's book after three weeks of −₹2,500 days, and −₹1,000 so far today.
        using var desk = new Desk();
        long book = desk.Run(9101, "Manual");
        for (int d = 1; d <= 21; d++)
        {
            var day = Friday(10, 0).AddDays(-d);
            desk.Position(book, Call, opened: day, closed: day.AddHours(1), realized: -2_500m);
        }
        desk.Position(book, Put, opened: Today(9, 30), closed: Today(10, 0), realized: -1_000m);

        await desk.Gate().EvaluateOrderAsync(book, Call, "BUY", 1, isClosing: false, CancellationToken.None);

        var figures = await desk.FiguresAsync(book);
        Assert.Equal(-1_000m, figures.Gross);
    }

    [Fact]
    public async Task Todays_loss_counts_even_when_the_books_lifetime_is_positive()
    {
        // Up ₹1.2 lakh before today, down ₹15,000 today: the old rule read +₹1.05 lakh and let it keep buying.
        using var desk = new Desk();
        long book = desk.Run(9102, "Manual");
        desk.Position(book, Call, opened: Friday(10, 0), closed: Friday(11, 0), realized: 120_000m);
        desk.Position(book, Put, opened: Today(9, 30), closed: Today(10, 0), realized: -15_000m);

        var refused = await Assert.ThrowsAsync<RiskViolationException>(() =>
            desk.Gate().EvaluateOrderAsync(book, Call, "BUY", 1, isClosing: false, CancellationToken.None));

        Assert.StartsWith("MAX DAILY LOSS EXCEEDED: today's net P&L (IST day) −₹15,000.00 (after ₹0.00 charges) is below the limit −₹10,000.00.", refused.Message);
        Assert.Contains("exits remain allowed", refused.Message);
    }

    [Fact]
    public async Task Todays_charges_are_taken_off_todays_pnl()
    {
        // Gross −₹9,950 today is inside the limit; the charges of today's fills take it past.
        using var desk = new Desk();
        long run = desk.Run(9103, "Ghost");
        desk.Position(run, Call, opened: Today(9, 30), closed: Today(10, 0), realized: -9_950m);
        desk.Fill(run, Call, "BUY", 200m, 2, Today(9, 30));
        desk.Fill(run, Call, "SELL", 123.85m, 2, Today(10, 0));
        // Friday's fills are not today's charges.
        desk.Fill(run, Put, "BUY", 300m, 10, Friday(10, 0));

        decimal charges = OptionCharges.For(200m * 2 * Lot, 123.85m * 2 * Lot, 2, ChargeSchedule.IndexOptions).Total;
        Assert.True(charges > 50m, "the example needs charges larger than the ₹50 of room left");

        var figures = await desk.FiguresAsync(run);
        Assert.Equal((-9_950m, Math.Round(charges, 2)), (figures.Gross, figures.Charges));
        Assert.Equal(Math.Round(-9_950m - charges, 2), figures.Net);

        var refused = await Assert.ThrowsAsync<RiskViolationException>(() =>
            desk.Gate().EvaluateOrderAsync(run, Call, "BUY", 1, isClosing: false, CancellationToken.None));
        Assert.Contains($"(after ₹{Math.Round(charges, 2).ToString("#,##0.00", CultureInfo.InvariantCulture)} charges)", refused.Message);
    }

    [Fact]
    public async Task A_run_that_is_up_before_charges_but_down_after_them_is_stopped()
    {
        // The Fulcrum shape: high churn, gross slightly positive, the charges drain the account.
        using var desk = new Desk();
        long run = desk.Run(9104, "Churn");
        desk.Position(run, Call, opened: Today(9, 20), closed: Today(14, 0), realized: 500m);
        for (int i = 0; i < 300; i++)
        {
            desk.Fill(run, Call, i % 2 == 0 ? "BUY" : "SELL", 150m, 4, Today(9, 20).AddMinutes(i));
        }

        var figures = await desk.FiguresAsync(run);
        Assert.Equal(500m, figures.Gross);
        Assert.True(figures.Net < Limit, $"net {figures.Net} should be past the limit");

        await Assert.ThrowsAsync<RiskViolationException>(() =>
            desk.Gate().EvaluateOrderAsync(run, Call, "BUY", 1, isClosing: false, CancellationToken.None));
    }

    [Fact]
    public async Task Exits_remain_allowed_past_the_daily_loss()
    {
        using var desk = new Desk();
        long run = desk.Run(9105, "Ghost");
        desk.Position(run, Put, opened: Today(9, 30), closed: Today(10, 0), realized: -20_000m);
        desk.Position(run, Call, opened: Today(10, 15), mark: 80m, average: 100m, quantity: 1);

        var gate = desk.Gate();
        await Assert.ThrowsAsync<RiskViolationException>(() =>
            gate.EvaluateOrderAsync(run, Put, "BUY", 1, isClosing: false, CancellationToken.None));

        // Must not throw: the run is getting flat.
        await gate.EvaluateOrderAsync(run, Call, "SELL", 1, isClosing: true, CancellationToken.None);
    }

    [Fact]
    public async Task An_open_position_from_today_counts_at_its_mark_with_any_partial_close()
    {
        using var desk = new Desk();
        long run = desk.Run(9106, "Ghost");
        // Bought 3 lots at 100 this morning, sold 1 at 60 (−₹2,600), the other 2 marked at 40 (−₹7,800).
        desk.Position(run, Call, opened: Today(9, 30), mark: 40m, average: 100m, quantity: 2, realized: -2_600m);

        var figures = await desk.FiguresAsync(run);

        Assert.Equal(-2_600m + (40m - 100m) * 2 * Lot, figures.Gross);
        await Assert.ThrowsAsync<RiskViolationException>(() =>
            desk.Gate().EvaluateOrderAsync(run, Put, "BUY", 1, isClosing: false, CancellationToken.None));
    }

    // ---------------------------------------------- a position carried in

    [Fact]
    public async Task A_carried_position_counts_only_its_move_since_the_previous_close()
    {
        using var desk = new Desk();
        long book = desk.Run(9107, "Manual");
        // Bought Friday at 300; Friday closed at 150 (−₹9,750 already lost on Friday); marked at 140 now. The old
        // rule read −₹10,400 and refused the buy.
        desk.Position(book, Call, opened: Friday(10, 0), mark: 140m, average: 300m, quantity: 1);
        desk.Quote(Call, close: 150m, at: Today(10, 59));

        var figures = await desk.FiguresAsync(book);

        Assert.Equal((140m - 150m) * Lot, figures.Gross);
        await desk.Gate().EvaluateOrderAsync(book, Put, "BUY", 1, isClosing: false, CancellationToken.None);
    }

    [Fact]
    public async Task A_carried_position_with_no_close_from_today_counts_a_loss_in_full_and_a_gain_not_at_all()
    {
        using var desk = new Desk();
        long losing = desk.Run(9108, "Manual");
        long winning = desk.Run(9109, "Manual");
        desk.Position(losing, Call, opened: Friday(10, 0), mark: 140m, average: 300m, quantity: 1);
        desk.Position(winning, Put, opened: Friday(10, 0), mark: 400m, average: 100m, quantity: 1);
        // Friday's quote: no previous close is known for today.
        desk.Quote(Call, close: 150m, at: Friday(15, 29));

        Assert.Equal((140m - 300m) * Lot, (await desk.FiguresAsync(losing)).Gross);
        Assert.Equal(0m, (await desk.FiguresAsync(winning)).Gross);
    }

    [Fact]
    public async Task A_carried_position_closed_or_reduced_today_counts_its_realized_pnl()
    {
        using var desk = new Desk();
        long book = desk.Run(9110, "Manual");
        // Closed this morning.
        desk.Position(book, Call, opened: Friday(10, 0), closed: Today(9, 20), realized: -4_000m);
        // Two lots carried, one sold this morning for −₹3,000, the other marked flat to Friday's close.
        desk.Position(book, Put, opened: Friday(11, 0), mark: 120m, average: 150m, quantity: 1, realized: -3_000m, group: "G2");
        desk.Fill(book, Put, "SELL", 104m, 1, Today(9, 25), group: "G2");
        desk.Quote(Put, close: 120m, at: Today(10, 59));
        // Closed on Friday: none of it is today's.
        desk.Position(book, Call, opened: Friday(9, 30), closed: Friday(14, 0), realized: -50_000m);

        var figures = await desk.FiguresAsync(book);

        Assert.Equal(-4_000m - 3_000m + 0m, figures.Gross);
    }

    [Fact]
    public async Task A_carried_positions_partial_close_on_an_earlier_day_is_not_todays()
    {
        using var desk = new Desk();
        long book = desk.Run(9111, "Manual");
        desk.Position(book, Put, opened: Friday(11, 0), mark: 125m, average: 150m, quantity: 1, realized: -30_000m, group: "G2");
        desk.Fill(book, Put, "SELL", 104m, 1, Friday(14, 0), group: "G2");
        desk.Quote(Put, close: 120m, at: Today(10, 59));

        var figures = await desk.FiguresAsync(book);

        Assert.Equal((125m - 120m) * Lot, figures.Gross);
    }

    // ---------------------------------------------------------- a recap run

    [Fact]
    public async Task A_recap_run_counts_all_of_itself_whatever_day_its_rows_carry()
    {
        // Played this evening; its rows carry the replayed session's times (11 Sep), not today's.
        using var desk = new Desk();
        long recap = desk.Run(9112, "Ghost", parametersJson: """{"session":"recap","recap_date":"2026-09-11"}""");
        var replayed = IstTime.FromIst(new DateTime(2026, 9, 11, 10, 7, 0));
        desk.Position(recap, Call, opened: replayed, closed: replayed.AddMinutes(30), realized: -12_000m);
        desk.Fill(recap, Call, "BUY", 100m, 1, replayed);

        var figures = await desk.FiguresAsync(recap);

        Assert.True(figures.WholeRun);
        Assert.Equal(-12_000m, figures.Gross);
        Assert.True(figures.Charges > 0m);
        var refused = await Assert.ThrowsAsync<RiskViolationException>(() =>
            desk.Gate().EvaluateOrderAsync(recap, Put, "BUY", 1, isClosing: false, CancellationToken.None));
        Assert.Contains("this recap run's net P&L", refused.Message);
    }

    // ------------------------------------------------------- the pure rule

    [Fact]
    public void The_rule_position_by_position()
    {
        static DailyLoss.Position P(string status, DateTime opened, DateTime? closed, decimal realized, decimal unrealized, decimal? mark = null, int qty = 1, string dir = "LONG")
            => new(1, "G1", Call, dir, status, qty, opened, closed, closed ?? opened, realized, unrealized, mark);

        // Opened today: everything.
        Assert.Equal(-700m, DailyLoss.TodaysPart(P("Open", Today(9, 30), null, -200m, -500m), DayStart, null, Lot, false));
        Assert.Equal(-200m, DailyLoss.TodaysPart(P("Closed", Today(9, 30), Today(10, 0), -200m, 0m), DayStart, null, Lot, false));
        // A strategy leg moved to the manual book keeps what it realized before the move.
        Assert.Equal(-200m, DailyLoss.TodaysPart(P("Carried", Today(9, 30), Today(15, 30), -200m, 0m), DayStart, null, Lot, false));
        // Opened and closed earlier: nothing.
        Assert.Equal(0m, DailyLoss.TodaysPart(P("Closed", Friday(9, 30), Friday(10, 0), -200m, 0m), DayStart, null, Lot, false));
        // Opened earlier, closed today: its realized.
        Assert.Equal(-200m, DailyLoss.TodaysPart(P("Closed", Friday(9, 30), Today(9, 20), -200m, 0m), DayStart, null, Lot, false));
        // Carried and open: the move since the previous close, long and short.
        Assert.Equal(650m, DailyLoss.TodaysPart(P("Open", Friday(9, 30), null, 0m, 1_950m, mark: 130m), DayStart, 120m, Lot, false));
        Assert.Equal(-650m, DailyLoss.TodaysPart(P("Open", Friday(9, 30), null, 0m, -1_950m, mark: 130m, dir: "SHORT"), DayStart, 120m, Lot, false));
        // Carried, no previous close: a loss in full, a gain not at all.
        Assert.Equal(-1_950m, DailyLoss.TodaysPart(P("Open", Friday(9, 30), null, 0m, -1_950m, mark: 70m), DayStart, null, Lot, false));
        Assert.Equal(0m, DailyLoss.TodaysPart(P("Open", Friday(9, 30), null, 0m, 1_950m, mark: 130m), DayStart, null, Lot, false));
        // Carried, no mark to measure from: the same.
        Assert.Equal(0m, DailyLoss.TodaysPart(P("Open", Friday(9, 30), null, 0m, 1_950m), DayStart, 120m, Lot, false));
        // Carried and reduced today: what it realized counts too.
        Assert.Equal(650m - 300m, DailyLoss.TodaysPart(P("Open", Friday(9, 30), null, -300m, 1_950m, mark: 130m), DayStart, 120m, Lot, true));
        Assert.Equal(650m, DailyLoss.TodaysPart(P("Open", Friday(9, 30), null, -300m, 1_950m, mark: 130m), DayStart, 120m, Lot, false));
    }

    // ------------------------------------------------------------ harness

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class Limits : IRiskLimitsStore
    {
        public RiskLimitsDto GetLimits() => new() { MaxOrdersPerMinute = 10_000, MaxDailyLoss = Limit, MaxConcurrentRuns = 10, MaxRunsPerUser = 5 };

        public Task UpdateLimitsAsync(RiskLimitsDto newLimits, string updatedBy, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>One in-memory database, the gate over it, at 11:00 IST on Monday 2 Nov 2026.</summary>
    private sealed class Desk : IDisposable
    {
        private readonly string _name = $"daily-loss-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;
        private readonly TradingDbContext _db;
        private readonly ILotSizeResolver _lots = new PositionGreeksTests.FixedLots(Lot);

        public Desk()
        {
            _provider = new ServiceCollection()
                .AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(_name)
                    .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)))
                .BuildServiceProvider();
            _db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseInMemoryDatabase(_name)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
        }

        public RiskManagementService Gate()
            => new(_db, new Limits(), _provider.GetRequiredService<IServiceScopeFactory>(), _lots, new FixedTime(Now));

        public Task<DailyLoss.Figures> FiguresAsync(long runId)
            => DailyLoss.ReadAsync(_db, _lots, runId, Now, CancellationToken.None);

        public long Run(long id, string strategy, string parametersJson = "{}")
        {
            _db.SimulationRuns.Add(new SimulationRun
            {
                Id = id, UserId = 1, Mode = "LivePaper", Symbol = "NSE:NIFTY50-INDEX", Status = "Running",
                StrategyName = strategy, ParametersJson = parametersJson, CreatedUtc = Friday(9, 0),
            });
            _db.SaveChanges();
            return id;
        }

        /// <summary>A closed position when <paramref name="closed"/> is given, else an open one at its mark.</summary>
        public void Position(long runId, string symbol, DateTime opened, DateTime? closed = null, decimal realized = 0m,
            decimal? mark = null, decimal average = 100m, int quantity = 0, string group = "G1")
        {
            bool open = closed is null;
            _db.PaperPositions.Add(new PaperPosition
            {
                SimulationRunId = runId, StrategyName = "test", GroupId = group, Symbol = symbol, Direction = "LONG",
                Quantity = open ? quantity : 0, AveragePrice = average, LastMarkPrice = mark ?? average,
                RealizedPnl = realized,
                UnrealizedPnl = open && mark is decimal m ? PaperPnl.Unrealized("LONG", average, m, quantity, Lot) : 0m,
                Status = open ? "Open" : "Closed", OpenedUtc = opened, ClosedUtc = closed, UpdatedUtc = closed ?? Now,
            });
            _db.SaveChanges();
        }

        public void Fill(long runId, string symbol, string side, decimal price, int lots, DateTime at, string group = "G1")
        {
            _db.PaperOrders.Add(new PaperOrder
            {
                SimulationRunId = runId, StrategyName = "test", GroupId = group, Symbol = symbol, Side = side,
                Quantity = lots, Status = "Filled", RequestedPrice = price, FillPrice = price, CreatedUtc = at, FilledUtc = at,
            });
            _db.SaveChanges();
        }

        public void Quote(string symbol, decimal close, DateTime at)
        {
            _db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = symbol, LastTradedPrice = close, Close = close, UpdatedUtc = at });
            _db.SaveChanges();
        }

        public void Dispose()
        {
            _db.Dispose();
            _provider.Dispose();
        }
    }
}
