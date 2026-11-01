// src/AlgoTrading.Infrastructure/Services/RiskManagementService.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Exceptions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services;

public class RiskManagementService : IRiskManagementService
{
    /// <summary>
    /// <see cref="RiskEvent.Kind"/> of an order this gate refused: the only
    /// refused orders the platform keeps (Trade → Orders lists them).
    /// </summary>
    public const string OrderRejectedKind = "OrderRejected";

    private readonly TradingDbContext _dbContext;
    private readonly IRiskLimitsStore _limitsStore;

    // Rate limiting is intentionally in-process: it is a per-instance burst guard,
    // and losing the window on restart fails safe (the counter restarts at zero).
    private static readonly ConcurrentDictionary<long, ConcurrentQueue<DateTime>> _orderTimestamps = new();

    // The kill switch, by contrast, is persisted. A restart must NOT silently resume
    // trading after an operator has halted it, so the flag lives in system_settings
    // and this cache exists only to keep the hot order path off the database.
    private static volatile bool _killSwitchCache;
    private static DateTime _killSwitchCacheExpiresUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim CacheLock = new(1, 1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILotSizeResolver _lotSizes;
    private readonly TimeProvider _time;

    /// <param name="lotSizes">Lot sizes for the daily loss's charges and carried positions (<see cref="DailyLoss"/>).</param>
    /// <param name="time">The clock the daily loss's IST day is read from; the system clock when null.</param>
    public RiskManagementService(
        TradingDbContext dbContext,
        IRiskLimitsStore limitsStore,
        IServiceScopeFactory scopeFactory,
        ILotSizeResolver lotSizes,
        TimeProvider? time = null)
    {
        _dbContext = dbContext;
        _limitsStore = limitsStore;
        _scopeFactory = scopeFactory;
        _lotSizes = lotSizes;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Decides whether one leg may be filled.
    /// </summary>
    /// <param name="isClosing">
    /// True when this leg only reduces or closes an existing position.
    /// </param>
    /// <remarks>
    /// Every gate here exists to stop a run taking on MORE risk. None of them
    /// has any business stopping it from getting flat, and until now all three
    /// did: <c>side</c> was accepted and never read, so a strategy's own
    /// CLOSE_GROUP went through the same checks as an entry. A run that crossed
    /// the daily-loss line could not close the position that had crossed it —
    /// the loss limit locked the trader into the loss it was meant to stop.
    /// <para>
    /// So a closing leg is always allowed. That includes under the kill switch:
    /// halting trading means "open nothing more", and flattening is the outcome
    /// the switch exists to reach, not something to block on the way there.
    /// </para>
    /// <para>
    /// <paramref name="quantity"/> is recorded in the rejection reason but not
    /// gated on — there is no position-size limit in <c>RiskLimitsDto</c> yet.
    /// It is named here so nobody reads this signature and assumes size is
    /// being enforced.
    /// </para>
    /// </remarks>
    public async Task EvaluateOrderAsync(
        long simulationRunId,
        string symbol,
        string side,
        int quantity,
        bool isClosing,
        CancellationToken cancellationToken)
    {
        // Getting flat is never refused. Checked before anything else so a
        // closing leg cannot be caught by a gate added later either.
        if (isClosing) return;

        var limits = _limitsStore.GetLimits();

        // 1. Check Kill Switch
        if (await IsKillSwitchActiveAsync(cancellationToken))
        {
            await RejectOrderAsync(simulationRunId, symbol, side, quantity, "GLOBAL KILL SWITCH IS ACTIVE. NEW POSITIONS REJECTED (exits are always allowed).", cancellationToken);
        }

        // 2. Check Rate Limits (Max Orders per Minute)
        var queue = _orderTimestamps.GetOrAdd(simulationRunId, _ => new ConcurrentQueue<DateTime>());
        var now = DateTime.UtcNow;

        // Clean up old timestamps outside the 1-minute sliding window
        while (queue.TryPeek(out var oldest) && (now - oldest).TotalMinutes > 1)
        {
            queue.TryDequeue(out _);
        }

        // Counted BEFORE this order, and this order is only recorded once it has
        // passed. Enqueueing first made the counter include its own rejections,
        // so a run that hit the limit pushed itself further past it on every
        // retry and could never come back under the line.
        if (queue.Count >= limits.MaxOrdersPerMinute)
        {
            await RejectOrderAsync(simulationRunId, symbol, side, quantity, $"RATE LIMIT EXCEEDED: More than {limits.MaxOrdersPerMinute} orders placed in the last minute for run {simulationRunId} (leg {side} {quantity}).", cancellationToken);
        }

        // 3. Check Max Daily Loss: the run's net P&L for today (IST), after
        // charges (DailyLoss). Until 2 Nov it was the run's whole life, gross,
        // so a manual book or the AI Trader's book was shut for good by old
        // losses, and a high-churn run was never stopped by its charges.
        var day = await DailyLoss.ReadAsync(_dbContext, _lotSizes, simulationRunId, _time.GetUtcNow().UtcDateTime, cancellationToken);
        if (day.Net < limits.MaxDailyLoss)
        {
            await RejectOrderAsync(simulationRunId, symbol, side, quantity, DailyLossReason(day, limits.MaxDailyLoss), cancellationToken);
        }

        // Accepted: only now does it count toward the next order's rate window.
        queue.Enqueue(now);
    }

    /// <summary>
    /// The refusal for the daily loss. It starts "MAX DAILY LOSS EXCEEDED:", which Sentinel's log watcher
    /// keys on, and names what was measured: today's net, the charges in it, the limit.
    /// </summary>
    internal static string DailyLossReason(DailyLoss.Figures day, decimal limit)
    {
        string what = day.WholeRun ? "this recap run's net P&L" : "today's net P&L (IST day)";
        string until = day.WholeRun ? "" : " for the rest of the day";
        return $"MAX DAILY LOSS EXCEEDED: {what} {Rupees(day.Net)} (after {Rupees(day.Charges)} charges) is below the limit {Rupees(limit)}. "
             + $"New positions are refused{until}; exits remain allowed.";
    }

    /// <summary>"−₹52,310.50" / "₹1,240.00".</summary>
    private static string Rupees(decimal value)
        => (value < 0 ? "−₹" : "₹") + Math.Abs(value).ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    private async Task RejectOrderAsync(long simulationRunId, string symbol, string side, int lots, string reason, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var localDb = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        
        var riskEvent = new RiskEvent
        {
            OccurredUtc = DateTime.UtcNow,
            Kind = OrderRejectedKind,
            Reason = reason,
            SimulationRunId = simulationRunId,
            Symbol = symbol,
            DetailsJson = RejectionDetails.ToJson(side, lots)
        };
        localDb.RiskEvents.Add(riskEvent);
        await localDb.SaveChangesAsync(cancellationToken);
        
        throw new RiskViolationException(reason);
    }

    public Task ActivateKillSwitchAsync(CancellationToken cancellationToken)
        => SetKillSwitchAsync(true, updatedBy: null, reason: null, cancellationToken);

    public Task DeactivateKillSwitchAsync(CancellationToken cancellationToken)
        => SetKillSwitchAsync(false, updatedBy: null, reason: null, cancellationToken);

    public Task ActivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken)
        => SetKillSwitchAsync(true, updatedBy, reason, cancellationToken);

    public Task DeactivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken)
        => SetKillSwitchAsync(false, updatedBy, reason, cancellationToken);

    public async Task<bool> IsKillSwitchActiveAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _killSwitchCacheExpiresUtc)
        {
            return _killSwitchCache;
        }

        await CacheLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while we waited.
            if (DateTime.UtcNow < _killSwitchCacheExpiresUtc)
            {
                return _killSwitchCache;
            }

            var setting = await _dbContext.SystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Key == SystemSettingKeys.KillSwitchActive, cancellationToken);

            _killSwitchCache = string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase);
            _killSwitchCacheExpiresUtc = DateTime.UtcNow.Add(CacheTtl);
            return _killSwitchCache;
        }
        finally
        {
            CacheLock.Release();
        }
    }

    public async Task<KillSwitchState> GetKillSwitchStateAsync(CancellationToken cancellationToken)
    {
        var setting = await _dbContext.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == SystemSettingKeys.KillSwitchActive, cancellationToken);

        return new KillSwitchState
        {
            IsActive = string.Equals(setting?.Value, "true", StringComparison.OrdinalIgnoreCase),
            UpdatedBy = setting?.UpdatedBy,
            Reason = setting?.Reason,
            UpdatedUtc = setting?.UpdatedUtc
        };
    }

    private async Task SetKillSwitchAsync(bool active, string? updatedBy, string? reason, CancellationToken cancellationToken)
    {
        var setting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(x => x.Key == SystemSettingKeys.KillSwitchActive, cancellationToken);

        if (setting is null)
        {
            setting = new SystemSetting
            {
                Key = SystemSettingKeys.KillSwitchActive,
                CreatedUtc = DateTime.UtcNow
            };
            await _dbContext.SystemSettings.AddAsync(setting, cancellationToken);
        }

        setting.Value = active ? "true" : "false";
        setting.UpdatedBy = updatedBy;
        setting.UpdatedUtc = DateTime.UtcNow;

        var evt = new RiskEvent
        {
            OccurredUtc = DateTime.UtcNow,
            Kind = active ? "KillSwitchActivated" : "KillSwitchDeactivated",
            ActorName = updatedBy ?? "system",
            Reason = reason
        };
        _dbContext.RiskEvents.Add(evt);

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Publish immediately so the change takes effect without waiting out the TTL.
        _killSwitchCache = active;
        _killSwitchCacheExpiresUtc = DateTime.UtcNow.Add(CacheTtl);
    }
}
