// src/AlgoTrading.Application/UseCases/Simulator/GetSimulationSignalsUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for fetching all raw signals emitted by a strategy during a specific simulation run.
/// </summary>
public class GetSimulationSignalsUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetSimulationSignalsUseCase"/>.
    /// </summary>
    public GetSimulationSignalsUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    /// <summary>
    /// Fetches the simulation signals.
    /// </summary>
    public Task<IReadOnlyList<SimulationSignalResponse>> ExecuteAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.GetSignalsAsync(simulationRunId, cancellationToken);
    }
}