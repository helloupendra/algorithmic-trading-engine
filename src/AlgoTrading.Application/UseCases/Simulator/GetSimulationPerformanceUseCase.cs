// src/AlgoTrading.Application/UseCases/Simulator/GetSimulationPerformanceUseCase.cs// src/AlgoTrading.Application.UseCases.Simulator;

using AlgoTrading.Application.Interfaces;


public class GetSimulationPerformanceUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    public GetSimulationPerformanceUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    public Task<PerformanceMetricsResponse> ExecuteAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.GetPerformanceMetricsAsync(simulationRunId, cancellationToken);
    }
}

