using System.Security.Claims;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The minute-by-minute P&amp;L of live runs: what the recorder writes, that a
/// minute is never written twice, that a run's last row lands where it ended,
/// that the series answers only for the runs the caller may see, and above
/// all that a minute's net is the run card's net at that minute.
/// </summary>
public class RunPnlSeriesTests
{
    private const string Call = "NSE:NIFTY2692924500CE";
    private const string Put = "NSE:NIFTY2692924500PE";
    private const int LotSize = 75;

    private static DateTime Ist(int d, int h, int m, int s = 0) => IstTime.FromIst(new DateTime(2026, 9, d, h, m, s));

    // ======================================================== the recorder

    [Fact]
    public async Task A_pass_writes_one_row_per_live_run_at_its_minute_and_nothing_for_the_rest()
    {
        using var desk = new PnlDesk();
        long live = desk.NewRun("Ghost", PnlDesk.Trader);
        long stopping = desk.NewRun("Ghost", PnlDesk.Trader, status: "Stopping");
        long stoppedYesterday = desk.NewRun("Ghost", PnlDesk.Trader, status: "Stopped", completedUtc: Ist(27, 15, 30));
        long backtest = desk.NewRun("Ghost", PnlDesk.Trader, mode: "Backtest");
        desk.Fill(live, Call, "SELL", 2, 100m, Ist(28, 9, 20));

        var pass = await desk.Record(Ist(28, 10, 15, 50));

        Assert.Equal(2, pass.Live);
        var rows = desk.Minutes();
        Assert.Equal(new[] { live, stopping }, rows.Select(r => r.SimulationRunId).Order());
        Assert.All(rows, r => Assert.Equal(Ist(28, 10, 15), r.AtUtc));
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.AtUtc.Kind));
        Assert.DoesNotContain(rows, r => r.SimulationRunId == stoppedYesterday || r.SimulationRunId == backtest);
    }

    [Fact]
    public async Task A_minutes_net_is_the_run_cards_net()
    {
        using var desk = new PnlDesk();
        long run = desk.NewRun("Ghost", PnlDesk.Trader);
        // One round trip booked +1,500, one written leg still open.
        desk.Fill(run, Call, "BUY", 2, 100m, Ist(28, 9, 20));
        desk.Fill(run, Call, "SELL", 2, 110m, Ist(28, 9, 40), closes: true, realized: 1_500m);
        desk.Fill(run, Put, "SELL", 2, 100m, Ist(28, 9, 45));
        desk.Quote(Put, 90.35m);

        await desk.Record(Ist(28, 10, 15, 50));
        var view = await desk.RunCard(run);
        var row = Assert.Single(desk.Minutes());

        Assert.Equal(Math.Round(view.Pnl.Realized, 2), row.Realized);
        Assert.Equal(Math.Round(view.Pnl.Unrealized, 2), row.Unrealized);
        Assert.Equal(Math.Round(view.Pnl.Charges, 2), row.Charges);
        Assert.Equal(Math.Round(view.Pnl.Net, 2), row.Net);
        // (100 − 90.35) × 2 lots × 75 on the open leg, and charges really taken off.
        Assert.Equal(1_447.50m, row.Unrealized);
        Assert.True(row.Charges > 0m);
        Assert.Equal(row.Realized + row.Unrealized - row.Charges, row.Net);
    }

    [Fact]
    public async Task A_manual_books_minute_is_the_net_its_history_row_shows()
    {
        // The book is the one run the history marks without a runner behind
        // it; until 28 Sep it read the book at its stored mark while the
        // book's own page marked it at the quote.
        using var desk = new PnlDesk();
        long book = desk.NewBook(PnlDesk.Trader, Ist(21, 9, 0));
        desk.Fill(book, Call, "BUY", 3, 80m, Ist(28, 9, 30));
        desk.Quote(Call, 95m);

        await desk.Record(Ist(28, 10, 15, 50));
        var history = Assert.Single(await desk.History(), r => r.RunId == book);
        var row = Assert.Single(desk.Minutes());

        Assert.True(history.IsActive);
        Assert.Equal(15m * 3 * LotSize, history.UnrealizedPnl);
        Assert.Equal(Math.Round(history.NetPnl + history.UnrealizedPnl, 2), row.Net);
        Assert.Equal(Math.Round((await desk.RunCard(book)).Pnl.Net, 2), row.Net);
    }

    [Fact]
    public async Task The_same_minute_written_twice_is_one_row_with_the_later_figures()
    {
        using var desk = new PnlDesk();
        long run = desk.NewRun("Ghost", PnlDesk.Trader);
        desk.Fill(run, Put, "SELL", 2, 100m, Ist(28, 9, 45));
        desk.Quote(Put, 95m);

        await desk.Record(Ist(28, 10, 15, 5));
        desk.Quote(Put, 92m);
        await desk.Record(Ist(28, 10, 15, 50));

        var row = Assert.Single(desk.Minutes());
        Assert.Equal(8m * 2 * LotSize, row.Unrealized);

        await desk.Record(Ist(28, 10, 16, 50));
        Assert.Equal(new[] { Ist(28, 10, 15), Ist(28, 10, 16) }, desk.Minutes().Select(r => r.AtUtc).Order());
    }

    [Fact]
    public void The_table_holds_one_row_per_run_per_minute()
    {
        // The in-memory provider does not enforce it; Postgres does, from this.
        using var desk = new PnlDesk();
        using var db = desk.Db();
        var entity = db.Model.FindEntityType(typeof(RunPnlMinute))!;
        var unique = Assert.Single(entity.GetIndexes(), i => i.IsUnique);

        Assert.Equal(new[] { nameof(RunPnlMinute.SimulationRunId), nameof(RunPnlMinute.AtUtc) }, unique.Properties.Select(p => p.Name));
        Assert.Equal("run_pnl_minutes", entity.GetTableName());
    }

    [Fact]
    public async Task A_run_that_stops_gets_its_final_row_at_the_minute_it_ended()
    {
        using var desk = new PnlDesk();
        long run = desk.NewRun("Ghost", PnlDesk.Trader);
        long leg = desk.Fill(run, Put, "SELL", 2, 100m, Ist(28, 9, 45));
        desk.Quote(Put, 95m);

        // Sampled live at 10:16:10, stopped at 10:16:20: the flatten books
        // the leg at 90, and nothing is open any more.
        await desk.Record(Ist(28, 10, 16, 10));
        Assert.Equal(750m, Assert.Single(desk.Minutes()).Unrealized);
        desk.Flatten(run, leg, exit: 90m, at: Ist(28, 10, 16, 20));

        var pass = await desk.Record(Ist(28, 10, 16, 50));
        Assert.Equal((0, 1), (pass.Live, pass.Ended));
        var final = Assert.Single(desk.Minutes());
        Assert.Equal(Ist(28, 10, 16), final.AtUtc);
        Assert.Equal(0m, final.Unrealized);
        Assert.Equal(1_500m, final.Realized);
        Assert.Equal(Math.Round((await desk.RunCard(run)).Pnl.Net, 2), final.Net);

        // The next passes find nothing new to say about it.
        await desk.Record(Ist(28, 10, 17, 50));
        await desk.Record(Ist(28, 10, 30, 50));
        Assert.Single(desk.Minutes());
    }

    [Fact]
    public async Task A_run_that_ended_while_the_recorder_was_away_still_gets_its_final_row()
    {
        // Died at 11:02 (a restart closed it as an orphan, say), never sampled.
        using var desk = new PnlDesk();
        long run = desk.NewRun("Ghost", PnlDesk.Trader, status: "Stopped", completedUtc: Ist(28, 11, 2, 30));
        desk.Fill(run, Call, "BUY", 1, 100m, Ist(28, 10, 0));
        desk.Fill(run, Call, "SELL", 1, 104m, Ist(28, 11, 2), closes: true, realized: 300m);

        await desk.Record(Ist(28, 11, 5, 50));

        var row = Assert.Single(desk.Minutes());
        Assert.Equal(Ist(28, 11, 2), row.AtUtc);
        Assert.Equal(300m, row.Realized);

        // Past the window it is history, and left alone.
        using (var db = desk.Db())
        {
            db.RunPnlMinutes.RemoveRange(db.RunPnlMinutes);
            db.SaveChanges();
        }
        await desk.Record(Ist(28, 11, 2, 30) + RunPnlRecorder.FinalRowWindow + TimeSpan.FromMinutes(1));
        Assert.Empty(desk.Minutes());
    }

    [Fact]
    public async Task A_manual_book_is_written_when_its_figures_move_and_not_every_minute_of_the_night()
    {
        using var desk = new PnlDesk();
        long book = desk.NewBook(PnlDesk.Trader, Ist(21, 9, 0));
        desk.Fill(book, Call, "BUY", 1, 80m, Ist(28, 9, 30));
        desk.Quote(Call, 95m);

        await desk.Record(Ist(28, 22, 0, 50));
        var quiet = await desk.Record(Ist(28, 22, 1, 50));
        Assert.Equal(1, quiet.Unchanged);
        Assert.Single(desk.Minutes());

        desk.Quote(Call, 96m);
        await desk.Record(Ist(29, 9, 15, 50));
        Assert.Equal(new[] { 15m * LotSize, 16m * LotSize }, desk.Minutes().OrderBy(r => r.AtUtc).Select(r => r.Unrealized));
    }

    [Fact]
    public async Task A_strategy_run_is_written_every_minute_it_is_live_even_when_nothing_moved()
    {
        // So a gap in a run's series means the recorder was away, never "flat".
        using var desk = new PnlDesk();
        desk.NewRun("Ghost", PnlDesk.Trader);

        await desk.Record(Ist(28, 9, 20, 50));
        await desk.Record(Ist(28, 9, 21, 50));

        Assert.Equal(2, desk.Minutes().Count);
    }

    [Fact]
    public void The_pass_runs_fifty_seconds_into_each_minute()
    {
        Assert.Equal(Ist(28, 10, 15, 50), RunPnlRecorderService.NextPassUtc(Ist(28, 10, 15, 3)));
        Assert.Equal(Ist(28, 10, 16, 50), RunPnlRecorderService.NextPassUtc(Ist(28, 10, 15, 50)));
        Assert.Equal(Ist(28, 10, 15), RunPnlRecorder.MinuteOf(Ist(28, 10, 15, 59).AddMilliseconds(999)));
    }

    // ======================================================== the series

    [Fact]
    public async Task A_trader_gets_only_their_own_runs_whatever_userId_they_pass()
    {
        using var desk = new PnlDesk();
        long mine = desk.NewRun("Ghost", PnlDesk.Trader);
        long theirs = desk.NewRun("Ghost", PnlDesk.OtherTrader);
        desk.Point(mine, Ist(28, 9, 20), net: 100m);
        desk.Point(theirs, Ist(28, 9, 20), net: 200m);

        var own = await desk.Series(PnlDesk.Trader, admin: false, userId: PnlDesk.OtherTrader);

        Assert.Equal(new[] { mine }, own.Runs.Select(r => r.RunId));
        Assert.Equal(new[] { PnlDesk.Trader }, own.Accounts.Select(a => a.UserId));
    }

    [Fact]
    public async Task An_admin_gets_every_account_or_the_one_asked_for()
    {
        using var desk = new PnlDesk();
        long mine = desk.NewRun("Ghost", PnlDesk.Trader);
        long theirs = desk.NewRun("Ghost", PnlDesk.OtherTrader);
        desk.Point(mine, Ist(28, 9, 20), net: 100m);
        desk.Point(theirs, Ist(28, 9, 20), net: 200m);

        var all = await desk.Series(PnlDesk.AdminId, admin: true);
        var one = await desk.Series(PnlDesk.AdminId, admin: true, userId: PnlDesk.OtherTrader);

        Assert.Equal(new[] { mine, theirs }, all.Runs.Select(r => r.RunId).Order());
        Assert.Equal(new[] { "rahul", "trader" }, all.Accounts.Select(a => a.UserName));
        Assert.Equal(new[] { theirs }, one.Runs.Select(r => r.RunId));
    }

    [Fact]
    public async Task The_series_is_one_ist_day_in_minutes_from_its_midnight()
    {
        using var desk = new PnlDesk();
        long run = desk.NewRun("Ghost", PnlDesk.Trader);
        desk.Point(run, Ist(27, 23, 59), net: 1m);    // the day before
        desk.Point(run, Ist(28, 9, 15), net: 10m);
        desk.Point(run, Ist(28, 9, 16), net: 12.5m);
        desk.Point(run, Ist(29, 0, 0), net: 99m);     // the day after

        var day = await desk.Series(PnlDesk.Trader, admin: false);

        Assert.Equal("2026-09-28", day.Date);
        Assert.Equal(Ist(28, 0, 0), day.DayStartUtc);
        var series = Assert.Single(day.Runs);
        Assert.Equal(new[] { 555, 556 }, series.Minutes);
        Assert.Equal(new[] { 10m, 12.5m }, series.Net);
        Assert.Equal("NIFTY", series.Underlying);
    }

    [Fact]
    public async Task The_account_totals_are_the_days_trading_runs_and_leave_out_an_older_book()
    {
        using var desk = new PnlDesk();
        long run = desk.NewRun("Ghost", PnlDesk.Trader);
        long alerter = desk.NewRun("LogicEngine", PnlDesk.Trader, parameters: "{\"role\":\"alerts\"}");
        long book = desk.NewBook(PnlDesk.Trader, Ist(21, 9, 0));
        desk.Point(run, Ist(28, 9, 20), net: 100m);
        desk.Point(alerter, Ist(28, 9, 20), net: 0m);
        desk.Point(book, Ist(28, 9, 20), net: 50_000m);   // a week of hand trades

        var day = await desk.Series(PnlDesk.Trader, admin: false);

        Assert.True(day.Runs.Single(r => r.RunId == run).InAccountTotals);
        Assert.False(day.Runs.Single(r => r.RunId == alerter).InAccountTotals);
        var bookSeries = day.Runs.Single(r => r.RunId == book);
        Assert.True(bookSeries.IsManualBook);
        Assert.False(bookSeries.InAccountTotals);

        var account = Assert.Single(day.Accounts);
        Assert.Equal(1, account.Runs);
        Assert.Equal(new[] { 100m }, account.Net);
    }

    [Fact]
    public void An_accounts_minute_holds_each_runs_last_value_and_a_stopped_run_keeps_what_it_made()
    {
        // Run 1 from 09:15, ended at 09:17 on +300; run 2 from 09:16.
        var first = Series(1, (555, 100m), (556, 250m), (557, 300m));
        var second = Series(2, (556, -40m), (558, -10m));

        var account = RunPnlSeriesBuilder.SumAccount(7, "trader", new[] { first, second });

        Assert.Equal(new[] { 555, 556, 557, 558 }, account.Minutes);
        Assert.Equal(new[] { 100m, 210m, 260m, 290m }, account.Net);
        Assert.Equal(new[] { 100m, 210m, 260m, 290m }, account.Realized);
        Assert.Equal(2, account.Runs);
    }

    [Fact]
    public async Task A_date_that_is_not_yyyy_MM_dd_is_refused()
    {
        using var desk = new PnlDesk();
        var result = await desk.InScope(sp => desk.Controller(sp, PnlDesk.Trader, admin: false)
            .GetPnlSeries("28-09-2026", null, sp.GetRequiredService<RunPnlSeriesBuilder>(), CancellationToken.None));

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private static RunPnlSeries Series(long runId, params (int Minute, decimal Net)[] points) => new()
    {
        RunId = runId,
        Minutes = points.Select(p => p.Minute).ToList(),
        Net = points.Select(p => p.Net).ToList(),
        Realized = points.Select(p => p.Net).ToList(),
        Unrealized = points.Select(_ => 0m).ToList(),
        Charges = points.Select(_ => 0m).ToList(),
    };

    // ======================================================== the harness

    /// <summary>The engine, the run views and the recorder over one in-memory database.</summary>
    private sealed class PnlDesk : IDisposable
    {
        public const long Trader = 7;
        public const long OtherTrader = 8;
        public const long AdminId = 1;

        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"pnl-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;
        private readonly string _emptyEngine = Directory.CreateTempSubdirectory("pnl-engine-").FullName;

        public PnlDesk()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<TradingDbContext>(Configure);
            services.AddSingleton<IRiskManagementService>(RecapClockTests.Inert<IRiskManagementService>.Create());
            services.AddSingleton<IProcessSettingsStore>(RecapClockTests.Inert<IProcessSettingsStore>.Create());
            services.AddSingleton<ILotSizeResolver>(new PositionGreeksTests.FixedLots(LotSize));
            services.AddSingleton<IMarketSessionService>(PositionGreeksTests.Sessions());
            services.AddScoped<IPaperTradingService, PaperTradingService>();
            services.AddSingleton<StrategyProcessRegistry>();
            services.AddScoped<RunCharges>();
            services.AddScoped<RunPnl>();
            services.AddScoped<RunPnlRecorder>();
            services.AddScoped<RunPnlSeriesBuilder>();
            services.AddScoped<PositionGreeksBuilder>();
            services.AddScoped<PositionViewBuilder>();
            services.Configure<StrategyRunnerOptions>(o => o.EngineDirectory = _emptyEngine);
            services.AddSingleton(sp => new StrategyCatalogService(
                new PythonEngineLocator(sp.GetRequiredService<IOptions<StrategyRunnerOptions>>(), RecapClockTests.Inert<IWebHostEnvironment>.Create()),
                NullLogger<StrategyCatalogService>.Instance));
            services.AddScoped<LiveRunHistoryBuilder>();
            _provider = services.BuildServiceProvider();

            using var db = Db();
            db.AppUsers.AddRange(
                new AppUser { Id = AdminId, UserName = "admin", Role = UserRoles.Admin, IsActive = true },
                new AppUser { Id = Trader, UserName = "trader", Role = UserRoles.Trader, IsActive = true },
                new AppUser { Id = OtherTrader, UserName = "rahul", Role = UserRoles.Trader, IsActive = true });
            db.SaveChanges();
        }

        private void Configure(DbContextOptionsBuilder options) => options
            .UseInMemoryDatabase(_name, _root)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));

        public TradingDbContext Db()
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>();
            Configure(options);
            return new TradingDbContext(options.Options);
        }

        public async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
        {
            using var scope = _provider.CreateScope();
            return await work(scope.ServiceProvider);
        }

        // ------------------------------------------------------ arranging --

        public long NewRun(string strategy, long userId, string status = "Running", string mode = "LivePaper",
            DateTime? completedUtc = null, string parameters = "{\"underlying\":\"NIFTY\"}")
        {
            using var db = Db();
            var run = new SimulationRun
            {
                UserId = userId, Mode = mode, Status = status, Symbol = "NSE:NIFTY50-INDEX", StrategyName = strategy,
                ParametersJson = parameters, StartedUtc = Ist(28, 9, 15), CreatedUtc = Ist(28, 9, 15),
                CompletedUtc = completedUtc, LastError = string.Empty, Resolution = "1m", ReplaySpeed = string.Empty
            };
            db.SimulationRuns.Add(run);
            db.SaveChanges();
            return run.Id;
        }

        public long NewBook(long userId, DateTime openedUtc)
        {
            using var db = Db();
            var book = ManualBook.NewBook(userId, openedUtc);
            db.SimulationRuns.Add(book);
            db.SaveChanges();
            return book.Id;
        }

        /// <summary>
        /// A fill and the position it leaves: an open leg, or with
        /// <paramref name="closes"/> the leg it closed, now Closed with
        /// <paramref name="realized"/> booked.
        /// </summary>
        public long Fill(long runId, string symbol, string side, int lots, decimal price, DateTime atUtc,
            bool closes = false, decimal realized = 0m)
        {
            using var db = Db();
            db.PaperOrders.Add(new PaperOrder
            {
                SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = symbol, Side = side,
                Quantity = lots, OrderType = "MARKET_SIM", Status = "Filled", RequestedPrice = price, FillPrice = price,
                CreatedUtc = atUtc, FilledUtc = atUtc
            });

            if (closes)
            {
                var open = db.PaperPositions.Single(p => p.SimulationRunId == runId && p.Symbol == symbol && p.Status == "Open");
                open.Status = "Closed";
                open.Quantity = 0;
                open.RealizedPnl = realized;
                open.UnrealizedPnl = 0m;
                open.LastMarkPrice = price;
                open.ClosedUtc = atUtc;
                open.UpdatedUtc = atUtc;
                db.SaveChanges();
                return open.Id;
            }

            var position = new PaperPosition
            {
                SimulationRunId = runId, StrategyName = "Ghost", GroupId = "G1", Symbol = symbol,
                Direction = side == "BUY" ? "LONG" : "SHORT", Quantity = lots, AveragePrice = price,
                LastMarkPrice = price, Status = "Open", OpenedUtc = atUtc, UpdatedUtc = atUtc
            };
            db.PaperPositions.Add(position);
            db.SaveChanges();
            return position.Id;
        }

        /// <summary>The stop pipeline's end state: the leg squared off at <paramref name="exit"/>, the run Stopped.</summary>
        public void Flatten(long runId, long positionId, decimal exit, DateTime at)
        {
            using var db = Db();
            var position = db.PaperPositions.Single(p => p.Id == positionId);
            db.PaperOrders.Add(new PaperOrder
            {
                SimulationRunId = runId, StrategyName = "Ghost", GroupId = position.GroupId, Symbol = position.Symbol,
                Side = position.Direction == "LONG" ? "SELL" : "BUY", Quantity = position.Quantity, OrderType = "MARKET_SIM",
                Status = "Filled", RequestedPrice = exit, FillPrice = exit, CreatedUtc = at, FilledUtc = at
            });
            position.RealizedPnl = PaperPnl.Realized(position.Direction, position.AveragePrice, exit, position.Quantity, LotSize);
            position.UnrealizedPnl = 0m;
            position.LastMarkPrice = exit;
            position.Quantity = 0;
            position.Status = "Closed";
            position.ClosedUtc = at;

            var run = db.SimulationRuns.Single(r => r.Id == runId);
            run.Status = "Stopped";
            run.CompletedUtc = at;
            db.SaveChanges();
        }

        public void Quote(string symbol, decimal ltp)
        {
            using var db = Db();
            var row = db.LiveQuotesLatest.SingleOrDefault(x => x.Symbol == symbol);
            if (row is null)
            {
                row = new LiveQuoteLatest { Symbol = symbol, RawPayload = "{}", SourceKey = "dhan" };
                db.LiveQuotesLatest.Add(row);
            }
            row.LastTradedPrice = ltp;
            row.UpdatedUtc = DateTime.UtcNow;
            db.SaveChanges();
        }

        public void Point(long runId, DateTime atUtc, decimal net)
        {
            using var db = Db();
            db.RunPnlMinutes.Add(new RunPnlMinute { SimulationRunId = runId, AtUtc = atUtc, Realized = net, Net = net });
            db.SaveChanges();
        }

        // ------------------------------------------------------ acting --

        public Task<RunPnlRecorder.Pass> Record(DateTime nowUtc) =>
            InScope(sp => sp.GetRequiredService<RunPnlRecorder>().RecordAsync(nowUtc, CancellationToken.None));

        public StrategyController Controller(IServiceProvider sp, long userId, bool admin)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()), new(ClaimTypes.Name, admin ? "admin" : "trader") };
            if (admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));

            return new StrategyController(
                sp.GetRequiredService<TradingDbContext>(),
                sp.GetRequiredService<StrategyCatalogService>(),
                sp.GetRequiredService<StrategyProcessRegistry>(),
                null!,                                          // run control
                null!,                                          // engine locator
                sp.GetRequiredService<IPaperTradingService>(),
                sp.GetRequiredService<ILotSizeResolver>(),
                sp.GetRequiredService<PositionViewBuilder>(),
                sp.GetRequiredService<LiveRunHistoryBuilder>(),
                sp.GetRequiredService<RunCharges>(),
                null!,                                          // orders
                null!,                                          // watchlist
                null!,                                          // notifier
                null!,                                          // strategy access
                Options.Create(new StrategyRunnerOptions()),
                NullLogger<StrategyController>.Instance)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
                }
            };
        }

        /// <summary>The run card's figures: GET /api/Strategy/runs/{id}/live, as its owner.</summary>
        public Task<StrategyLiveViewResponse> RunCard(long runId) =>
            InScope(async sp =>
            {
                var result = await Controller(sp, Trader, admin: false).GetRunLive(runId, CancellationToken.None);
                return Assert.IsType<StrategyLiveViewResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
            });

        /// <summary>The owner's run history, as GET /api/Strategy/runs builds it.</summary>
        public Task<List<LiveRunSummaryResponse>> History() =>
            InScope(sp => sp.GetRequiredService<LiveRunHistoryBuilder>().ListAsync(
                new LiveRunHistoryFilter(Trader, null, null, null, null, null, 50, 0), CancellationToken.None));

        public Task<RunPnlSeriesResponse> Series(long callerId, bool admin, long? userId = null) =>
            InScope(async sp =>
            {
                var result = await Controller(sp, callerId, admin)
                    .GetPnlSeries("2026-09-28", userId, sp.GetRequiredService<RunPnlSeriesBuilder>(), CancellationToken.None);
                return Assert.IsType<RunPnlSeriesResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
            });

        // ------------------------------------------------------ reading --

        public List<RunPnlMinute> Minutes()
        {
            using var db = Db();
            return db.RunPnlMinutes.AsNoTracking().ToList();
        }

        public void Dispose()
        {
            _provider.Dispose();
            try { Directory.Delete(_emptyEngine, recursive: true); } catch (IOException) { }
        }
    }
}
