// src/AlgoTrading.Application/UseCases/Simulator/GetPaperPositionsUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for querying open and closed virtual positions for a specific simulation run.
/// </summary>
public class GetPaperPositionsUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetPaperPositionsUseCase"/>.
    /// </summary>
    public GetPaperPositionsUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    /// <summary>
    /// Fetches the paper positions.
    /// </summary>
    public Task<IReadOnlyList<PaperPositionResponse>> ExecuteAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.GetPaperPositionsAsync(simulationRunId, cancellationToken);
    }
}
