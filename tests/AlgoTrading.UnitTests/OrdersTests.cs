using System.Reflection;
using System.Security.Claims;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Exceptions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/Orders: one IST day of orders across runs and manual books, the
/// ones the risk gate refused among them, and only the accounts the caller
/// may see.
/// </summary>
/// <remarks>
/// Trade → Orders used to ask each of the day's runs for its own ledger and
/// found manual books through their open legs, so another account's book
/// with nothing open was never read, and a refused order was nowhere.
/// </remarks>
public class OrdersTests
{
    private const long Admin = 1;
    private const long Trader = 7;
    private const long OtherTrader = 8;
    private const int LotSize = 75;

    private const string NiftyCall = "NSE:NIFTY2692924500CE";
    private const string NiftyPut = "NSE:NIFTY2692924500PE";
    private const string Reliance = "NSE:RELIANCE-EQ";

    private static DateTime Ist(int d, int h, int m, int s = 0) => IstTime.FromIst(new DateTime(2026, 9, d, h, m, s));

    [Fact]
    public void The_endpoint_needs_the_strategies_grant()
    {
        var attribute = typeof(OrdersController).GetCustomAttribute<RequireModuleAttribute>();
        Assert.NotNull(attribute);
        var key = typeof(RequireModuleAttribute).GetField("_moduleKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(attribute);
        Assert.Equal(PlatformModules.Strategies, key);
    }

    // ============================================================ scope

    [Fact]
    public async Task A_trader_gets_their_own_orders_whatever_userId_or_runId_they_pass()
    {
        using var desk = new Blotter();
        var s = desk.SeedDay();

        var own = await desk.Ok(Trader, admin: false, userId: OtherTrader);
        var theirs = await desk.Ok(Trader, admin: false, runId: s.OtherRun);

        Assert.Equal(new[] { s.BookOrder, s.GhostExit, s.GhostEntry }, own.Orders.Where(o => o.Kind == "order").Select(o => o.Id));
        Assert.All(own.Orders, o => Assert.Equal(Trader, o.UserId));
        Assert.Equal(new[] { s.GhostRun, s.Book }, own.Runs.Select(r => r.RunId).Order());
        Assert.Empty(theirs.Orders);
        Assert.Equal(0, theirs.Total);
    }

    [Fact]
    public async Task An_admin_gets_every_account_or_the_one_asked_for_and_can_still_pick_another()
    {
        using var desk = new Blotter();
        var s = desk.SeedDay();

        var all = await desk.Ok(Admin, admin: true);
        var one = await desk.Ok(Admin, admin: true, userId: OtherTrader);

        Assert.Equal(new[] { Trader, OtherTrader }.Order(), all.Orders.Select(o => o.UserId).Distinct().Order());
        Assert.Equal(new[] { s.OtherOrder }, one.Orders.Select(o => o.Id));
        Assert.Equal(1, one.Total);
        // The account is a filter for an admin: the counts still offer every account's runs.
        Assert.Equal(new[] { "rahul", "trader", "trader" }, one.Runs.Select(r => r.UserName));
        Assert.Equal(all.Runs.Select(r => r.RunId), one.Runs.Select(r => r.RunId));
    }

    // ============================================================ the day

    [Fact]
    public async Task The_day_is_the_IST_calendar_day_and_a_book_opened_last_week_shows_only_todays_orders()
    {
        using var desk = new Blotter();
        long book = desk.Book(Trader, Ist(21, 10, 0));
        desk.Order(book, Reliance, "BUY", 10, Ist(21, 10, 5));                  // last week
        long lastEvening = desk.Order(book, Reliance, "SELL", 10, Ist(27, 23, 59, 59));
        long firstInstant = desk.Order(book, Reliance, "BUY", 5, Ist(28, 0, 0));
        long lastInstant = desk.Order(book, Reliance, "SELL", 5, Ist(28, 23, 59, 59));
        long nextDay = desk.Order(book, Reliance, "BUY", 1, Ist(29, 0, 0));

        var day = await desk.Ok(Trader, admin: false);
        var dayBefore = await desk.Ok(Trader, admin: false, date: "2026-09-27");

        Assert.Equal(new[] { lastInstant, firstInstant }, day.Orders.Select(o => o.Id));
        Assert.Equal(desk.DayStart, day.DayStartUtc);
        Assert.Equal("2026-09-28", day.Date);
        Assert.Contains(lastEvening, dayBefore.Orders.Select(o => o.Id));
        Assert.DoesNotContain(nextDay, day.Orders.Select(o => o.Id));
    }

    [Fact]
    public async Task A_date_that_is_not_yyyy_MM_dd_is_refused()
    {
        using var desk = new Blotter();

        Assert.IsType<BadRequestObjectResult>((await desk.Get(Trader, admin: false, date: "28-09-2026")).Result);
    }

    // ============================================================ paging

    [Fact]
    public async Task Pages_run_newest_first_across_orders_and_refusals_without_a_row_twice()
    {
        using var desk = new Blotter();
        long run = desk.Run("Ghost", Trader);
        var rows = new List<(string Kind, long Id, DateTime At)>();
        for (int i = 0; i < 5; i++) rows.Add(("order", desk.Order(run, NiftyCall, "BUY", 1, Ist(28, 9, 20 + i)), Ist(28, 9, 20 + i)));
        rows.Add(("rejection", desk.Rejection(run, NiftyPut, Ist(28, 9, 22, 30), "RATE LIMIT EXCEEDED"), Ist(28, 9, 22, 30)));
        var newestFirst = rows.OrderByDescending(x => x.At).Select(x => (x.Kind, x.Id)).ToList();

        var pages = new List<OrderRowResponse>();
        for (int skip = 0; skip < 6; skip += 4)
        {
            var page = await desk.Ok(Trader, admin: false, skip: skip, take: 4);
            Assert.Equal(6, page.Total);
            pages.AddRange(page.Orders);
        }

        Assert.Equal(newestFirst, pages.Select(o => (o.Kind, o.Id)));
        Assert.True(pages.Zip(pages.Skip(1)).All(p => p.First.AtUtc >= p.Second.AtUtc));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 501)]
    [InlineData(-1, 50)]
    public async Task A_page_outside_the_limits_is_refused(int skip, int take)
    {
        using var desk = new Blotter();

        Assert.IsType<BadRequestObjectResult>((await desk.Get(Trader, admin: false, skip: skip, take: take)).Result);
    }

    // ============================================================ mode

    [Fact]
    public async Task Backtest_orders_stay_out_of_the_live_blotter_and_read_at_the_runs_frozen_lot_size()
    {
        using var desk = new Blotter();
        long live = desk.Run("Ghost", Trader);
        long backtest = desk.Run("Ghost", Trader, mode: "OfflineReplay", parameters: "{\"underlying\":\"NIFTY\",\"lot_size\":65}");
        long liveOrder = desk.Order(live, NiftyCall, "BUY", 2, Ist(28, 9, 20));
        long barOrder = desk.Order(backtest, NiftyCall, "BUY", 2, Ist(28, 9, 20));

        var livePaper = await desk.Ok(Trader, admin: false);
        var replays = await desk.Ok(Trader, admin: false, mode: "offlinereplay");

        Assert.Equal(new[] { liveOrder }, livePaper.Orders.Select(o => o.Id));
        Assert.Equal("LivePaper", livePaper.Mode);
        var bar = Assert.Single(replays.Orders);
        Assert.Equal(barOrder, bar.Id);
        Assert.Equal("OfflineReplay", replays.Mode);
        Assert.Equal(((int?)2, (int?)65, (int?)130), (bar.Lots, bar.LotSize, bar.Quantity));
        Assert.IsType<BadRequestObjectResult>((await desk.Get(Trader, admin: false, mode: "Backtest")).Result);
    }

    // ============================================================ rows

    [Fact]
    public async Task An_order_reads_in_lots_and_units_with_how_its_fill_was_priced()
    {
        using var desk = new Blotter();
        var s = desk.SeedDay();

        var day = await desk.Ok(Trader, admin: false);
        // An order and a refusal can share an id (two tables): a row is its kind and its id.
        OrderRowResponse Order(long id) => day.Orders.Single(o => o.Kind == "order" && o.Id == id);
        var entry = Order(s.GhostEntry);
        var book = Order(s.BookOrder);

        Assert.Equal(("order", s.GhostRun, "Ghost", "NIFTY", false), (entry.Kind, entry.RunId, entry.StrategyName, entry.Underlying, entry.IsManualBook));
        Assert.Equal(("SELL", NiftyCall, "trader"), (entry.Side, entry.Symbol, entry.UserName));
        Assert.Equal(((int?)2, (int?)LotSize, (int?)150), (entry.Lots, entry.LotSize, entry.Quantity));
        Assert.Equal((101.5m, 101.2m, "Filled"), (entry.RequestedPrice!.Value, entry.FillPrice!.Value, entry.Status));
        Assert.Equal(("bid", "filled at the bid", (double?)1.5, false), (entry.PriceRule, entry.PriceNote, entry.QuoteAgeSeconds, entry.StaleQuote));
        Assert.Equal("runner-uuid-1", entry.ClientSignalId);
        Assert.Null(entry.Reason);

        // An order booked before fills recorded their pricing says nothing rather than guessing.
        var exit = Order(s.GhostExit);
        Assert.Null(exit.PriceRule);
        Assert.False(exit.StaleQuote);

        Assert.True(book.IsManualBook);
        Assert.Equal(ManualOrdersController.BookStrategyName, book.StrategyName);
        Assert.Null(book.Underlying);
    }

    [Fact]
    public async Task An_order_the_risk_gate_refuses_appears_with_its_side_size_and_reason()
    {
        using var desk = new Blotter();
        long run = desk.Run("Ghost", Trader);
        desk.Loss(run, -50_000m);   // far past the -10,000 daily loss

        await Assert.ThrowsAsync<RiskViolationException>(() => desk.InScope(sp =>
            sp.GetRequiredService<RiskManagementService>().EvaluateOrderAsync(run, NiftyPut, "SELL", 3, isClosing: false, CancellationToken.None)));

        var day = await desk.Ok(Trader, admin: false, date: IstTime.DateString(DateTime.UtcNow));
        var refused = Assert.Single(day.Orders);

        Assert.Equal(("rejection", "Rejected", run, NiftyPut), (refused.Kind, refused.Status, refused.RunId, refused.Symbol));
        Assert.Equal(("SELL", 3, LotSize, 3 * LotSize), (refused.Side, refused.Lots!.Value, refused.LotSize!.Value, refused.Quantity!.Value));
        Assert.StartsWith("MAX DAILY LOSS EXCEEDED", refused.Reason);
        Assert.Null(refused.FillPrice);
        Assert.Equal(1, Assert.Single(day.Runs).Rejected);
        Assert.Contains(day.Statuses, x => x.Status == "Rejected" && x.Orders == 1);
    }

    [Fact]
    public async Task A_refusal_recorded_before_its_size_was_still_shows_its_reason()
    {
        using var desk = new Blotter();
        long run = desk.Run("Ghost", Trader);
        desk.Rejection(run, NiftyCall, Ist(28, 10, 0), "GLOBAL KILL SWITCH IS ACTIVE.", details: null);

        var refused = Assert.Single((await desk.Ok(Trader, admin: false)).Orders);

        Assert.Equal("GLOBAL KILL SWITCH IS ACTIVE.", refused.Reason);
        Assert.Null(refused.Side);
        Assert.Null(refused.Lots);
        Assert.Null(refused.Quantity);
    }

    // ============================================================ filters

    [Fact]
    public async Task The_status_run_and_symbol_filters_narrow_the_rows_but_not_what_the_filters_offer()
    {
        using var desk = new Blotter();
        var s = desk.SeedDay();

        var rejected = await desk.Ok(Trader, admin: false, status: "rejected");
        var filled = await desk.Ok(Trader, admin: false, status: "Filled");
        var ghost = await desk.Ok(Trader, admin: false, runId: s.GhostRun);
        var reliance = await desk.Ok(Trader, admin: false, symbol: Reliance);

        Assert.Equal(new[] { s.GhostRefusal }, rejected.Orders.Select(o => o.Id));
        Assert.All(filled.Orders, o => Assert.Equal("order", o.Kind));
        Assert.Equal(3, filled.Total);
        Assert.All(ghost.Orders, o => Assert.Equal(s.GhostRun, o.RunId));
        Assert.Equal(3, ghost.Total);   // two orders and the refusal
        Assert.Equal(new[] { s.BookOrder }, reliance.Orders.Select(o => o.Id));

        foreach (var answer in new[] { rejected, filled, ghost, reliance })
        {
            Assert.Equal(new[] { ("Filled", 3), ("Rejected", 1) }, answer.Statuses.Select(x => (x.Status, x.Orders)));
            Assert.Equal(new[] { (s.GhostRun, 3, 2, 1), (s.Book, 1, 1, 0) }, answer.Runs.Select(r => (r.RunId, r.Orders, r.Filled, r.Rejected)).OrderBy(x => x.RunId == s.Book));
        }
        Assert.IsType<BadRequestObjectResult>((await desk.Get(Trader, admin: false, status: "Booked")).Result);
    }

    [Fact]
    public void The_shared_scope_rule_gives_an_admin_what_they_ask_and_a_trader_their_own()
    {
        static ClaimsPrincipal Caller(long id, bool admin)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, id.ToString()) };
            if (admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        Assert.Equal(OtherTrader, Caller(Admin, admin: true).ScopeUserId(OtherTrader));
        Assert.Null(Caller(Admin, admin: true).ScopeUserId(null));
        Assert.Equal(Trader, Caller(Trader, admin: false).ScopeUserId(OtherTrader));
        Assert.Equal(Trader, Caller(Trader, admin: false).ScopeUserId(null));
    }

    // ======================================================== the harness

    /// <summary>The orders builder and the risk gate over one in-memory database.</summary>
    private sealed class Blotter : IDisposable
    {
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"orders-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public DateTime DayStart { get; } = IstTime.StartOfDayUtc(new DateOnly(2026, 9, 28));

        public Blotter()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<TradingDbContext>(o => o
                .UseInMemoryDatabase(_name, _root)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
            services.AddSingleton<ILotSizeResolver>(new PositionGreeksTests.FixedLots(LotSize));
            services.AddSingleton<StrategyProcessRegistry>();
            services.AddSingleton<IRiskLimitsStore>(new Limits());
            services.AddScoped<RiskManagementService>();
            services.AddScoped<OrdersBuilder>();
            _provider = services.BuildServiceProvider();

            Write(db => db.AppUsers.AddRange(
                new AppUser { Id = Admin, UserName = "admin", Role = UserRoles.Admin },
                new AppUser { Id = Trader, UserName = "trader", Role = UserRoles.Trader },
                new AppUser { Id = OtherTrader, UserName = "rahul", Role = UserRoles.Trader }));
        }

        public sealed record Seeded(long GhostRun, long Book, long OtherRun, long GhostEntry, long GhostExit, long GhostRefusal, long BookOrder, long OtherOrder);

        /// <summary>
        /// The trader's Ghost run on NIFTY (an entry priced at the bid, an exit
        /// from before fills recorded their pricing, a refusal) and their manual
        /// book (a share); another trader's run with one order.
        /// </summary>
        public Seeded SeedDay()
        {
            long ghost = Run("Ghost", Trader);
            long book = Book(Trader, Ist(21, 9, 30));
            long other = Run("Ghost", OtherTrader);

            long signal = Write(db =>
            {
                var s = new SimulationSignal { SimulationRunId = ghost, StrategyName = "Ghost", SignalType = "OPEN_GROUP", GroupId = "G1", ClientSignalId = "runner-uuid-1", TimestampUtc = Ist(28, 9, 20) };
                db.SimulationSignals.Add(s);
                return s;
            }).Id;
            long entry = Order(ghost, NiftyCall, "SELL", 2, Ist(28, 9, 20), requested: 101.5m, fill: 101.2m, signalId: signal,
                metadata: "{\"rule\":\"bid\",\"note\":\"filled at the bid\",\"quoteAgeSeconds\":1.5}");
            long refusal = Rejection(ghost, NiftyPut, Ist(28, 9, 25), "RATE LIMIT EXCEEDED: More than 20 orders", RejectionDetails.ToJson("SELL", 2));
            long exit = Order(ghost, NiftyCall, "BUY", 2, Ist(28, 11, 0), fill: 90m);
            long share = Order(book, Reliance, "BUY", 10, Ist(28, 12, 0), fill: 2_900m);
            long theirs = Order(other, NiftyCall, "BUY", 1, Ist(28, 9, 40), fill: 99m);
            return new Seeded(ghost, book, other, entry, exit, refusal, share, theirs);
        }

        public long Run(string strategy, long userId, string mode = "LivePaper", string parameters = "{\"underlying\":\"NIFTY\"}")
            => Write(db =>
            {
                var run = new SimulationRun
                {
                    UserId = userId, Mode = mode, Status = "Running", Symbol = "NSE:NIFTY50-INDEX", StrategyName = strategy,
                    ParametersJson = parameters, LastError = string.Empty, Resolution = "1m", ReplaySpeed = string.Empty,
                    StartedUtc = Ist(28, 9, 15), CreatedUtc = Ist(28, 9, 15)
                };
                db.SimulationRuns.Add(run);
                return run;
            }).Id;

        public long Book(long userId, DateTime openedUtc) => Write(db =>
        {
            var book = ManualBook.NewBook(userId, openedUtc);
            db.SimulationRuns.Add(book);
            return book;
        }).Id;

        public long Order(long runId, string symbol, string side, int lots, DateTime atUtc,
            decimal? requested = null, decimal fill = 100m, long? signalId = null, string? metadata = null)
            => Write(db =>
            {
                var order = new PaperOrder
                {
                    SimulationRunId = runId, SimulationSignalId = signalId, StrategyName = "Ghost", GroupId = "G1", Symbol = symbol,
                    Side = side, Quantity = lots, OrderType = "MARKET_SIM", Status = "Filled", RequestedPrice = requested ?? fill,
                    FillPrice = fill, MetadataJson = metadata, CreatedUtc = atUtc, FilledUtc = atUtc
                };
                db.PaperOrders.Add(order);
                return order;
            }).Id;

        public long Rejection(long runId, string symbol, DateTime atUtc, string reason, string? details = "{\"side\":\"BUY\",\"lots\":1}")
            => Write(db =>
            {
                var e = new RiskEvent
                {
                    OccurredUtc = atUtc, Kind = RiskManagementService.OrderRejectedKind, Reason = reason,
                    SimulationRunId = runId, Symbol = symbol, DetailsJson = details
                };
                db.RiskEvents.Add(e);
                return e;
            }).Id;

        public void Loss(long runId, decimal realized) => Write(db =>
        {
            var p = new PaperPosition
            {
                SimulationRunId = runId, Symbol = NiftyCall, Direction = "SHORT", Quantity = 0, AveragePrice = 100m,
                Status = "Closed", RealizedPnl = realized, OpenedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow
            };
            db.PaperPositions.Add(p);
            return p;
        });

        public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = _provider.CreateScope();
            return await work(scope.ServiceProvider);
        }

        public async Task InScope(Func<IServiceProvider, Task> work)
        {
            using var scope = _provider.CreateScope();
            await work(scope.ServiceProvider);
        }

        public Task<ActionResult<OrdersResponse>> Get(long callerId, bool admin, string? date = "2026-09-28", long? userId = null,
            long? runId = null, string? symbol = null, string? status = null, string? mode = null, int skip = 0, int take = OrdersFilter.DefaultTake)
            => InScope(async sp =>
            {
                var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, callerId.ToString()) };
                if (admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
                var controller = new OrdersController(sp.GetRequiredService<OrdersBuilder>())
                {
                    ControllerContext = new ControllerContext
                    {
                        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
                    }
                };
                return await controller.GetOrders(date, runId, symbol, status, mode, userId, skip, take, CancellationToken.None);
            });

        public async Task<OrdersResponse> Ok(long callerId, bool admin, string? date = "2026-09-28", long? userId = null,
            long? runId = null, string? symbol = null, string? status = null, string? mode = null, int skip = 0, int take = OrdersFilter.DefaultTake)
        {
            var result = await Get(callerId, admin, date, userId, runId, symbol, status, mode, skip, take);
            return Assert.IsType<OrdersResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }

        private T Write<T>(Func<TradingDbContext, T> add)
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var row = add(db);
            db.SaveChanges();
            return row;
        }

        private void Write(Action<TradingDbContext> add) => Write(db => { add(db); return 0; });

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>Limits wide enough that only the daily loss can refuse: the rate window is shared across tests by run id.</summary>
    private sealed class Limits : IRiskLimitsStore
    {
        public RiskLimitsDto GetLimits() => new() { MaxOrdersPerMinute = 10_000, MaxDailyLoss = -10_000m, MaxConcurrentRuns = 10, MaxRunsPerUser = 5 };

        public Task UpdateLimitsAsync(RiskLimitsDto newLimits, string updatedBy, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
