// src/AlgoTrading.Infrastructure/Services/SimulationService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Orchestrates the lifecycle of simulation and backtest runs (Create, List, Retrieve).
/// Validates symbols and enforces session rules depending on the requested mode (LivePaper vs OfflineReplay).
/// </summary>
public class SimulationService : ISimulationService
{
    private readonly TradingDbContext _dbContext;
    private readonly IMarketSessionService _marketSessionService;

    public SimulationService(
        TradingDbContext dbContext,
        IMarketSessionService marketSessionService)
    {
        _dbContext = dbContext;
        _marketSessionService = marketSessionService;
    }

    public async Task<SimulationRunResponse> CreateRunAsync(
        CreateSimulationRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Mode))
            throw new ArgumentException("Mode is required.", nameof(request.Mode));

        if (string.IsNullOrWhiteSpace(request.Symbol))
            throw new ArgumentException("Symbol is required.", nameof(request.Symbol));

        if (string.IsNullOrWhiteSpace(request.Resolution))
            throw new ArgumentException("Resolution is required.", nameof(request.Resolution));

        var mode = request.Mode.Trim();
        if (mode != "LivePaper" && mode != "OfflineReplay")
            throw new ArgumentException("Mode must be either 'LivePaper' or 'OfflineReplay'.", nameof(request.Mode));

        // Validate symbol exists in instruments
        var instrumentExists = await _dbContext.Instruments
            .AsNoTracking()
            .AnyAsync(x => x.Symbol == request.Symbol && x.IsEnabled, cancellationToken);

        if (!instrumentExists)
            throw new InvalidOperationException($"Instrument '{request.Symbol}' was not found or is not enabled.");

        // Optional guard for LivePaper mode
        if (mode == "LivePaper")
        {
            // TEMPORARY BYPASS FOR HISTORICAL REPLAY TESTING
            // bool marketOpen = _marketSessionService.IsMarketOpen(DateTime.UtcNow, "NSE", "CM");
            // if (!marketOpen)
            // {
            //     throw new InvalidOperationException(
            //         "Market is currently closed. Use OfflineReplay mode or wait until market opens.");
            // }
        }

        if (mode == "OfflineReplay")
        {
            if (!request.FromUtc.HasValue || !request.ToUtc.HasValue)
                throw new InvalidOperationException("OfflineReplay requires FromUtc and ToUtc.");

            if (request.FromUtc > request.ToUtc)
                throw new InvalidOperationException("FromUtc cannot be greater than ToUtc.");
        }

        var entity = new SimulationRun
        {
            Mode = mode,
            Symbol = request.Symbol,
            Resolution = request.Resolution,
            FromUtc = request.FromUtc,
            ToUtc = request.ToUtc,
            ReplaySpeed = request.ReplaySpeed,
            Status = "Pending",
            StrategyName = request.StrategyName,
            ParametersJson = string.IsNullOrWhiteSpace(request.ParametersJson) ? "{}" : request.ParametersJson,
            CreatedUtc = DateTime.UtcNow,
            InitialCapital = request.InitialCapital > 0 ? request.InitialCapital : 1000000m,
            UserId = request.UserId
        };

        await _dbContext.SimulationRuns.AddAsync(entity, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Map(entity);
    }

    public async Task<SimulationRunResponse?> GetRunAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<SimulationRunResponse>> GetRunsAsync(
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.SimulationRuns.AsNoTracking();
        
        if (userId.HasValue)
        {
            query = query.Where(x => x.UserId == userId.Value);
        }

        var rows = await query
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        return rows.Select(Map).ToList();
    }

    private static SimulationRunResponse Map(SimulationRun row)
    {
        return new SimulationRunResponse
        {
            Id = row.Id,
            Mode = row.Mode,
            Symbol = row.Symbol,
            Resolution = row.Resolution,
            FromUtc = row.FromUtc,
            ToUtc = row.ToUtc,
            ReplaySpeed = row.ReplaySpeed,
            Status = row.Status,
            StrategyName = row.StrategyName,
            ParametersJson = row.ParametersJson,
            CreatedUtc = row.CreatedUtc,
            StartedUtc = row.StartedUtc,
            CompletedUtc = row.CompletedUtc,
            LastError = row.LastError,
            InitialCapital = row.InitialCapital,
            UserId = row.UserId
        };
    }
}