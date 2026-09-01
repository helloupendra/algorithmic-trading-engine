// src/AlgoTrading.Infrastructure/Services/SimulationRunnerService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Execution engine for backtesting. Iterates through historical data frames and pushes them to strategies.
/// Supports running replay sessions offline using previously fetched local database bars.
/// </summary>
public class SimulationRunnerService : ISimulationRunner
{
    private readonly TradingDbContext _dbContext;
    private readonly IReplayFeedProvider _replayFeedProvider;

    public SimulationRunnerService(
        TradingDbContext dbContext,
        IReplayFeedProvider replayFeedProvider)
    {
        _dbContext = dbContext;
        _replayFeedProvider = replayFeedProvider;
    }

    public async Task<StartSimulationRunResponse> StartRunAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {runId} was not found.");

        if (!string.Equals(run.Mode, "OfflineReplay", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only OfflineReplay mode is supported in this first replay runner version.");

        if (!run.FromUtc.HasValue || !run.ToUtc.HasValue)
            throw new InvalidOperationException("OfflineReplay run requires FromUtc and ToUtc.");

        if (run.Status == "Running")
            throw new InvalidOperationException("Simulation run is already running.");

        run.Status = "Running";
        run.StartedUtc = DateTime.UtcNow;
        run.LastError = string.Empty;

        await _dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var frames = await _replayFeedProvider.LoadBarsAsync(
                run.Symbol,
                run.Resolution,
                run.FromUtc.Value,
                run.ToUtc.Value,
                cancellationToken);

            if (frames.Count == 0)
            {
                run.Status = "Failed";
                run.CompletedUtc = DateTime.UtcNow;
                run.LastError = "No replay bars found for the requested symbol/range.";
                await _dbContext.SaveChangesAsync(cancellationToken);

                return new StartSimulationRunResponse
                {
                    RunId = run.Id,
                    Status = run.Status,
                    FramesProcessed = 0,
                    Message = run.LastError
                };
            }

            // First version: just iterate in order and count frames.
            int processed = 0;
            DateTime? firstFrameUtc = null;
            DateTime? lastFrameUtc = null;

            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (firstFrameUtc is null)
                    firstFrameUtc = frame.TimestampUtc;

                lastFrameUtc = frame.TimestampUtc;
                processed++;

                // Future: strategy execution hook goes here
                // e.g. _strategyEngine.OnBar(frame, run, cancellationToken);
            }

            run.Status = "Completed";
            run.CompletedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            return new StartSimulationRunResponse
            {
                RunId = run.Id,
                Status = run.Status,
                FramesProcessed = processed,
                FirstFrameUtc = firstFrameUtc,
                LastFrameUtc = lastFrameUtc,
                Message = "Replay completed successfully."
            };
        }
        catch (Exception ex)
        {
            run.Status = "Failed";
            run.CompletedUtc = DateTime.UtcNow;
            run.LastError = ex.Message;
            await _dbContext.SaveChangesAsync(cancellationToken);
            throw;
        }
    }
}