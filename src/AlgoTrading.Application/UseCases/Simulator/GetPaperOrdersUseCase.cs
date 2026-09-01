// src/AlgoTrading.Application/UseCases/Simulator/GetPaperOrdersUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for querying all paper trading orders generated within a specific simulation run.
/// </summary>
public class GetPaperOrdersUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetPaperOrdersUseCase"/>.
    /// </summary>
    public GetPaperOrdersUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    /// <summary>
    /// Fetches the paper orders.
    /// </summary>
    public Task<IReadOnlyList<PaperOrderResponse>> ExecuteAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.GetPaperOrdersAsync(simulationRunId, cancellationToken);
    }
}