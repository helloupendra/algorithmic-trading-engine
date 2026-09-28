using System.Security.Claims;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/OptionChain/positions answers for one run mode: the live chain
/// never shows a backtest's legs, and a backtest's legs are never marked at
/// today's quote.
/// </summary>
/// <remarks>
/// Until 28 Sep the endpoint took every running run of either mode, so a
/// backtest's open legs on the chain's underlying reached the live chain,
/// valued at the live price of a day it was not replaying.
/// </remarks>
public class OptionChainPositionsTests
{
    private const long Admin = 1;
    private const long Trader = 7;
    private const int LotSize = 65;
    private const string NiftyCall = "NSE:NIFTY2692924500CE";
    private const string NiftyPut = "NSE:NIFTY2692924500PE";

    [Fact]
    public async Task By_default_only_live_runs_and_manual_books_are_answered()
    {
        using var chain = new Chain();
        var seeded = chain.Seed();

        var positions = await chain.Positions(Admin, admin: true, mode: null);

        Assert.Equal(new[] { seeded.LiveRun, seeded.Book }.Order(), positions.Select(p => p.RunId).Order());
        Assert.DoesNotContain(positions, p => p.RunId == seeded.Backtest);
    }

    [Fact]
    public async Task Live_paper_asked_by_name_is_the_same_answer()
    {
        using var chain = new Chain();
        chain.Seed();

        var byDefault = await chain.Positions(Admin, admin: true, mode: null);
        var byName = await chain.Positions(Admin, admin: true, mode: "livepaper");

        Assert.Equal(byDefault.Select(p => p.RunId), byName.Select(p => p.RunId));
    }

    [Fact]
    public async Task A_live_leg_is_marked_at_the_newer_live_quote()
    {
        using var chain = new Chain();
        var seeded = chain.Seed();

        var positions = await chain.Positions(Admin, admin: true, mode: null);
        var call = positions.Single(p => p.RunId == seeded.LiveRun);

        Assert.Equal(120m, call.MarkPrice);                     // the quote, not the stored 100
        Assert.Equal(chain.QuoteUtc, call.MarkUtc);
        Assert.Equal(20m * 2 * LotSize, call.UnrealizedPnl);
    }

    [Fact]
    public async Task Backtests_are_answered_only_when_asked_for_at_their_own_mark()
    {
        using var chain = new Chain();
        var seeded = chain.Seed();

        var positions = await chain.Positions(Admin, admin: true, mode: "OfflineReplay");

        var leg = Assert.Single(positions);
        Assert.Equal(seeded.Backtest, leg.RunId);
        Assert.Equal(90m, leg.MarkPrice);                       // its replay's bar close, never today's 120
        Assert.Equal(chain.ReplayMarkUtc, leg.MarkUtc);
        Assert.Equal(-10m * LotSize, leg.UnrealizedPnl);
    }

    [Fact]
    public async Task A_trader_sees_only_their_own_runs_in_either_mode()
    {
        using var chain = new Chain();
        var seeded = chain.Seed();

        var live = await chain.Positions(Trader, admin: false, mode: null);
        var backtests = await chain.Positions(Trader, admin: false, mode: "OfflineReplay");

        Assert.Equal(new[] { seeded.Book }, live.Select(p => p.RunId));
        Assert.Empty(backtests);                                // the backtest is the admin's
    }

    [Fact]
    public async Task An_unknown_mode_is_refused()
    {
        using var chain = new Chain();
        chain.Seed();

        var result = await chain.Controller(Admin, admin: true)
            .GetPositions("NIFTY", "Recap", chain.Db(), new PositionGreeksTests.FixedLots(LotSize), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    private sealed class Chain : IDisposable
    {
        private readonly InMemoryDatabaseRoot _root = new();
        private readonly string _name = $"chain-positions-{Guid.NewGuid():N}";
        private readonly List<TradingDbContext> _contexts = new();

        public DateTime QuoteUtc { get; } = DateTime.UtcNow.AddSeconds(-5);
        public DateTime ReplayMarkUtc { get; } = DateTime.UtcNow.AddMinutes(-1);

        public TradingDbContext Db()
        {
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(_name, _root).Options);
            _contexts.Add(db);
            return db;
        }

        public sealed record Seeded(long LiveRun, long Book, long Backtest);

        public Seeded Seed()
        {
            var db = Db();
            db.AppUsers.AddRange(
                new AppUser { Id = Admin, UserName = "admin", Role = UserRoles.Admin },
                new AppUser { Id = Trader, UserName = "trader", Role = UserRoles.Trader });
            foreach (var (symbol, type) in new[] { (NiftyCall, "CE"), (NiftyPut, "PE") })
            {
                db.Instruments.Add(new Instrument
                {
                    Symbol = symbol, Exchange = "NSE", Segment = "FO", InstrumentType = type, OptionType = type,
                    Underlying = "NIFTY", StrikePrice = 24500m, ExpiryDate = new DateOnly(2026, 9, 29), IsEnabled = true
                });
            }

            var live = Run(db, Admin, "Ghost", StrategyRunControl.LivePaperMode);
            var book = Run(db, Trader, ManualOrdersController.BookStrategyName, StrategyRunControl.LivePaperMode);
            var backtest = Run(db, Admin, "Ghost", BacktestRunControl.OfflineReplayMode);
            db.SaveChanges();

            var stale = QuoteUtc.AddMinutes(-10);
            Leg(db, live, NiftyCall, "LONG", 2, entry: 100m, mark: 100m, markedUtc: stale);
            Leg(db, book, NiftyPut, "SHORT", 1, entry: 80m, mark: 80m, markedUtc: stale);
            Leg(db, backtest, NiftyCall, "LONG", 1, entry: 100m, mark: 90m, markedUtc: ReplayMarkUtc);
            db.LiveQuotesLatest.Add(new LiveQuoteLatest
            {
                Symbol = NiftyCall, LastTradedPrice = 120m, UpdatedUtc = QuoteUtc, RawPayload = "{}", SourceKey = "dhan"
            });
            db.SaveChanges();

            return new Seeded(live.Id, book.Id, backtest.Id);
        }

        public async Task<List<OptionChainPositionResponse>> Positions(long callerId, bool admin, string? mode)
        {
            var result = await Controller(callerId, admin)
                .GetPositions("NIFTY", mode, Db(), new PositionGreeksTests.FixedLots(LotSize), CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            return Assert.IsAssignableFrom<IEnumerable<OptionChainPositionResponse>>(ok.Value).ToList();
        }

        public OptionChainController Controller(long callerId, bool admin)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, callerId.ToString()) };
            if (admin) claims.Add(new Claim(ClaimTypes.Role, UserRoles.Admin));
            return new OptionChainController(null!, null!)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
                }
            };
        }

        private static SimulationRun Run(TradingDbContext db, long userId, string strategy, string mode)
        {
            var run = new SimulationRun
            {
                UserId = userId, Mode = mode, Status = "Running", Symbol = "NSE:NIFTY50-INDEX", StrategyName = strategy,
                ParametersJson = "{}", LastError = string.Empty, Resolution = "1m", ReplaySpeed = string.Empty,
                StartedUtc = DateTime.UtcNow.AddHours(-1), CreatedUtc = DateTime.UtcNow.AddHours(-1)
            };
            db.SimulationRuns.Add(run);
            return run;
        }

        private static void Leg(TradingDbContext db, SimulationRun run, string symbol, string direction, int lots,
            decimal entry, decimal mark, DateTime markedUtc)
        {
            db.PaperPositions.Add(new PaperPosition
            {
                SimulationRunId = run.Id, StrategyName = run.StrategyName, GroupId = "G1", Symbol = symbol, Direction = direction,
                Quantity = lots, AveragePrice = entry, LastMarkPrice = mark, Status = "Open", OpenedUtc = markedUtc,
                UpdatedUtc = markedUtc
            });
        }

        public void Dispose()
        {
            foreach (var db in _contexts) db.Dispose();
        }
    }
}
