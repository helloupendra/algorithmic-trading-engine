
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for retrieving detailed metadata for a specific simulation run.
/// </summary>
public class GetSimulationRunUseCase
{
    private readonly ISimulationService _simulationService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetSimulationRunUseCase"/>.
    /// </summary>
    public GetSimulationRunUseCase(ISimulationService simulationService)
    {
        _simulationService = simulationService;
    }

    /// <summary>
    /// Fetches the metadata for the specified run ID.
    /// </summary>
    public Task<SimulationRunResponse?> ExecuteAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        return _simulationService.GetRunAsync(id, cancellationToken);
    }
}
