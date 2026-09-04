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

    public RiskManagementService(TradingDbContext dbContext, IRiskLimitsStore limitsStore, IServiceScopeFactory scopeFactory)
    {
        _dbContext = dbContext;
        _limitsStore = limitsStore;
        _scopeFactory = scopeFactory;
    }

    public async Task EvaluateOrderAsync(long simulationRunId, string symbol, string side, int quantity, CancellationToken cancellationToken)
    {
        var limits = _limitsStore.GetLimits();

        // 1. Check Kill Switch
        if (await IsKillSwitchActiveAsync(cancellationToken))
        {
            await RejectOrderAsync(simulationRunId, symbol, "GLOBAL KILL SWITCH IS ACTIVE. ALL ORDERS REJECTED.", cancellationToken);
        }

        // 2. Check Rate Limits (Max Orders per Minute)
        var queue = _orderTimestamps.GetOrAdd(simulationRunId, _ => new ConcurrentQueue<DateTime>());
        var now = DateTime.UtcNow;
        queue.Enqueue(now);

        // Clean up old timestamps outside the 1-minute sliding window
        while (queue.TryPeek(out var oldest) && (now - oldest).TotalMinutes > 1)
        {
            queue.TryDequeue(out _);
        }

        if (queue.Count > limits.MaxOrdersPerMinute)
        {
            await RejectOrderAsync(simulationRunId, symbol, $"RATE LIMIT EXCEEDED: More than {limits.MaxOrdersPerMinute} orders placed in the last minute for run {simulationRunId}.", cancellationToken);
        }

        // 3. Check Max Daily Loss
        var positions = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        decimal totalRealized = positions.Sum(x => x.RealizedPnl);
        decimal totalUnrealized = positions.Where(x => x.Status == "Open").Sum(x => x.UnrealizedPnl);
        decimal currentPnl = totalRealized + totalUnrealized;

        if (currentPnl < limits.MaxDailyLoss)
        {
            await RejectOrderAsync(simulationRunId, symbol, $"MAX DAILY LOSS EXCEEDED: Current PnL {currentPnl} is below the limit of {limits.MaxDailyLoss}.", cancellationToken);
        }
    }

    private async Task RejectOrderAsync(long simulationRunId, string symbol, string reason, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var localDb = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        
        var riskEvent = new RiskEvent
        {
            OccurredUtc = DateTime.UtcNow,
            Kind = "OrderRejected",
            Reason = reason,
            SimulationRunId = simulationRunId,
            Symbol = symbol
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
