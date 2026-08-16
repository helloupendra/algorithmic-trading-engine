// src/AlgoTrading.Application/UseCases/Simulator/RefreshSimulationPortfolioUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

public class RefreshSimulationPortfolioUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    public RefreshSimulationPortfolioUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    public Task<SimulationPortfolioResponse> ExecuteAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.RefreshPortfolioMarkToMarketAsync(
            simulationRunId,
            cancellationToken);
    }
}