using System.Reflection;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A recap run must read against the replayed day's chart.
///
/// On 2026-09-11 run 117 signalled a NIFTY 23250 CE entry at 10:07:38 on the
/// replay, and the console showed it filled at 19:07:39; the leg-target exit
/// the API made itself showed 19:21:07, and sorted above the entry it closed.
/// These pin the fix: every row of a recap run is on the replayed session's
/// clock, and a live run keeps the wall clock exactly as before.
/// </summary>
public class RecapClockTests
{
    private const string Spot = "NSE:NIFTY50-INDEX";
    private const string Option = "NSE:NIFTY2691523250CE";
    private const string RecapParameters = """{"session":"recap","recap_date":"2026-09-11","lots":2}""";
    private const string LiveParameters = """{"lots":2}""";

    private static readonly DateTime EntrySignal = new(2026, 9, 11, 4, 37, 38, DateTimeKind.Utc);   // 10:07:38 IST
    private static readonly DateTime ReplayAtExit = new(2026, 9, 11, 4, 51, 5, DateTimeKind.Utc);   // 10:21:05 IST

    [Theory]
    [InlineData(RecapParameters, true)]
    [InlineData("""{"session":"RECAP"}""", true)]
    [InlineData(LiveParameters, false)]
    [InlineData("""{"session":"live"}""", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void Reads_the_session_from_the_run_parameters(string? json, bool expected)
    {
        Assert.Equal(expected, RecapClock.IsRecap(json));
    }

    [Fact]
    public void Takes_a_stamp_only_when_it_is_a_real_minute_on_the_replayed_day()
    {
        var day = new DateOnly(2026, 9, 11);
        Assert.Equal(ReplayAtExit, RecapClock.OnReplayedDay(ReplayAtExit, day));
        // BSE's date-only stamp: midnight IST says which day, not which minute.
        Assert.Null(RecapClock.OnReplayedDay(new DateTime(2026, 9, 10, 18, 30, 0, DateTimeKind.Utc), day));
        // Yesterday's quote, still sitting there before tonight's replay reached it.
        Assert.Null(RecapClock.OnReplayedDay(ReplayAtExit.AddDays(-1), day));
        Assert.Null(RecapClock.OnReplayedDay(null, day));
    }

    [Fact]
    public async Task A_recap_entry_is_filled_at_the_time_the_runner_stamped_it()
    {
        var (service, db, runId) = Build(RecapParameters);

        await service.CreateSignalAsync(OpenSignal(runId));

        var order = db.PaperOrders.AsNoTracking().Single();
        var position = db.PaperPositions.AsNoTracking().Single();
        Assert.Equal(EntrySignal, order.FilledUtc);
        Assert.Equal(EntrySignal, order.CreatedUtc);
        Assert.Equal(EntrySignal, position.OpenedUtc);
    }

    [Fact]
    public async Task A_live_entry_keeps_the_wall_clock()
    {
        var (service, db, runId) = Build(LiveParameters);

        await service.CreateSignalAsync(OpenSignal(runId));

        var order = db.PaperOrders.AsNoTracking().Single();
        Assert.True(DateTime.UtcNow - order.FilledUtc!.Value < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task A_recap_exit_made_by_the_API_is_timed_by_the_replay_and_sorts_after_the_entry()
    {
        var (service, db, runId) = Build(RecapParameters);
        await service.CreateSignalAsync(OpenSignal(runId));
        SetQuote(db, Spot, 23290.10m, ReplayAtExit);
        SetQuote(db, Option, 137.75m, ReplayAtExit.AddSeconds(-2));
        var positionId = db.PaperPositions.AsNoTracking().Single().Id;

        int closed = await service.ClosePositionsAsync(runId, new[] { positionId }, "Leg target hit", "risk-guard");

        Assert.Equal(1, closed);
        var exit = db.PaperOrders.AsNoTracking().Single(x => x.Side == "SELL");
        var position = db.PaperPositions.AsNoTracking().Single();
        var close = db.SimulationSignals.AsNoTracking().Single(x => x.SignalType == "CLOSE_GROUP");
        Assert.Equal(137.75m, exit.FillPrice);
        Assert.Equal(ReplayAtExit, exit.FilledUtc);
        Assert.Equal(ReplayAtExit, position.ClosedUtc);
        Assert.Equal(ReplayAtExit, close.TimestampUtc);
        // When the row was written is still the wall clock.
        Assert.True(DateTime.UtcNow - close.CreatedUtc < TimeSpan.FromMinutes(1));

        var newestFirst = db.SimulationSignals.AsNoTracking()
            .OrderByDescending(x => x.TimestampUtc).Select(x => x.SignalType).ToList();
        Assert.Equal(new[] { "CLOSE_GROUP", "OPEN_GROUP" }, newestFirst);
    }

    [Fact]
    public async Task A_recap_whose_spot_is_not_on_the_replayed_day_keeps_the_wall_clock()
    {
        var (service, db, runId) = Build(RecapParameters);
        await service.CreateSignalAsync(OpenSignal(runId));
        SetQuote(db, Spot, 23290.10m, ReplayAtExit.AddDays(-1));
        SetQuote(db, Option, 137.75m, ReplayAtExit.AddDays(-1));
        var positionId = db.PaperPositions.AsNoTracking().Single().Id;

        await service.ClosePositionsAsync(runId, new[] { positionId }, "Square off", "admin");

        var exit = db.PaperOrders.AsNoTracking().Single(x => x.Side == "SELL");
        Assert.True(DateTime.UtcNow - exit.FilledUtc!.Value < TimeSpan.FromMinutes(1));
    }

    private static CreateSimulationSignalRequest OpenSignal(long runId) => new()
    {
        SimulationRunId = runId,
        StrategyName = "GhostTangentCrossings",
        SignalType = "OPEN_GROUP",
        TimestampUtc = EntrySignal,
        GroupId = "GTC_1789133859",
        MetadataJson = "{}",
        Legs = new List<SimulationSignalLegRequest>
        {
            new() { Symbol = Option, Side = "BUY", Quantity = 2, Price = 116.10m },
        },
    };

    private static (PaperTradingService Service, TradingDbContext Db, long RunId) Build(string parametersJson)
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"recap-clock-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var db = new TradingDbContext(options);
        var run = new SimulationRun
        {
            Mode = PaperTradingService.LivePaperMode,
            Symbol = Spot,
            Status = "Running",
            StrategyName = "GhostTangentCrossings",
            ParametersJson = parametersJson,
        };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return (new PaperTradingService(db, Inert<IRiskManagementService>.Create(), new FixedLotSize(65)), db, run.Id);
    }

    private static void SetQuote(TradingDbContext db, string symbol, decimal ltp, DateTime exchangeUtc)
    {
        var row = db.LiveQuotesLatest.SingleOrDefault(x => x.Symbol == symbol);
        if (row is null)
        {
            row = new LiveQuoteLatest { Symbol = symbol, SourceKey = "truedata", DataType = "symbolUpdate", RawPayload = "{}" };
            db.LiveQuotesLatest.Add(row);
        }
        row.LastTradedPrice = ltp;
        row.ExchangeTimestampUtc = exchangeUtc;
        row.UpdatedUtc = DateTime.UtcNow;
        db.SaveChanges();
    }

    private sealed class FixedLotSize(int lotSize) : ILotSizeResolver
    {
        public Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default)
            => Task.FromResult(new LotSizeInfo(lotSize, "test", "NIFTY"));

        public Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, LotSizeInfo>>(
                symbols.Distinct().ToDictionary(s => s, _ => new LotSizeInfo(lotSize, "test", "NIFTY")));

        public Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default)
            => Task.FromResult(new LotSizeInfo(lotSize, "test", underlying));
    }

    /// <summary>Any interface whose every call completes and does nothing (the risk gate passes).</summary>
    public class Inert<T> : DispatchProxy where T : class
    {
        public static T Create() => DispatchProxy.Create<T, Inert<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod!.ReturnType;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = type.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
            }
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
