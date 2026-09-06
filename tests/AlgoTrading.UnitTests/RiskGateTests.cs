using AlgoTrading.Application.Exceptions;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The per-order risk gate.
///
/// Every gate in it exists to stop a run taking on MORE risk. None of them has
/// any business stopping it from getting flat — and until this was fixed all
/// three did, because the `side` parameter was accepted and never read. A run
/// that crossed the daily-loss line could not close the position that had
/// crossed it: the loss limit locked the trader into the loss it was meant to
/// stop. These tests exist so that cannot come back.
/// </summary>
public class RiskGateTests
{
    private sealed class FixedLimits : IRiskLimitsStore
    {
        private readonly RiskLimitsDto _limits;
        public FixedLimits(RiskLimitsDto limits) => _limits = limits;
        public RiskLimitsDto GetLimits() => _limits;
        public Task UpdateLimitsAsync(RiskLimitsDto newLimits, string updatedBy, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private static DbContextOptions<TradingDbContext> OptionsFor(string name)
        => new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static TradingDbContext NewDb(string name) => new(OptionsFor(name));

    /// <summary>
    /// The gate records every rejection through a fresh scope, so the scope
    /// factory has to be able to hand out a context of its own — sharing this
    /// test's instance would let a rejection write race the assertions.
    /// </summary>
    private static RiskManagementService Build(TradingDbContext db, RiskLimitsDto limits, string dbName)
    {
        var provider = new ServiceCollection()
            .AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)))
            .BuildServiceProvider();

        return new(db, new FixedLimits(limits), provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static RiskLimitsDto Limits(int perMinute = 50, decimal maxDailyLoss = -10_000m)
        => new() { MaxOrdersPerMinute = perMinute, MaxDailyLoss = maxDailyLoss, MaxConcurrentRuns = 10, MaxRunsPerUser = 5 };

    private static async Task WithLossAsync(TradingDbContext db, long runId, decimal realized)
    {
        db.PaperPositions.Add(new PaperPosition
        {
            SimulationRunId = runId, Symbol = "NSE:X", Direction = "SHORT", Quantity = 1,
            AveragePrice = 100m, Status = "Closed", RealizedPnl = realized, UnrealizedPnl = 0m,
        });
        await db.SaveChangesAsync();
    }

    // --- the bug this file exists for -------------------------------------

    [Fact]
    public async Task A_closing_leg_is_allowed_even_past_the_daily_loss_limit()
    {
        const string dbName = nameof(A_closing_leg_is_allowed_even_past_the_daily_loss_limit);
        using var db = NewDb(dbName);
        await WithLossAsync(db, runId: 1, realized: -50_000m); // far past the -10,000 limit

        var gate = Build(db, Limits(), dbName);

        // Must not throw: this is the run closing the very position that lost the money.
        await gate.EvaluateOrderAsync(1, "NSE:X", "BUY", 1, isClosing: true, CancellationToken.None);
    }

    [Fact]
    public async Task An_opening_leg_is_still_refused_past_the_daily_loss_limit()
    {
        const string dbName = nameof(An_opening_leg_is_still_refused_past_the_daily_loss_limit);
        using var db = NewDb(dbName);
        await WithLossAsync(db, runId: 2, realized: -50_000m);

        var gate = Build(db, Limits(), dbName);

        await Assert.ThrowsAsync<RiskViolationException>(() =>
            gate.EvaluateOrderAsync(2, "NSE:X", "SELL", 1, isClosing: false, CancellationToken.None));
    }

    [Fact]
    public async Task A_closing_leg_is_allowed_past_the_rate_limit()
    {
        const string dbName = nameof(A_closing_leg_is_allowed_past_the_rate_limit);
        using var db = NewDb(dbName);
        var gate = Build(db, Limits(perMinute: 2), dbName);

        await gate.EvaluateOrderAsync(3, "NSE:X", "SELL", 1, isClosing: false, CancellationToken.None);
        await gate.EvaluateOrderAsync(3, "NSE:X", "SELL", 1, isClosing: false, CancellationToken.None);

        // The window is full; an entry now fails but the exit must not.
        await Assert.ThrowsAsync<RiskViolationException>(() =>
            gate.EvaluateOrderAsync(3, "NSE:X", "SELL", 1, isClosing: false, CancellationToken.None));

        await gate.EvaluateOrderAsync(3, "NSE:X", "BUY", 1, isClosing: true, CancellationToken.None);
    }

    [Fact]
    public async Task A_rejected_order_does_not_count_toward_the_rate_window()
    {
        // The counter used to be incremented before the check, so a run that hit
        // the limit pushed itself further past it on every retry and could never
        // come back under the line.
        const string dbName = nameof(A_rejected_order_does_not_count_toward_the_rate_window);
        using var db = NewDb(dbName);
        var gate = Build(db, Limits(perMinute: 1), dbName);

        await gate.EvaluateOrderAsync(4, "NSE:X", "SELL", 1, isClosing: false, CancellationToken.None);

        for (int i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<RiskViolationException>(() =>
                gate.EvaluateOrderAsync(4, "NSE:X", "SELL", 1, isClosing: false, CancellationToken.None));
        }

        // Five refusals later the window still holds exactly the one accepted
        // order, so the run is one expiry away from trading again rather than six.
        var queue = typeof(RiskManagementService)
            .GetField("_orderTimestamps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as System.Collections.Concurrent.ConcurrentDictionary<long, System.Collections.Concurrent.ConcurrentQueue<DateTime>>;

        Assert.Equal(1, queue![4].Count);
    }

    [Fact]
    public async Task An_order_within_the_limits_is_allowed()
    {
        const string dbName = nameof(An_order_within_the_limits_is_allowed);
        using var db = NewDb(dbName);
        await WithLossAsync(db, runId: 5, realized: -100m);

        var gate = Build(db, Limits(), dbName);
        await gate.EvaluateOrderAsync(5, "NSE:X", "SELL", 1, isClosing: false, CancellationToken.None);
    }
}
