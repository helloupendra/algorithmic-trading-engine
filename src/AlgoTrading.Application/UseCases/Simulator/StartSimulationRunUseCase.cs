
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for instructing the execution engine to begin processing a pending simulation run.
/// </summary>
public class StartSimulationRunUseCase
{
    private readonly ISimulationRunner _simulationRunner;

    /// <summary>
    /// Initializes a new instance of <see cref="StartSimulationRunUseCase"/>.
    /// </summary>
    public StartSimulationRunUseCase(ISimulationRunner simulationRunner)
    {
        _simulationRunner = simulationRunner;
    }

    /// <summary>
    /// Starts the simulation execution.
    /// </summary>
    public Task<StartSimulationRunResponse> ExecuteAsync(
        long runId,
        CancellationToken cancellationToken = default)
    {
        return _simulationRunner.StartRunAsync(runId, cancellationToken);
    }
}
