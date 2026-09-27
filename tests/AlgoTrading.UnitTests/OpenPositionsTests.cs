using System.Reflection;
using System.Security.Claims;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/Positions/open: every open leg in one answer, whatever it is
/// written on, and only the legs the caller may see.
/// </summary>
/// <remarks>
/// The Desk asked the option chain's positions endpoint once per underlying it
/// knew of; a leg on anything else (a crude future carried into the book, a
/// share) never reached it.
/// </remarks>
public class OpenPositionsTests
{
    private const long Admin = 1;
    private const long Trader = 7;
    private const long OtherTrader = 8;
    private const int LotSize = 75;

    private const string NiftyPut = "NSE:NIFTY2692924500PE";
    private const string NiftyCall = "NSE:NIFTY2692924500CE";
    private const string CrudeFuture = "MCX:CRUDEOIL26OCTFUT";

    [Fact]
    public void The_endpoint_needs_the_strategies_grant()
    {
        var attribute = typeof(PositionsController).GetCustomAttribute<RequireModuleAttribute>();
        Assert.NotNull(attribute);
        var key = typeof(RequireModuleAttribute).GetField("_moduleKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(attribute);
        Assert.Equal(PlatformModules.Strategies, key);
    }

    [Fact]
    public async Task A_trader_gets_their_own_legs_on_every_underlying_whatever_userId_they_pass()
    {
        using var desk = new Book();
        var seeded = desk.SeedDay();

        var own = await desk.Open(Trader, admin: false, userId: OtherTrader);

        Assert.Equal(new[] { seeded.TraderPut, seeded.TraderCrude }.Order(), own.Positions.Select(p => p.PositionId).Order());
        Assert.All(own.Positions, p => Assert.Equal(Trader, p.UserId));
        Assert.Equal(new[] { "CRUDEOIL", "NIFTY" }, own.Positions.Select(p => p.Underlying).Order());
    }

    [Fact]
    public async Task An_admin_gets_every_account_or_the_one_asked_for_and_never_a_closed_stopped_or_backtest_leg()
    {
        using var desk = new Book();
        var seeded = desk.SeedDay();

        var all = await desk.Open(Admin, admin: true);
        var one = await desk.Open(Admin, admin: true, userId: OtherTrader);

        Assert.Equal(new[] { seeded.TraderPut, seeded.TraderCrude, seeded.OtherCall }.Order(), all.Positions.Select(p => p.PositionId).Order());
        Assert.Equal(new[] { "rahul", "trader", "trader" }, all.Positions.Select(p => p.UserName));
        Assert.Equal(new[] { seeded.OtherCall }, one.Positions.Select(p => p.PositionId));
    }

    [Fact]
    public async Task A_leg_reads_as_its_run_card_reads_it()
    {
        using var desk = new Book();
        var seeded = desk.SeedDay();

        var answer = await desk.Open(Trader, admin: false);
        var put = answer.Positions.Single(p => p.PositionId == seeded.TraderPut);

        Assert.Equal(seeded.TraderRun, put.RunId);
        Assert.Equal("Ghost", put.StrategyName);
        Assert.False(put.IsManualBook);
        Assert.Equal("trader", put.UserName);
        Assert.Equal(NiftyPut, put.Symbol);
        Assert.Equal("NIFTY", put.Underlying);
        Assert.Equal(24500m, put.Strike);
        Assert.Equal("PE", put.OptionType);
        Assert.Equal(new DateOnly(2026, 9, 29), put.ExpiryDate);
        Assert.Equal("SHORT", put.Direction);
        Assert.Equal((2, LotSize, 150), (put.Lots, put.LotSize, put.Quantity));
        Assert.Equal(100m, put.EntryPrice);
        Assert.Equal(90m, put.MarkPrice);
        Assert.Equal(desk.QuoteUtc, put.MarkUtc);
        Assert.Equal((long)Math.Floor((answer.AsOfUtc - desk.QuoteUtc).TotalSeconds), put.MarkAgeSeconds);
        Assert.Equal(10m * 2 * LotSize, put.UnrealizedPnl);
        Assert.Equal(95m, put.StopLossPrice);
        Assert.Null(put.TargetPrice);
        Assert.False(put.CarryForward);

        // The quote carried the feed's greeks: a written put's theta is income.
        Assert.NotNull(put.Greeks);
        Assert.Equal(PositionGreeks.SourceFeed, put.Greeks!.Source);
        Assert.Equal(12m * 150, put.Greeks.ThetaRupeesPerDay);
    }

    [Fact]
    public async Task A_leg_carried_into_the_book_says_where_it_came_from_and_how_old_its_mark_is()
    {
        using var desk = new Book();
        var seeded = desk.SeedDay();

        var answer = await desk.Open(Trader, admin: false);
        var crude = answer.Positions.Single(p => p.PositionId == seeded.TraderCrude);

        Assert.True(crude.IsManualBook);
        Assert.Equal(ManualOrdersController.BookStrategyName, crude.StrategyName);
        Assert.True(crude.CarryForward);
        Assert.Equal(seeded.CarriedFromRun, crude.CarriedFromRunId);
        Assert.Equal("CrudeMomentum", crude.CarriedFromStrategy);
        Assert.Equal(string.Empty, crude.OptionType);
        Assert.Null(crude.Strike);
        Assert.Equal("LONG", crude.Direction);

        // No quote since the close: yesterday's mark, and it says so.
        Assert.Equal(6_540m, crude.MarkPrice);
        Assert.Equal(desk.YesterdayClose, crude.MarkUtc);
        Assert.True(crude.MarkAgeSeconds > 12 * 3600);
        Assert.Equal(40m * LotSize, crude.UnrealizedPnl);
    }

    [Fact]
    public async Task Nothing_open_is_an_empty_list_not_an_error()
    {
        using var desk = new Book();

        var answer = await desk.Open(Trader, admin: false);

        Assert.Empty(answer.Positions);
    }

    /// <summary>The position builders over one in-memory database, and a day's legs.</summary>
    private sealed class Book : IDisposable
    {
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"open-{Guid.NewGuid():N}";
        private readonly ServiceProvider _provider;

        public DateTime QuoteUtc { get; } = DateTime.UtcNow.AddSeconds(-20);
        public DateTime YesterdayClose { get; } = DateTime.UtcNow.AddHours(-18);

        public Book()
        {
            var services = new ServiceCollection();
            services.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(_name, _root));
            services.AddSingleton<ILotSizeResolver>(new PositionGreeksTests.FixedLots(LotSize));
            services.AddSingleton<IMarketSessionService>(PositionGreeksTests.Sessions());
            services.AddScoped<PositionGreeksBuilder>();
            services.AddScoped<PositionViewBuilder>();
            services.AddScoped<OpenPositionsBuilder>();
            _provider = services.BuildServiceProvider();
        }

        public sealed record Seeded(long TraderRun, long TraderPut, long TraderCrude, long OtherCall, long CarriedFromRun);

        public Seeded SeedDay()
        {
            using var scope = _provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.AppUsers.AddRange(
                new AppUser { Id = Admin, UserName = "admin", Role = UserRoles.Admin },
                new AppUser { Id = Trader, UserName = "trader", Role = UserRoles.Trader },
                new AppUser { Id = OtherTrader, UserName = "rahul", Role = UserRoles.Trader });

            var run = Run(db, "Ghost", Trader);
            var yesterdaysCrude = Run(db, "CrudeMomentum", Trader, status: "Stopped");
            var book = Run(db, ManualOrdersController.BookStrategyName, Trader);
            var otherRun = Run(db, "Ghost", OtherTrader);
            var stopped = Run(db, "Ghost", Trader, status: "Stopped");
            var backtest = Run(db, "Ghost", Trader, mode: "Backtest");
            db.SaveChanges();

            var put = Leg(db, run, NiftyPut, "SHORT", 2, 100m, stopLoss: 95m);
            Leg(db, run, NiftyCall, "LONG", 0, 80m, status: "Closed");
            var carriedSource = Leg(db, yesterdaysCrude, CrudeFuture, "LONG", 1, 6_500m, status: PaperTradingService.CarriedStatus);
            var otherCall = Leg(db, otherRun, NiftyCall, "LONG", 1, 80m);
            Leg(db, stopped, NiftyCall, "LONG", 1, 80m);
            Leg(db, backtest, NiftyCall, "LONG", 1, 80m);
            db.SaveChanges();

            var crude = Leg(db, book, CrudeFuture, "LONG", 1, 6_500m, mark: 6_540m, markedUtc: YesterdayClose,
                carry: true, carriedFrom: carriedSource.Id);

            db.LiveQuotesLatest.Add(new LiveQuoteLatest
            {
                Symbol = NiftyPut, LastTradedPrice = 90m, UpdatedUtc = QuoteUtc, RawPayload = "{}", SourceKey = "dhan",
                Delta = -0.42m, Gamma = 0.001m, Theta = -12m, Vega = 9m, ImpliedVolatility = 0.13m
            });
            db.SaveChanges();

            return new Seeded(run.Id, put.Id, crude.Id, otherCall.Id, yesterdaysCrude.Id);
        }

        public async Task<OpenPositionsResponse> Open(long callerId, bool admin, long? userId = null)
        {
            using var scope = _provider.CreateScope();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, callerId.ToString()) };
            if (admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));

            var controller = new PositionsController(scope.ServiceProvider.GetRequiredService<OpenPositionsBuilder>())
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
                }
            };

            var result = await controller.GetOpen(userId, CancellationToken.None);
            return Assert.IsType<OpenPositionsResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        }

        private static SimulationRun Run(TradingDbContext db, string strategy, long userId, string status = "Running", string mode = "LivePaper")
        {
            var run = new SimulationRun
            {
                UserId = userId, Mode = mode, Status = status, Symbol = "NSE:NIFTY50-INDEX", StrategyName = strategy,
                ParametersJson = "{}", LastError = string.Empty, Resolution = "1m", ReplaySpeed = string.Empty,
                StartedUtc = DateTime.UtcNow.AddHours(-1), CreatedUtc = DateTime.UtcNow.AddHours(-1)
            };
            db.SimulationRuns.Add(run);
            return run;
        }

        private PaperPosition Leg(TradingDbContext db, SimulationRun run, string symbol, string direction, int lots, decimal entry,
            string status = "Open", decimal? stopLoss = null, decimal? mark = null, DateTime? markedUtc = null,
            bool carry = false, long? carriedFrom = null)
        {
            var at = markedUtc ?? QuoteUtc.AddMinutes(-30);
            var position = new PaperPosition
            {
                SimulationRunId = run.Id, StrategyName = run.StrategyName, GroupId = "G1", Symbol = symbol, Direction = direction,
                Quantity = lots, AveragePrice = entry, LastMarkPrice = mark ?? entry, Status = status, OpenedUtc = at,
                UpdatedUtc = at, StopLossPrice = stopLoss, CarryForward = carry, CarriedFromPositionId = carriedFrom
            };
            db.PaperPositions.Add(position);
            db.SaveChanges();
            return position;
        }

        public void Dispose() => _provider.Dispose();
    }
}
