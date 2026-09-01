// src/AlgoTrading.Application/UseCases/Simulator/GetSimulationRunsUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for retrieving a list of all historical and pending simulation runs.
/// </summary>
public class GetSimulationRunsUseCase
{
    private readonly ISimulationService _simulationService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetSimulationRunsUseCase"/>.
    /// </summary>
    public GetSimulationRunsUseCase(ISimulationService simulationService)
    {
        _simulationService = simulationService;
    }

    /// <summary>
    /// Fetches the list of simulation runs.
    /// </summary>
    public Task<IReadOnlyList<SimulationRunResponse>> ExecuteAsync(
        long? userId = null,
        CancellationToken cancellationToken = default)
    {
        return _simulationService.GetRunsAsync(userId, cancellationToken);
    }
}