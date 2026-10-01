using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
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
public class AiTraderBookReaderTests
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

    // ---------- helpers ----------

    internal static AiTraderBookReader Reader(TradingDbContext db, bool killSwitch = false, StrategyProcessRegistry? registry = null)
    {
        var lots = new MarketBriefBuilderTests.Lots(65);
        var charges = new RunCharges(db, lots);
        registry ??= new StrategyProcessRegistry(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StrategyProcessRegistry>.Instance);
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
