// src/AlgoTrading.Application/UseCases/Simulator/GetSimulationEquityCurveUseCase.cs// src/AlgoTradingusing AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

public class GetSimulationEquityCurveUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    public GetSimulationEquityCurveUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    public Task<IReadOnlyList<SimulationEquitySnapshotResponse>> ExecuteAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.GetEquityCurveAsync(simulationRunId, cancellationToken);
    }
}
