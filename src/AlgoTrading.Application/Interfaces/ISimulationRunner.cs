
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Interface that acts as a bridge to the Python strategy engine.
/// Implementations handle the actual spawning of processes or API calls to run strategies.
/// </summary>
public interface ISimulationRunner
{
    /// <summary>
    /// Triggers the background execution of a pre-configured simulation run.
    /// </summary>
    Task<StartSimulationRunResponse> StartRunAsync(
        long runId,
        CancellationToken cancellationToken = default);
}
