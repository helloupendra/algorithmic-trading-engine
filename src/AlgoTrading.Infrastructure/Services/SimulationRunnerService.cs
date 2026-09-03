// src/AlgoTrading.Infrastructure/Services/SimulationRunnerService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Kept for the POST /api/Simulator/runs/{id}/start route. Backtests are driven
/// by the Python backtest runner (tools/backtest_runner.py) launched through
/// POST /api/Backtest/runs, so this in-process frame counter no longer runs:
/// starting a run here would mark it Completed with no positions, which is a
/// silent failure. It answers with a 400 pointing at the real entry point.
/// </summary>
public class SimulationRunnerService : ISimulationRunner
{
    public const string RedirectMessage = "Backtests start via POST /api/Backtest/runs";

    private readonly TradingDbContext _dbContext;

    public SimulationRunnerService(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<StartSimulationRunResponse> StartRunAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {runId} was not found.");

        throw new InvalidOperationException(RedirectMessage);
    }
}
