using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AlgoTrading.BacktestTests;

public sealed class ReplayBookingTests
{
    [Fact]
    public async Task Fixed_replay_books_the_same_round_trip_without_using_live_or_future_prices()
    {
        var first = await Replay();
        var second = await Replay();
        Assert.Equal(first, second);
        Assert.Equal((100.5m, 109.5m, 18m, "Closed"), first);
    }

    private static async Task<(decimal Entry, decimal Exit, decimal Pnl, string Status)> Replay()
    {
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        const string symbol = "NSE:TEST-EQ";
        var at = new DateTime(2026, 9, 30, 4, 0, 0, DateTimeKind.Utc);
        var book = new MarketReplayBook();
        book.Begin(new DateOnly(2026, 9, 30));
        var run = new SimulationRun { UserId = 1, StrategyName = "fixture", Symbol = symbol, Status = "Running",
            Mode = PaperTradingService.LivePaperMode, ParametersJson = """{"session":"recap","recap_date":"2026-09-30"}""" };
        db.SimulationRuns.Add(run);
        db.LiveQuotesLatest.Add(new() { Symbol = symbol, LastTradedPrice = 900, AskPrice = 901, BidPrice = 899,
            UpdatedUtc = DateTime.UtcNow, RawPayload = "{}", SourceKey = "live" });
        await db.SaveChangesAsync();
        var service = new PaperTradingService(db, new Risk(), new Lots(), replayBook: book);
        void Tick(decimal price, DateTime time) => book.Apply([new UpsertLiveTickRequest { Symbol = symbol,
            LastTradedPrice = price, AskPrice = price + .5m, BidPrice = price - .5m, ExchangeTimestampUtc = time, IsReplay = true }]);
        Task<SimulationSignalResponse> Signal(string type, string side, DateTime time) => service.CreateSignalAsync(new() {
            SimulationRunId = run.Id, StrategyName = "fixture", SignalType = type, GroupId = "round-trip",
            TimestampUtc = time, ClientSignalId = type,
            Legs = [new() { Symbol = symbol, Side = side, Quantity = 2 }],
        });
        Tick(100, at);
        await Signal("OPEN_GROUP", "BUY", at);
        await Signal("OPEN_GROUP", "BUY", at); // Strategy retries must not double exposure either.
        Tick(999, at.AddDays(1));
        Assert.Equal(100, book.Quote(symbol)!.LastTradedPrice);
        Tick(110, at.AddMinutes(10));
        await Signal("CLOSE_GROUP", "SELL", at.AddMinutes(10));
        var orders = await db.PaperOrders.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, orders.Count);
        var position = await db.PaperPositions.SingleAsync();
        Assert.Equal(at.AddMinutes(10), position.ClosedUtc);
        Assert.Equal(900, (await db.LiveQuotesLatest.SingleAsync()).LastTradedPrice);
        book.End();
        Assert.Null(book.Quote(symbol));
        return (orders[0].FillPrice!.Value, orders[1].FillPrice!.Value, position.RealizedPnl, position.Status);
    }
    private sealed class Lots : ILotSizeResolver {
        public Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken ct = default) => Task.FromResult(new LotSizeInfo(1, "fixture", symbol));
        public Task<LotSizeInfo> ResolveForUnderlyingAsync(string symbol, CancellationToken ct = default) => ResolveAsync(symbol, ct);
        public Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(IEnumerable<string> symbols, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, LotSizeInfo>>(symbols.Distinct().ToDictionary(s => s, s => new LotSizeInfo(1, "fixture", s)));
    }
    private sealed class Risk : IRiskManagementService {
        public Task EvaluateOrderAsync(long run, string symbol, string side, int qty, bool closing, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> IsKillSwitchActiveAsync(CancellationToken ct) => Task.FromResult(false);
        public Task<KillSwitchState> GetKillSwitchStateAsync(CancellationToken ct) => Task.FromResult(new KillSwitchState());
        public Task ActivateKillSwitchAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task DeactivateKillSwitchAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task ActivateKillSwitchAsync(string? by, string? reason, CancellationToken ct) => throw new NotSupportedException();
        public Task DeactivateKillSwitchAsync(string? by, string? reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
