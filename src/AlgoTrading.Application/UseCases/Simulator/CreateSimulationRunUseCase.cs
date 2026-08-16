using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.UseCases.Simulator
{

    /// <summary>
    /// Use case for initializing a new simulation or paper trading run.
    /// </summary>
    public class CreateSimulationRunUseCase
    {
        private readonly ISimulationService _simulationService;

        /// <summary>
        /// Initializes a new instance of <see cref="CreateSimulationRunUseCase"/>.
        /// </summary>
        public CreateSimulationRunUseCase(ISimulationService simulationService)
        {
            _simulationService = simulationService;
        }

        /// <summary>
        /// Persists the run configuration to the database and returns its metadata.
        /// </summary>
        public Task<SimulationRunResponse> ExecuteAsync(
            CreateSimulationRunRequest request,
            CancellationToken cancellationToken = default)
        {
            return _simulationService.CreateRunAsync(request, cancellationToken);
        }
    }

}
