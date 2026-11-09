using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>Serializes the global stop with strategy starts and switch releases.</summary>
public sealed class KillSwitchControl(
    IRiskManagementService risk,
    IPaperTradingService paper,
    StrategyRunControl runs,
    StrategyProcessRegistry registry,
    TradingDbContext db)
{
    public sealed record Result(int StoppedRuns, long[] RemainingRuns, int OpenPositions)
    {
        public bool Complete => RemainingRuns.Length == 0 && OpenPositions == 0;
    }

    public async Task<Result> ActivateAsync(string? actor, string? reason, CancellationToken cancellationToken)
    {
        await registry.StartGate.WaitAsync(cancellationToken);
        try
        {
            await risk.ActivateKillSwitchAsync(actor, reason, cancellationToken);
            // Once persisted, finishing the stop must outlive a disconnected browser.
            return await StopAsync(actor ?? "admin");
        }
        finally { registry.StartGate.Release(); }
    }

    public async Task DeactivateAsync(string? actor, string? reason, CancellationToken cancellationToken)
    {
        await registry.StartGate.WaitAsync(cancellationToken);
        try { await risk.DeactivateKillSwitchAsync(actor, reason, cancellationToken); }
        finally { registry.StartGate.Release(); }
    }

    /// <summary>Finish a persisted stop after an interrupted API lifetime without changing its audit attribution.</summary>
    public async Task<Result?> ResumeAsync(CancellationToken cancellationToken)
    {
        await registry.StartGate.WaitAsync(cancellationToken);
        try
        {
            var state = await risk.GetKillSwitchStateAsync(cancellationToken);
            return state.IsActive ? await StopAsync(state.UpdatedBy ?? "kill-switch") : null;
        }
        finally { registry.StartGate.Release(); }
    }

    private async Task<Result> StopAsync(string actor)
    {
        const string reason = "Global kill switch activated";
        int stopped = await runs.StopAllAsync(reason, flatten: true, by: actor, CancellationToken.None);
        // The registry may have missed a surviving runner. Verify its recorded PID
        // before adopting/stopping it; an unverifiable process remains a visible failure.
        var reconciled = await runs.ReconcileOrphanedRunsAsync(CancellationToken.None);
        stopped += reconciled.Closed;
        stopped += await runs.StopAllAsync(reason, flatten: true, by: actor, CancellationToken.None);
        // Also square off manual books and any positions left by a failed run stop.
        await paper.FlattenAllPositionsAsync(CancellationToken.None);
        var openRows = await db.SimulationRuns.AsNoTracking()
            .Where(x => x.Mode == StrategyRunControl.LivePaperMode
                && (x.Status == StrategyRunControl.RunStatusRunning || x.Status == StrategyRunControl.RunStatusStopping)
                && x.StrategyName != ManualOrdersController.BookStrategyName)
            .Select(x => x.Id).ToListAsync(CancellationToken.None);
        var remaining = openRows.Concat(registry.List().Select(x => x.RunId)).Distinct().Order().ToArray();
        int positions = await db.PaperPositions.CountAsync(x => x.Status == "Open", CancellationToken.None);
        return new Result(stopped, remaining, positions);
    }
}
