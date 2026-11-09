using AlgoTrading.Api.Services;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services.Today;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AlgoTrading.IntegrationTests;

/// <summary>
/// Exercises the manual ticket against PostgreSQL, including a failure after
/// the fill rows were written but before protection can commit. Each test owns
/// a fresh database on the explicitly configured test server.
/// </summary>
public sealed class ManualOrderTransactionTests : IAsyncLifetime
{
    private const string Symbol = "NSE:TEST-EQ";
    private const long UserId = 7;
    private readonly string _database = $"manual_order_test_{Guid.NewGuid():N}";
    private string _adminConnection = null!;
    private string _connection = null!;

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresTheoryAttribute.Variable))
        {
            Database = "postgres", Pooling = false
        };
        _adminConnection = builder.ConnectionString;
        await AdminCommand($"CREATE DATABASE \"{_database}\"");
        builder.Database = _database;
        _connection = builder.ConnectionString;

        try
        {
            await using var db = OpenDb();
            await db.Database.EnsureCreatedAsync();
            db.AppUsers.Add(new AppUser { Id = UserId, UserName = "test-trader", Email = "trader@example.invalid" });
            db.Instruments.Add(new Instrument
            {
                Symbol = Symbol, Exchange = "NSE", Segment = "CM", InstrumentType = "EQ",
                IsEnabled = true, LotSize = 1, TickSize = 0.05m
            });
            db.LiveQuotesLatest.Add(new LiveQuoteLatest
            {
                Symbol = Symbol, LastTradedPrice = 100m, BidPrice = 99.5m, AskPrice = 100.5m,
                UpdatedUtc = DateTime.UtcNow, RawPayload = "{}", SourceKey = "test"
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public Task DisposeAsync() => AdminCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");

    [PostgresTheory]
    [InlineData("Running")]
    [InlineData("Stopped")]
    public async Task A_committed_carried_fill_is_valued_in_Today_even_after_the_runner_stops(string status)
    {
        await using var db = OpenDb();
        await Ticket(db, new RecordingEvents(() => OpenDb())).PlaceAsync(Command(90, 120, true), CancellationToken.None);
        var run = await db.SimulationRuns.SingleAsync();
        var position = await db.PaperPositions.SingleAsync();
        var now = DateTime.UtcNow;
        run.StartedUtc = run.CreatedUtc = now.AddDays(-2);
        run.Status = status;
        position.OpenedUtc = now.AddDays(-2);
        position.UnrealizedPnl = 999m; // The latest quote must override this stale stored value.
        (await db.LiveQuotesLatest.SingleAsync()).LastTradedPrice = 80m;
        await db.SaveChangesAsync();

        var directory = Directory.CreateTempSubdirectory("today-catalog-");
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "strategies"));
            using var provider = new ServiceCollection().BuildServiceProvider();
            var registry = new StrategyProcessRegistry(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<StrategyProcessRegistry>.Instance);
            var lots = new UnitLots();
            var charges = new RunCharges(db, lots);
            var catalog = new StrategyCatalogService(new PythonEngineLocator(
                Options.Create(new StrategyRunnerOptions { EngineDirectory = directory.FullName }), null!),
                NullLogger<StrategyCatalogService>.Instance);
            var history = new LiveRunHistoryBuilder(db, registry, catalog, lots, charges, new RunPnl(db, lots, charges));
            // TradingAsync reads neither host files nor market sessions.
            var builder = new TodayBuilder(db, history, null!, new AlgoTrading.Infrastructure.Ai.AiSettingsStore(db), null!);
            var trading = await builder.TradingAsync(IstTime.DateOf(now), CancellationToken.None);
            var row = Assert.Single(trading.Runs);
            var account = Assert.Single(trading.Accounts);
            Assert.Equal(run.Id, row.RunId);
            Assert.Equal(-20.5m, row.Unrealized);
            Assert.True(row.Charges > 0m);
            Assert.Equal(-row.Charges, row.RealizedNet);
            Assert.Equal(-20.5m - row.Charges, row.Net);
            Assert.Equal(row.Net, account.Net);
            Assert.Equal(1, account.OpenLegs);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [PostgresTheory]
    [InlineData(90, 120, true)]
    [InlineData(90, null, false)]
    [InlineData(null, 120, false)]
    [InlineData(null, null, true)]
    [InlineData(null, null, false)]
    public async Task First_fill_event_can_only_observe_the_complete_committed_position(int? stop, int? target, bool carry)
    {
        await using var db = OpenDb();
        var events = new RecordingEvents(() => OpenDb());
        var service = Ticket(db, events);

        var result = await service.PlaceAsync(Command(stop, target, carry), CancellationToken.None);

        Assert.NotNull(result.Fill);
        Assert.Empty(events.Errors);
        Assert.Contains(events.Observed, x => x.Kind == DeskEventKinds.Fill);
        Assert.All(events.Observed, observed =>
        {
            Assert.Equal(1, observed.Signals);
            Assert.Equal(1, observed.Orders);
            var position = Assert.Single(observed.Positions);
            Assert.Equal((decimal?)stop, position.StopLossPrice);
            Assert.Equal((decimal?)target, position.TargetPrice);
            Assert.Equal(carry, position.CarryForward);
            Assert.Equal(100.5m, position.AveragePrice);
        });
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_cancelled_protection_write_rolls_back_the_entire_fill(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new FailProtectionWrite(cancel, cancellation);
        await using var db = OpenDb(failure);
        var events = new RecordingEvents(() => OpenDb());
        var service = Ticket(db, events);

        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.PlaceAsync(Command(90, 120, true), cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() =>
                service.PlaceAsync(Command(90, 120, true), cancellation.Token));

        Assert.True(failure.ReachedProtectionWrite);
        Assert.True(failure.InTransaction);
        Assert.Equal(1, failure.OrdersBeforeFailure);
        await using var observer = OpenDb();
        Assert.Empty(await observer.SimulationSignals.ToListAsync());
        Assert.Empty(await observer.PaperOrders.ToListAsync());
        Assert.Empty(await observer.PaperPositions.ToListAsync());
        Assert.Empty(events.Observed);
        Assert.Empty(events.Errors);

        // Rollback and cancellation must also release the run gate, so a
        // subsequent request can book a fully protected position.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var retry = await Ticket(observer, events).PlaceAsync(Command(90, 120, true), timeout.Token);
        Assert.NotNull(retry.Fill);
        Assert.Single(await observer.PaperOrders.AsNoTracking().ToListAsync());
        var protectedPosition = Assert.Single(await observer.PaperPositions.AsNoTracking().ToListAsync());
        Assert.Equal(90m, protectedPosition.StopLossPrice);
        Assert.Equal(120m, protectedPosition.TargetPrice);
        Assert.True(protectedPosition.CarryForward);
    }

    private static ManualOrderCommand Command(decimal? stop, decimal? target, bool carry) =>
        new(UserId, "test-trader", Symbol, "BUY", 1, null, stop, target, carry);

    private static ManualOrderService Ticket(TradingDbContext db, IDeskEventPublisher events)
    {
        var lots = new UnitLots();
        var paper = new PaperTradingService(db, new AllowOrders(), lots, deskEvents: events);
        return new ManualOrderService(db, paper, lots, NullLogger.Instance, events);
    }

    private TradingDbContext OpenDb(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(_connection);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new TradingDbContext(options.Options);
    }

    private async Task AdminCommand(string sql)
    {
        await using var connection = new NpgsqlConnection(_adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FailProtectionWrite(bool cancel, CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        public bool ReachedProtectionWrite { get; private set; }
        public bool InTransaction { get; private set; }
        public int OrdersBeforeFailure { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = (TradingDbContext)eventData.Context!;
            if (!db.ChangeTracker.Entries<PaperPosition>().Any(x =>
                    x.State == EntityState.Modified && x.Entity.StopLossPrice is not null && x.Entity.TargetPrice is not null))
                return result;

            ReachedProtectionWrite = true;
            InTransaction = db.Database.CurrentTransaction is not null;
            OrdersBeforeFailure = await db.PaperOrders.CountAsync(cancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected protection write failure.");
        }
    }

    private sealed record ObservedBooking(string Kind, int Signals, int Orders, List<PaperPosition> Positions);

    private sealed class RecordingEvents(Func<TradingDbContext> openDb) : IDeskEventPublisher
    {
        public List<ObservedBooking> Observed { get; } = [];
        public List<Exception> Errors { get; } = [];

        public void Publish(DeskEvent deskEvent)
        {
            // A separate connection sees only committed data, just like a
            // browser fetching the position as soon as its fill event arrives.
            try
            {
                using var db = openDb();
                Observed.Add(new ObservedBooking(deskEvent.Kind, db.SimulationSignals.Count(), db.PaperOrders.Count(),
                    db.PaperPositions.AsNoTracking().ToList()));
            }
            catch (Exception ex)
            {
                Errors.Add(ex);
            }
        }
    }

    private sealed class UnitLots : ILotSizeResolver
    {
        public Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default)
            => Task.FromResult(new LotSizeInfo(1, "test", symbol));
        public Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default)
            => ResolveAsync(underlying, cancellationToken);
        public Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, LotSizeInfo>>(symbols.Distinct().ToDictionary(x => x, x => new LotSizeInfo(1, "test", x)));
    }

    private sealed class AllowOrders : IRiskManagementService
    {
        public Task EvaluateOrderAsync(long simulationRunId, string symbol, string side, int quantity, bool isClosing, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ActivateKillSwitchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeactivateKillSwitchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ActivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeactivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsKillSwitchActiveAsync(CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<KillSwitchState> GetKillSwitchStateAsync(CancellationToken cancellationToken) => Task.FromResult(new KillSwitchState());
    }
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public const string Variable = "OPENFNO_TEST_POSTGRES";

    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a disposable PostgreSQL server; see this project's README.";
    }
}
