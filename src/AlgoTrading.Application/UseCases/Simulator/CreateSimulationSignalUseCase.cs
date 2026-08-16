// src/AlgoTrading.Application/UseCases/Simulator/CreateSimulationSignalUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for recording a new trading signal emitted by a strategy during simulation.
/// </summary>
public class CreateSimulationSignalUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    /// <summary>
    /// Initializes a new instance of <see cref="CreateSimulationSignalUseCase"/>.
    /// </summary>
    public CreateSimulationSignalUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    /// <summary>
    /// Executes the signal processing, potentially generating paper orders.
    /// </summary>
    public Task<SimulationSignalResponse> ExecuteAsync(
        CreateSimulationSignalRequest request,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.CreateSignalAsync(request, cancellationToken);
    }
}