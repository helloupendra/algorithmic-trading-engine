// src/AlgoTrading.Application/Interfaces/IPaperTradingService.cs
using AlgoTrading.Contracts.Simulator;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Service interface managing the simulated broker layer. Handles signals, orders, positions, and PnL calculation.
/// </summary>
public interface IPaperTradingService
{
    /// <summary>
    /// Processes a new trading signal (e.g., ENTRY, EXIT) and converts it into paper orders.
    /// </summary>
    Task<SimulationSignalResponse> CreateSignalAsync(
        CreateSimulationSignalRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all raw signals generated during a specific simulation run.
    /// </summary>
    Task<IReadOnlyList<SimulationSignalResponse>> GetSignalsAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all paper orders executed during a specific simulation run.
    /// </summary>
    Task<IReadOnlyList<PaperOrderResponse>> GetPaperOrdersAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current open and closed positions for a specific simulation run.
    /// </summary>
    Task<IReadOnlyList<PaperPositionResponse>> GetPaperPositionsAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a summary of the portfolio performance (Initial Capital, Equity, Drawdown) for the run.
    /// </summary>
    Task<SimulationPortfolioResponse> GetPortfolioSummaryAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default);


    Task<SimulationPortfolioResponse> RefreshPortfolioMarkToMarketAsync(
            long simulationRunId,
            CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimulationEquitySnapshotResponse>> GetEquityCurveAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default);

    Task<PerformanceMetricsResponse> GetPerformanceMetricsAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default);

    Task FlattenAllPositionsAsync(CancellationToken cancellationToken = default);

}