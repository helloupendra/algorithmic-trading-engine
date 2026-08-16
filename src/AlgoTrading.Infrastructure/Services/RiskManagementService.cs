// src/AlgoTrading.Infrastructure/Services/RiskManagementService.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Exceptions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services;

public class RiskManagementService : IRiskManagementService
{
    private readonly TradingDbContext _dbContext;
    private readonly RiskManagementSettings _settings;
    
    // Global static state for kill switch and rate limiting across scoped instances
    private static bool _isKillSwitchActive = false;
    private static readonly ConcurrentDictionary<long, ConcurrentQueue<DateTime>> _orderTimestamps = new();

    public RiskManagementService(TradingDbContext dbContext, IOptions<RiskManagementSettings> settings)
    {
        _dbContext = dbContext;
        _settings = settings.Value;
    }

    public async Task EvaluateOrderAsync(long simulationRunId, string symbol, string side, int quantity, CancellationToken cancellationToken)
    {
        // 1. Check Kill Switch
        if (_isKillSwitchActive)
        {
            throw new RiskViolationException("GLOBAL KILL SWITCH IS ACTIVE. ALL ORDERS REJECTED.");
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

        if (queue.Count > _settings.MaxOrdersPerMinute)
        {
            throw new RiskViolationException($"RATE LIMIT EXCEEDED: More than {_settings.MaxOrdersPerMinute} orders placed in the last minute for run {simulationRunId}.");
        }

        // 3. Check Max Daily Loss
        var positions = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        decimal totalRealized = positions.Sum(x => x.RealizedPnl);
        decimal totalUnrealized = positions.Where(x => x.Status == "Open").Sum(x => x.UnrealizedPnl);
        decimal currentPnl = totalRealized + totalUnrealized;

        if (currentPnl < _settings.MaxDailyLoss)
        {
            throw new RiskViolationException($"MAX DAILY LOSS EXCEEDED: Current PnL {currentPnl} is below the limit of {_settings.MaxDailyLoss}.");
        }
    }

    public Task ActivateKillSwitchAsync(CancellationToken cancellationToken)
    {
        _isKillSwitchActive = true;
        return Task.CompletedTask;
    }

    public Task DeactivateKillSwitchAsync(CancellationToken cancellationToken)
    {
        _isKillSwitchActive = false;
        return Task.CompletedTask;
    }

    public Task<bool> IsKillSwitchActiveAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(_isKillSwitchActive);
    }
}
