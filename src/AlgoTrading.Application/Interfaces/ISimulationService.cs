using AlgoTrading.Contracts.Simulator;
using System;
using System.Collections.Generic;
using System.Text;


namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Service interface for managing the lifecycle and metadata of simulation (backtest or paper) runs.
/// </summary>
public interface ISimulationService
{
    /// <summary>
    /// Instantiates a new simulation run in the database in a 'Pending' state.
    /// </summary>
    Task<SimulationRunResponse> CreateRunAsync(
        CreateSimulationRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves metadata for a specific run.
    /// </summary>
    Task<SimulationRunResponse?> GetRunAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all historical and active simulation runs.
    /// </summary>
    Task<IReadOnlyList<SimulationRunResponse>> GetRunsAsync(
        long? userId = null,
        CancellationToken cancellationToken = default);
}

