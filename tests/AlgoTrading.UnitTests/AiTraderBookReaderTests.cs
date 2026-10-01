using System.Diagnostics;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI Trader's own account as the rules read it (<see cref="AiTraderBookReader"/>): the desk's kill switch, and
/// in live mode its manual book's day.
/// </summary>
public sealed class AiTraderBookReaderTests : IDisposable
{
    // Monday 5 Oct 2026, 11:00 IST.
    private static readonly DateTime Eleven = IstTime.FromIst(new DateTime(2026, 10, 5, 11, 0, 0));

    [Fact]
    public async Task The_live_desks_kill_switch_is_not_carried_into_a_replays_book_but_is_into_the_live_and_shadow_ones()
    {
        var db = NewDb();
        var reader = Reader(db, killSwitch: true);
        var replayed = IstTime.FromIst(new DateTime(2026, 9, 30, 11, 0, 0));

        var replay = await reader.ReadAsync(replayed, replay: true, default);
        var live = await reader.ReadAsync(Eleven, replay: false, default);

        Assert.False(replay.KillSwitch);
        Assert.True(live.KillSwitch);
        // A replay places nothing and is judged on its own fresh book: the live desk's halt refuses none of its buys.
        var plan = new AiTraderPlan(AiTraderPlan.Buy, "NIFTY", "CE", "ATM", 1, 90m, 160m, null, null, null, "Trend.", 0.5);
        var contract = new AiTraderContract("NSE:NIFTY26O0622650CE", "NIFTY", "CE", 22650m, new DateOnly(2026, 10, 6), 120m, 65);
        Assert.Equal("ok", AiTraderGuard.Check(plan, replay, AiTraderAgent.Rules, contract).Rule);
        Assert.Equal("kill-switch", AiTraderGuard.Check(plan, live, AiTraderAgent.Rules, contract).Rule);
    }

    [Fact]
    public async Task Net_today_counts_only_todays_part_of_its_live_book()
    {
        var db = NewDb();
        long book = await Book(db);
        var friday = IstTime.FromIst(new DateTime(2026, 10, 2, 14, 0, 0));
        db.PaperPositions.AddRange(
            // Carried from Friday: bought at 100, marked at 130 now, and Friday's close was 120. Today it made (130 − 120) × 65.
            Held(book, Carried, friday, 100m, 1, mark: 130m),
            // Bought and sold today: −₹650.
            Held(book, Call, At(9, 30), 80m, 1, mark: 70m, closedUtc: At(10, 0)),
            // Bought today and open: +₹260 at its mark.
            Held(book, Put, At(10, 30), 50m, 1, mark: 54m),
            // Bought Thursday, sold Friday: none of it is today's.
            Held(book, Call, friday.AddDays(-1), 60m, 1, mark: 137m, closedUtc: friday));
        db.PaperOrders.AddRange(
            Fill(book, Carried, "BUY", 100m, friday), Fill(book, Call, "BUY", 60m, friday.AddDays(-1)), Fill(book, Call, "SELL", 137m, friday),
            Fill(book, Call, "BUY", 80m, At(9, 30)), Fill(book, Call, "SELL", 70m, At(10, 0)), Fill(book, Put, "BUY", 50m, At(10, 30)));
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = Carried, LastTradedPrice = 130m, Close = 120m, UpdatedUtc = At(10, 59) });
        await db.SaveChangesAsync();

        var read = await Reader(db).ReadAsync(Eleven, replay: false, default);

        // Today's fills only: two buys and a sell.
        decimal charges = OptionCharges.For((80m + 50m) * 65, 70m * 65, 3, ChargeSchedule.IndexOptions).Total;
        Assert.Equal(Math.Round(10m * 65 - 650m + 260m - charges, 2), read.NetToday);
        Assert.Equal(2, read.OpenedToday);
        Assert.Equal(new[] { Put, Carried }, read.Open.Select(p => p.Symbol).Order());
        // The model still reads each open position's whole P&L since its entry.
        Assert.Equal(1950m, read.Open.Single(p => p.Symbol == Carried).UnrealizedPnl);
    }

    [Fact]
    public async Task A_carried_position_with_no_close_from_today_counts_a_loss_in_full_and_a_gain_not_at_all()
    {
        // No quote for it today, so no previous close: the daily-loss rule is never loosened by a gain it cannot date.
        var db = NewDb();
        long book = await Book(db);
        var friday = IstTime.FromIst(new DateTime(2026, 10, 2, 14, 0, 0));
        db.PaperPositions.AddRange(Held(book, Carried, friday, 100m, 1, mark: 130m), Held(book, Put, friday, 50m, 1, mark: 40m));
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = Carried, LastTradedPrice = 130m, Close = 125m, UpdatedUtc = friday.AddHours(1) });
        await db.SaveChangesAsync();

        var read = await Reader(db).ReadAsync(Eleven, replay: false, default);

        Assert.Equal(-650m, read.NetToday);
    }

    [Fact]
    public async Task Only_runs_started_today_count_in_net_today_and_every_running_run_is_listed()
    {
        var db = NewDb();
        var user = await AiTraderAccount.EnsureAsync(db, default);
        var registry = Registry();
        Register(registry, 41, user.Id, startedUtc: IstTime.FromIst(new DateTime(2026, 10, 2, 9, 20, 0)));
        Register(registry, 42, user.Id, startedUtc: At(9, 20));
        db.PaperPositions.AddRange(
            new PaperPosition { SimulationRunId = 41, Symbol = Call, Direction = "LONG", Status = "Closed", RealizedPnl = 3000m, OpenedUtc = At(9, 25), ClosedUtc = At(9, 50) },
            new PaperPosition { SimulationRunId = 42, Symbol = Put, Direction = "LONG", Status = "Closed", RealizedPnl = -400m, OpenedUtc = At(9, 25), ClosedUtc = At(9, 50) });
        await db.SaveChangesAsync();

        var read = await Reader(db, registry: registry).ReadAsync(Eleven, replay: false, default);

        // RunPnl's figures are a run's whole life: a run started on Friday and still running is left out of today's.
        Assert.Equal(-400m, read.NetToday);
        Assert.Equal(new[] { (41L, 3000m), (42L, -400m) }, read.Runs.Select(r => (r.RunId, r.NetPnl)).OrderBy(r => r.Item1));
    }

    // ---------- helpers ----------

    private const string Carried = "NSE:NIFTY26O0622650CE";
    private const string Call = "NSE:NIFTY26O0622700CE";
    private const string Put = "NSE:NIFTY26O0622600PE";

    private static DateTime At(int hour, int minute) => IstTime.FromIst(new DateTime(2026, 10, 5, hour, minute, 0));

    private readonly List<Process> _processes = [];

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            p.Dispose();
        }
    }

    /// <summary>The ai-trader account and its manual book; the book's run id.</summary>
    internal static async Task<long> Book(TradingDbContext db)
    {
        var user = await AiTraderAccount.EnsureAsync(db, default);
        var book = ManualBook.NewBook(user.Id, IstTime.FromIst(new DateTime(2026, 9, 28, 9, 0, 0)));
        db.SimulationRuns.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    /// <summary>A long position in its book, one lot of 65 per lot; closed at its mark when <paramref name="closedUtc"/> is given.</summary>
    internal static PaperPosition Held(long book, string symbol, DateTime openedUtc, decimal entry, int lots, decimal mark, DateTime? closedUtc = null) => new()
    {
        SimulationRunId = book, StrategyName = "Manual", Symbol = symbol, Direction = "LONG", Quantity = closedUtc is null ? lots : 0, AveragePrice = entry,
        LastMarkPrice = mark, UnrealizedPnl = closedUtc is null ? (mark - entry) * lots * 65 : 0m, RealizedPnl = closedUtc is null ? 0m : (mark - entry) * lots * 65,
        Status = closedUtc is null ? "Open" : "Closed", OpenedUtc = openedUtc, ClosedUtc = closedUtc, UpdatedUtc = closedUtc ?? openedUtc,
        StopLossPrice = entry * 0.75m, TargetPrice = entry * 1.4m,
    };

    internal static PaperOrder Fill(long book, string symbol, string side, decimal price, DateTime atUtc, int lots = 1) => new()
    {
        SimulationRunId = book, StrategyName = "Manual", Symbol = symbol, Side = side, Quantity = lots, Status = "Filled", FillPrice = price,
        FilledUtc = atUtc, CreatedUtc = atUtc,
    };

    private static StrategyProcessRegistry Registry() =>
        new(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<StrategyProcessRegistry>.Instance);

    /// <summary>A registry entry around a real process that stays alive, as the registry requires.</summary>
    private void Register(StrategyProcessRegistry registry, long runId, long userId, DateTime startedUtc)
    {
        var info = TestSleeper.StartInfo();
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        var process = Process.Start(info)!;
        _processes.Add(process);
        Assert.True(registry.TryAdd(new RunningStrategy(StrategyCatalogService.StableId("ChainFlowBuy"), "ChainFlowBuy", process, "ai-trader", userId,
            startedUtc, runId, "NIFTY", "NSE:NIFTY50-INDEX", 1, new RiskRulesDto())));
    }

    internal static AiTraderBookReader Reader(TradingDbContext db, bool killSwitch = false, StrategyProcessRegistry? registry = null)
    {
        var lots = new MarketBriefBuilderTests.Lots(65);
        var charges = new RunCharges(db, lots);
        registry ??= Registry();
        return new AiTraderBookReader(db, lots, charges, new RunPnl(db, lots, charges), registry, new KillSwitch(killSwitch),
            new MarketSessionService(new OpenCalendar()));
    }

    /// <summary>The desk's kill switch, on or off; nothing else of the risk service is read.</summary>
    private sealed class KillSwitch(bool on) : IRiskManagementService
    {
        public Task<bool> IsKillSwitchActiveAsync(CancellationToken cancellationToken) => Task.FromResult(on);

        public Task<KillSwitchState> GetKillSwitchStateAsync(CancellationToken cancellationToken) => Task.FromResult(new KillSwitchState { IsActive = on });

        public Task EvaluateOrderAsync(long simulationRunId, string symbol, string side, int quantity, bool isClosing, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ActivateKillSwitchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeactivateKillSwitchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ActivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeactivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();
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
