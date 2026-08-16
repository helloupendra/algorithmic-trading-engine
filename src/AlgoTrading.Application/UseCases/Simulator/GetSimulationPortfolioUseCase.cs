// src/AlgoTrading.Application/UseCases/Simulator/GetSimulationPortfolioUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.UseCases.Simulator;

/// <summary>
/// Use case for calculating the high-level performance metrics (equity, PnL) of a simulation run.
/// </summary>
public class GetSimulationPortfolioUseCase
{
    private readonly IPaperTradingService _paperTradingService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetSimulationPortfolioUseCase"/>.
    /// </summary>
    public GetSimulationPortfolioUseCase(IPaperTradingService paperTradingService)
    {
        _paperTradingService = paperTradingService;
    }

    /// <summary>
    /// Fetches the portfolio summary.
    /// </summary>
    public Task<SimulationPortfolioResponse> ExecuteAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        return _paperTradingService.GetPortfolioSummaryAsync(simulationRunId, cancellationToken);
    }
}