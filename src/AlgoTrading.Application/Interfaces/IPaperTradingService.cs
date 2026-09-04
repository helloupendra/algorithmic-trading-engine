// src/AlgoTrading.Application/Interfaces/IPaperTradingService.cs
using AlgoTrading.Contracts.Backtest;
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

    /// <summary>
    /// Squares off every open position of every run (global kill switch).
    /// Delegates to <see cref="FlattenRunAsync"/> per run.
    /// </summary>
    Task FlattenAllPositionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Squares off every open position of one run at the latest live quote
    /// (falling back to the last mark price, then the entry price), recording a
    /// CLOSE_GROUP signal per group whose metadata carries <paramref name="reason"/>.
    /// Returns the number of positions closed.
    /// </summary>
    Task<int> FlattenRunAsync(
        long simulationRunId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Squares off a chosen subset of the run's open positions (by position id)
    /// on the same fill path as <see cref="FlattenRunAsync"/>: one CLOSE_GROUP
    /// signal per group carrying { reason, by }, reduce-only closing legs at
    /// the latest live quote (falling back to the last mark, then the entry).
    /// Ids that are unknown or already closed are skipped. Returns the number
    /// of positions closed.
    /// </summary>
    Task<int> ClosePositionsAsync(
        long simulationRunId,
        IEnumerable<long> positionIds,
        string reason,
        string by,
        CancellationToken cancellationToken = default);

    // ---- OfflineReplay (backtest runner) hooks ----

    /// <summary>
    /// Bulk-inserts equity snapshots with the GIVEN historical SnapshotUtc.
    /// OfflineReplay runs only. Returns the number inserted.
    /// </summary>
    Task<int> AddEquitySnapshotsAsync(
        long simulationRunId,
        IReadOnlyList<EquitySnapshotBatchItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets LastMarkPrice and recomputes UnrealizedPnl (lots x lotSize) for the
    /// run's open positions from bar-close prices, stamping UpdatedUtc = atUtc.
    /// Returns the number of positions updated.
    /// </summary>
    Task<int> ApplyMarksAsync(
        long simulationRunId,
        RunMarksRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes an OfflineReplay run from the runner: Status (Completed | Failed),
    /// CompletedUtc, LastError and a BACKTEST_SUMMARY signal carrying the
    /// runner's summary object. A run already marked Stopped keeps that status.
    /// </summary>
    Task CompleteRunAsync(
        long simulationRunId,
        CompleteRunRequest request,
        CancellationToken cancellationToken = default);
}