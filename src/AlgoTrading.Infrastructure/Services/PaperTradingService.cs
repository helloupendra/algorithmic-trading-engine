// src/AlgoTrading.Infrastructure/Services/PaperTradingService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Backtest;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Manages paper-trading state for simulation runs:
/// - persists strategy signals
/// - converts signals into paper orders
/// - creates/updates paper positions
/// - calculates portfolio summary / MTM / equity curve / performance metrics
///
/// OfflineReplay (backtest) runs are clocked by the signal's TimestampUtc, never
/// the wall clock, skip the live risk gate (the runner enforces SL/target) and
/// are never marked to market from LiveQuotesLatest: the runner keeps
/// LastMarkPrice / UnrealizedPnl current through <see cref="ApplyMarksAsync"/>.
/// </summary>
public class PaperTradingService : IPaperTradingService
{
    public const string LivePaperMode = "LivePaper";
    public const string OfflineReplayMode = "OfflineReplay";
    public const string BacktestSummarySignalType = "BACKTEST_SUMMARY";

    private const string RunStatusStopping = "Stopping";
    private const string RunStatusStopped = "Stopped";
    private const string RunStatusCompleted = "Completed";
    private const string RunStatusFailed = "Failed";

    private const int MaxEquitySnapshotBatch = 5000;

    private readonly TradingDbContext _dbContext;
    private readonly IRiskManagementService _riskManagementService;
    private readonly ILotSizeResolver _lotSizeResolver;

    public PaperTradingService(
        TradingDbContext dbContext,
        IRiskManagementService riskManagementService,
        ILotSizeResolver lotSizeResolver)
    {
        _dbContext = dbContext;
        _riskManagementService = riskManagementService;
        _lotSizeResolver = lotSizeResolver;
    }

    public static bool IsReplay(string? mode)
        => string.Equals(mode, OfflineReplayMode, StringComparison.OrdinalIgnoreCase);

    private static bool IsClosedStatus(string? status)
        => status is RunStatusStopping or RunStatusStopped or RunStatusCompleted or RunStatusFailed;

    // ---------------------------------------------------------------------
    // SIGNALS
    // ---------------------------------------------------------------------

    public async Task<SimulationSignalResponse> CreateSignalAsync(
        CreateSimulationSignalRequest request,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(x => x.Id == request.SimulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {request.SimulationRunId} was not found.");

        // A run that is being (or has been) stopped, or that has finished, no
        // longer accepts signals: the runner may still be posting while the API
        // squares off its positions, and a late OPEN/CLOSE_GROUP would open an
        // ownerless or reversed position on a closed run.
        if (IsClosedStatus(run.Status))
        {
            throw new InvalidOperationException(
                $"Simulation run {request.SimulationRunId} is {run.Status.ToLowerInvariant()}; the {request.SignalType} signal was rejected.");
        }

        bool replay = IsReplay(run.Mode);
        var timestampUtc = request.TimestampUtc.ToUniversalTime();

        var signal = new SimulationSignal
        {
            SimulationRunId = request.SimulationRunId,
            StrategyName = request.StrategyName,
            SignalType = request.SignalType,
            TimestampUtc = timestampUtc,
            GroupId = request.GroupId,
            MetadataJson = string.IsNullOrWhiteSpace(request.MetadataJson) ? "{}" : request.MetadataJson,
            CreatedUtc = DateTime.UtcNow
        };

        await _dbContext.SimulationSignals.AddAsync(signal, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Convert signal -> orders -> positions. Replays are clocked by the bar
        // time and skip the wall-clock risk gate (rate limit / daily loss).
        if (request.Legs is not null && request.Legs.Count > 0)
        {
            DateTime? clock = replay ? timestampUtc : null;
            foreach (var leg in request.Legs)
            {
                await CreateOrderAndApplyPositionAsync(signal, leg, cancellationToken, bypassRiskCheck: replay, reduceOnly: false, atUtc: clock);
            }
        }

        return MapSignal(signal);
    }

    public async Task<IReadOnlyList<SimulationSignalResponse>> GetSignalsAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.SimulationSignals
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .OrderBy(x => x.TimestampUtc)
            .ToListAsync(cancellationToken);

        return rows.Select(MapSignal).ToList();
    }

    // ---------------------------------------------------------------------
    // ORDERS
    // ---------------------------------------------------------------------

    public async Task<IReadOnlyList<PaperOrderResponse>> GetPaperOrdersAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.PaperOrders
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .OrderByDescending(x => x.CreatedUtc)
            .ToListAsync(cancellationToken);

        return rows.Select(MapOrder).ToList();
    }

    // ---------------------------------------------------------------------
    // POSITIONS
    // ---------------------------------------------------------------------

    public async Task<IReadOnlyList<PaperPositionResponse>> GetPaperPositionsAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.PaperPositions
            .Where(x => x.SimulationRunId == simulationRunId)
            .OrderByDescending(x => x.OpenedUtc)
            .ToListAsync(cancellationToken);

        // Mark-to-market open positions using the latest live quote. Replays keep
        // the marks the runner stored (bar closes), never today's LTP.
        var symbols = rows
            .Where(x => x.Status == "Open")
            .Select(x => x.Symbol)
            .Distinct()
            .ToList();

        if (symbols.Count > 0 && !await IsReplayRunAsync(simulationRunId, cancellationToken))
        {
            var latestQuotes = await LoadLiveQuotesAsync(symbols, cancellationToken);
            var lotSizes = await _lotSizeResolver.ResolveManyAsync(symbols, cancellationToken);

            foreach (var pos in rows.Where(x => x.Status == "Open"))
            {
                if (latestQuotes.TryGetValue(pos.Symbol, out var lastPrice) && lastPrice.HasValue)
                {
                    int lotSize = LotSizeOf(lotSizes, pos.Symbol);
                    pos.LastMarkPrice = lastPrice.Value;
                    pos.UnrealizedPnl = CalculateUnrealizedPnl(
                        pos.Direction,
                        pos.AveragePrice,
                        lastPrice.Value,
                        pos.Quantity,
                        lotSize);

                    pos.UpdatedUtc = DateTime.UtcNow;
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return rows.Select(MapPosition).ToList();
    }

    // ---------------------------------------------------------------------
    // PORTFOLIO SUMMARY
    // ---------------------------------------------------------------------

    public async Task<SimulationPortfolioResponse> GetPortfolioSummaryAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        // Read-only path (the risk guard polls it every few seconds per run):
        // the mark-to-market below is computed on the in-memory rows and never
        // saved, so there is nothing for the change tracker to do.
        var positions = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        var orders = await _dbContext.PaperOrders
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        var lotSizes = await _lotSizeResolver.ResolveManyAsync(
            positions.Select(x => x.Symbol), cancellationToken);

        if (!IsReplay(run.Mode))
        {
            var symbols = positions
                .Where(x => x.Status == "Open")
                .Select(x => x.Symbol)
                .Distinct()
                .ToList();

            var latestQuotes = await LoadLiveQuotesAsync(symbols, cancellationToken);
            MarkOpenPositions(positions, latestQuotes, lotSizes, DateTime.UtcNow);
        }

        return BuildPortfolio(run, positions, orders, lotSizes);
    }

    // ---------------------------------------------------------------------
    // MTM REFRESH + SNAPSHOT
    // ---------------------------------------------------------------------

    public async Task<SimulationPortfolioResponse> RefreshPortfolioMarkToMarketAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        var positions = await _dbContext.PaperPositions
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        var orders = await _dbContext.PaperOrders
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        var lotSizes = await _lotSizeResolver.ResolveManyAsync(
            positions.Select(x => x.Symbol), cancellationToken);

        bool replay = IsReplay(run.Mode);

        if (!replay)
        {
            var symbols = positions
                .Where(x => x.Status == "Open")
                .Select(x => x.Symbol)
                .Distinct()
                .ToList();

            var latestQuotes = await LoadLiveQuotesAsync(symbols, cancellationToken);
            MarkOpenPositions(positions, latestQuotes, lotSizes, DateTime.UtcNow);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        var portfolio = BuildPortfolio(run, positions, orders, lotSizes);

        // A replay's equity curve carries historical timestamps only (posted by
        // the runner); a wall-clock snapshot would land after the last bar and
        // distort drawdown, so it is not written.
        if (!replay)
        {
            var snapshot = new SimulationEquitySnapshot
            {
                SimulationRunId = run.Id,
                SnapshotUtc = DateTime.UtcNow,
                InitialCapital = run.InitialCapital,
                UsedCapital = portfolio.UsedCapital,
                AvailableCapital = portfolio.AvailableCapital,
                RealizedPnl = portfolio.RealizedPnl,
                UnrealizedPnl = portfolio.UnrealizedPnl,
                TotalPnl = portfolio.TotalPnl,
                CurrentEquity = portfolio.CurrentEquity,
                OpenPositions = portfolio.OpenPositions,
                ClosedPositions = portfolio.ClosedPositions
            };

            await _dbContext.SimulationEquitySnapshots.AddAsync(snapshot, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return portfolio;
    }

    // ---------------------------------------------------------------------
    // EQUITY CURVE
    // ---------------------------------------------------------------------

    public async Task<IReadOnlyList<SimulationEquitySnapshotResponse>> GetEquityCurveAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.SimulationEquitySnapshots
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .OrderBy(x => x.SnapshotUtc)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new SimulationEquitySnapshotResponse
        {
            SnapshotUtc = x.SnapshotUtc,
            InitialCapital = x.InitialCapital,
            UsedCapital = x.UsedCapital,
            AvailableCapital = x.AvailableCapital,
            RealizedPnl = x.RealizedPnl,
            UnrealizedPnl = x.UnrealizedPnl,
            TotalPnl = x.TotalPnl,
            CurrentEquity = x.CurrentEquity,
            OpenPositions = x.OpenPositions,
            ClosedPositions = x.ClosedPositions
        }).ToList();
    }

    // ---------------------------------------------------------------------
    // PERFORMANCE METRICS
    // ---------------------------------------------------------------------

    public async Task<PerformanceMetricsResponse> GetPerformanceMetricsAsync(
        long simulationRunId,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        var positions = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId && x.Status == "Closed")
            .ToListAsync(cancellationToken);

        var snapshots = await _dbContext.SimulationEquitySnapshots
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .OrderBy(x => x.SnapshotUtc)
            .ToListAsync(cancellationToken);

        decimal currentEquity = snapshots.LastOrDefault()?.CurrentEquity ?? run.InitialCapital;
        decimal totalReturnPercent = run.InitialCapital > 0
            ? ((currentEquity - run.InitialCapital) / run.InitialCapital) * 100m
            : 0m;

        decimal peak = decimal.MinValue;
        decimal maxDrawdown = 0m;

        foreach (var snap in snapshots)
        {
            if (snap.CurrentEquity > peak)
                peak = snap.CurrentEquity;

            if (peak > 0)
            {
                decimal dd = ((peak - snap.CurrentEquity) / peak) * 100m;
                if (dd > maxDrawdown)
                    maxDrawdown = dd;
            }
        }

        int totalClosed = positions.Count;
        int winning = positions.Count(x => x.RealizedPnl > 0);
        int losing = positions.Count(x => x.RealizedPnl < 0);

        decimal winRate = totalClosed > 0
            ? ((decimal)winning / totalClosed) * 100m
            : 0m;

        decimal grossProfit = positions.Where(x => x.RealizedPnl > 0).Sum(x => x.RealizedPnl);
        decimal grossLoss = positions.Where(x => x.RealizedPnl < 0).Sum(x => Math.Abs(x.RealizedPnl));

        decimal avgWin = winning > 0
            ? positions.Where(x => x.RealizedPnl > 0).Average(x => x.RealizedPnl)
            : 0m;

        decimal avgLoss = losing > 0
            ? positions.Where(x => x.RealizedPnl < 0).Average(x => Math.Abs(x.RealizedPnl))
            : 0m;

        decimal profitFactor = grossLoss > 0 ? grossProfit / grossLoss : 0m;

        decimal expectancy = totalClosed > 0
            ? positions.Sum(x => x.RealizedPnl) / totalClosed
            : 0m;

        return new PerformanceMetricsResponse
        {
            SimulationRunId = simulationRunId,
            InitialCapital = run.InitialCapital,
            CurrentEquity = currentEquity,
            TotalReturnPercent = totalReturnPercent,
            MaxDrawdownPercent = maxDrawdown,
            TotalClosedPositions = totalClosed,
            WinningPositions = winning,
            LosingPositions = losing,
            WinRatePercent = winRate,
            AverageWin = avgWin,
            AverageLoss = avgLoss,
            GrossProfit = grossProfit,
            GrossLoss = grossLoss,
            ProfitFactor = profitFactor,
            Expectancy = expectancy
        };
    }

    // ---------------------------------------------------------------------
    // RISK MANAGEMENT ACTIONS
    // ---------------------------------------------------------------------

    public async Task FlattenAllPositionsAsync(CancellationToken cancellationToken = default)
    {
        var runIds = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.Status == "Open")
            .Select(x => x.SimulationRunId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var runId in runIds)
        {
            await FlattenRunAsync(runId, "GLOBAL_KILL_SWITCH", cancellationToken);
        }
    }

    public async Task<int> FlattenRunAsync(
        long simulationRunId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        var openPositions = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId && x.Status == "Open")
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        if (openPositions.Count == 0) return 0;

        bool replay = IsReplay(run.Mode);

        // Live runs square off at the latest quote; replays at the last stored
        // bar-close mark, stamped at the time of that mark so the closing rows
        // stay on the historical timeline.
        var latestQuotes = replay
            ? new Dictionary<string, decimal?>()
            : await LoadLiveQuotesAsync(openPositions.Select(x => x.Symbol).Distinct().ToList(), cancellationToken);

        DateTime atUtc = replay
            ? openPositions.Max(x => x.UpdatedUtc)
            : DateTime.UtcNow;

        var metadata = System.Text.Json.JsonSerializer.Serialize(new { reason, system = true });
        int closed = 0;

        // One CLOSE_GROUP signal per group, carrying a closing leg for each open
        // position in that group — the same shape the runner emits when it exits
        // a group itself, so the activity feed reads the same either way.
        foreach (var group in openPositions.GroupBy(x => x.GroupId))
        {
            var signal = new SimulationSignal
            {
                SimulationRunId = simulationRunId,
                StrategyName = run.StrategyName,
                SignalType = "CLOSE_GROUP",
                TimestampUtc = atUtc,
                GroupId = group.Key,
                MetadataJson = metadata,
                CreatedUtc = DateTime.UtcNow
            };

            await _dbContext.SimulationSignals.AddAsync(signal, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            foreach (var pos in group)
            {
                decimal fillPrice =
                    (latestQuotes.TryGetValue(pos.Symbol, out var ltp) && ltp.HasValue ? ltp : null)
                    ?? pos.LastMarkPrice
                    ?? pos.AveragePrice;

                var leg = new SimulationSignalLegRequest
                {
                    Symbol = pos.Symbol,
                    Side = pos.Direction == "LONG" ? "SELL" : "BUY",
                    Quantity = pos.Quantity,
                    Price = fillPrice
                };

                // This IS the risk action, so it bypasses the risk evaluation.
                // Reduce-only: if another stopper closed this position between the
                // snapshot above and now, the closing leg is skipped instead of
                // opening a reverse position on a run that is being stopped.
                bool closedLeg = await CreateOrderAndApplyPositionAsync(
                    signal, leg, cancellationToken, bypassRiskCheck: true, reduceOnly: true, atUtc: replay ? atUtc : null);
                if (closedLeg) closed++;
            }
        }

        return closed;
    }

    // ---------------------------------------------------------------------
    // OFFLINE REPLAY HOOKS (backtest runner)
    // ---------------------------------------------------------------------

    public async Task<int> AddEquitySnapshotsAsync(
        long simulationRunId,
        IReadOnlyList<EquitySnapshotBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count > MaxEquitySnapshotBatch)
            throw new InvalidOperationException($"At most {MaxEquitySnapshotBatch} equity snapshots per request.");

        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        if (!IsReplay(run.Mode))
            throw new InvalidOperationException($"Simulation run {simulationRunId} is a {run.Mode} run; historical equity snapshots are accepted for OfflineReplay runs only.");

        if (items.Count == 0) return 0;

        var rows = items.Select(item =>
        {
            // Net of the charges booked so far, so the curve (and the drawdown
            // read from it) agrees with the run total and the runner's SL rule.
            decimal charges = Math.Max(0m, item.Charges);
            decimal totalPnl = item.RealizedPnl + item.UnrealizedPnl - charges;
            return new SimulationEquitySnapshot
            {
                SimulationRunId = run.Id,
                SnapshotUtc = item.SnapshotUtc.ToUniversalTime(),
                InitialCapital = run.InitialCapital,
                UsedCapital = item.UsedCapital,
                AvailableCapital = run.InitialCapital + item.RealizedPnl - charges - item.UsedCapital,
                RealizedPnl = item.RealizedPnl,
                UnrealizedPnl = item.UnrealizedPnl,
                TotalPnl = totalPnl,
                CurrentEquity = run.InitialCapital + totalPnl,
                OpenPositions = item.OpenPositions,
                ClosedPositions = item.ClosedPositions
            };
        }).ToList();

        await _dbContext.SimulationEquitySnapshots.AddRangeAsync(rows, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    public async Task<int> ApplyMarksAsync(
        long simulationRunId,
        RunMarksRequest request,
        CancellationToken cancellationToken = default)
    {
        var exists = await _dbContext.SimulationRuns
            .AsNoTracking()
            .AnyAsync(x => x.Id == simulationRunId, cancellationToken);

        if (!exists)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        if (request.Marks.Count == 0) return 0;

        var prices = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (var mark in request.Marks)
        {
            if (string.IsNullOrWhiteSpace(mark.Symbol)) continue;
            prices[mark.Symbol.Trim()] = mark.Price;
        }

        var open = await _dbContext.PaperPositions
            .Where(x => x.SimulationRunId == simulationRunId && x.Status == "Open")
            .ToListAsync(cancellationToken);

        var touched = open.Where(x => prices.ContainsKey(x.Symbol)).ToList();
        if (touched.Count == 0) return 0;

        var lotSizes = await ResolveLotSizesForRunAsync(simulationRunId, touched.Select(x => x.Symbol), cancellationToken);
        MarkOpenPositions(touched, prices, lotSizes, request.AtUtc.ToUniversalTime());

        await _dbContext.SaveChangesAsync(cancellationToken);
        return touched.Count;
    }

    public async Task CompleteRunAsync(
        long simulationRunId,
        CompleteRunRequest request,
        CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.SimulationRuns
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        if (!IsReplay(run.Mode))
            throw new InvalidOperationException($"Simulation run {simulationRunId} is a {run.Mode} run; only OfflineReplay runs complete through this endpoint.");

        var status = (request.Status ?? string.Empty).Trim();
        bool completed = string.Equals(status, RunStatusCompleted, StringComparison.OrdinalIgnoreCase);
        bool failed = string.Equals(status, RunStatusFailed, StringComparison.OrdinalIgnoreCase);
        if (!completed && !failed)
            throw new InvalidOperationException("status must be \"Completed\" or \"Failed\".");

        var now = DateTime.UtcNow;

        // A stop that raced the runner's final POST wins: the run stays Stopped.
        if (run.Status != RunStatusStopped)
        {
            run.Status = completed ? RunStatusCompleted : RunStatusFailed;
            run.CompletedUtc ??= now;
            if (failed)
            {
                run.LastError = string.IsNullOrWhiteSpace(request.Error) ? "Backtest runner reported a failure." : request.Error.Trim();
            }
        }
        else if (failed && string.IsNullOrWhiteSpace(run.LastError) && !string.IsNullOrWhiteSpace(request.Error))
        {
            run.LastError = request.Error.Trim();
        }

        string summaryJson = request.Summary is { ValueKind: System.Text.Json.JsonValueKind.Object } summary
            ? summary.GetRawText()
            : "{}";

        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = run.Id,
            StrategyName = run.StrategyName,
            SignalType = BacktestSummarySignalType,
            TimestampUtc = now,
            GroupId = string.Empty,
            MetadataJson = summaryJson,
            CreatedUtc = now
        }, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---------------------------------------------------------------------
    // INTERNAL ORDER / POSITION APPLICATION
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fills one leg as a paper order and applies it to the run's positions.
    /// With <paramref name="reduceOnly"/> the leg may only shrink or close an
    /// existing open position in its group: quantity is clamped to what is open
    /// and, when nothing is open (already closed by a concurrent stop), the leg
    /// is skipped and <c>false</c> is returned. <paramref name="atUtc"/> is the
    /// historical clock for replays; null means the wall clock.
    /// </summary>
    private async Task<bool> CreateOrderAndApplyPositionAsync(
        SimulationSignal signal,
        SimulationSignalLegRequest leg,
        CancellationToken cancellationToken,
        bool bypassRiskCheck = false,
        bool reduceOnly = false,
        DateTime? atUtc = null)
    {
        if (string.IsNullOrWhiteSpace(leg.Symbol))
            throw new InvalidOperationException("Paper leg symbol is required.");

        if (string.IsNullOrWhiteSpace(leg.Side))
            throw new InvalidOperationException("Paper leg side is required.");

        if (leg.Quantity <= 0)
            throw new InvalidOperationException("Paper leg quantity must be greater than zero.");

        string normalizedSide = leg.Side.Trim().ToUpperInvariant();
        if (normalizedSide != "BUY" && normalizedSide != "SELL")
            throw new InvalidOperationException("Paper leg side must be BUY or SELL.");

        int quantity = leg.Quantity;
        if (reduceOnly)
        {
            var open = await FindOpenPositionAsync(signal.SimulationRunId, signal.GroupId, leg.Symbol, cancellationToken);
            if (open is null || !IsClosingSide(open.Direction, normalizedSide))
            {
                return false;
            }

            quantity = Math.Min(quantity, open.Quantity);
            if (quantity <= 0)
            {
                return false;
            }
        }

        if (!bypassRiskCheck)
        {
            // RISK MANAGEMENT ENFORCEMENT
            await _riskManagementService.EvaluateOrderAsync(
                signal.SimulationRunId,
                leg.Symbol,
                normalizedSide,
                leg.Quantity,
                cancellationToken);
        }

        decimal fillPrice = leg.Price ?? 0m;
        DateTime clock = atUtc ?? DateTime.UtcNow;

        var order = new PaperOrder
        {
            SimulationRunId = signal.SimulationRunId,
            SimulationSignalId = signal.Id,
            StrategyName = signal.StrategyName,
            GroupId = signal.GroupId,
            Symbol = leg.Symbol,
            Side = normalizedSide,
            Quantity = quantity,
            OrderType = "MARKET_SIM",
            Status = "Filled",
            RequestedPrice = leg.Price,
            FillPrice = fillPrice,
            CreatedUtc = clock,
            FilledUtc = clock
        };

        await _dbContext.PaperOrders.AddAsync(order, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        bool applied = await ApplyPositionAsync(signal, order, cancellationToken, reduceOnly, clock);
        if (!applied)
        {
            // The position vanished between the lookup and the apply (closed by a
            // concurrent stopper). A filled order that moved nothing would only
            // confuse the order history, so drop it.
            _dbContext.PaperOrders.Remove(order);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return applied;
    }

    private Task<PaperPosition?> FindOpenPositionAsync(long runId, string groupId, string symbol, CancellationToken cancellationToken)
        => _dbContext.PaperPositions
            .FirstOrDefaultAsync(x =>
                x.SimulationRunId == runId &&
                x.GroupId == groupId &&
                x.Symbol == symbol &&
                x.Status == "Open",
                cancellationToken);

    private static bool IsClosingSide(string direction, string side)
        => (direction == "LONG" && side == "SELL") || (direction == "SHORT" && side == "BUY");

    /// <summary>
    /// Applies a filled order to the run's open position for (group, symbol).
    /// Returns <c>false</c> only in reduce-only mode when there is no open
    /// position to reduce; otherwise a fresh position is opened. Every
    /// timestamp written here is <paramref name="clock"/> (bar time for replays).
    /// </summary>
    private async Task<bool> ApplyPositionAsync(
        SimulationSignal signal,
        PaperOrder order,
        CancellationToken cancellationToken,
        bool reduceOnly,
        DateTime clock)
    {
        var existing = await FindOpenPositionAsync(signal.SimulationRunId, signal.GroupId, order.Symbol, cancellationToken);

        if (existing is null)
        {
            if (reduceOnly)
            {
                return false;
            }

            var direction = order.Side == "BUY" ? "LONG" : "SHORT";

            var pos = new PaperPosition
            {
                SimulationRunId = signal.SimulationRunId,
                StrategyName = signal.StrategyName,
                GroupId = signal.GroupId,
                Symbol = order.Symbol,
                Direction = direction,
                Quantity = order.Quantity,
                AveragePrice = order.FillPrice ?? 0m,
                LastMarkPrice = order.FillPrice,
                RealizedPnl = 0m,
                UnrealizedPnl = 0m,
                Status = "Open",
                OpenedUtc = order.FilledUtc ?? clock,
                UpdatedUtc = clock
            };

            await _dbContext.PaperPositions.AddAsync(pos, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        bool sameDirection =
            (existing.Direction == "LONG" && order.Side == "BUY") ||
            (existing.Direction == "SHORT" && order.Side == "SELL");

        if (sameDirection)
        {
            if (reduceOnly)
            {
                // The open position flipped direction under us; adding to it is
                // the opposite of squaring off.
                return false;
            }

            int newQty = existing.Quantity + order.Quantity;
            decimal oldNotional = existing.AveragePrice * existing.Quantity;
            decimal newNotional = (order.FillPrice ?? 0m) * order.Quantity;

            existing.AveragePrice = (oldNotional + newNotional) / newQty;
            existing.Quantity = newQty;
            existing.LastMarkPrice = order.FillPrice;
            existing.UpdatedUtc = clock;

            await _dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }

        int closingQty = Math.Min(existing.Quantity, order.Quantity);
        decimal fillPrice = order.FillPrice ?? 0m;

        var lotSizes = await ResolveLotSizesForRunAsync(signal.SimulationRunId, new[] { existing.Symbol }, cancellationToken);
        int lotSize = LotSizeOf(lotSizes, existing.Symbol);

        decimal realized = CalculateRealizedPnl(
            existing.Direction,
            existing.AveragePrice,
            fillPrice,
            closingQty,
            lotSize);

        existing.RealizedPnl += realized;
        existing.Quantity -= closingQty;
        existing.LastMarkPrice = fillPrice;
        existing.UpdatedUtc = clock;

        if (existing.Quantity == 0)
        {
            existing.Status = "Closed";
            existing.ClosedUtc = clock;
            existing.UnrealizedPnl = 0m;
        }
        else
        {
            existing.UnrealizedPnl = CalculateUnrealizedPnl(existing.Direction, existing.AveragePrice, fillPrice, existing.Quantity, lotSize);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Reduce-only legs never flip into a reverse position, even if the open
        // quantity shrank between the clamp and this apply.
        int remainder = order.Quantity - closingQty;
        if (remainder > 0 && !reduceOnly)
        {
            var reverseDirection = order.Side == "BUY" ? "LONG" : "SHORT";

            var newPos = new PaperPosition
            {
                SimulationRunId = signal.SimulationRunId,
                StrategyName = signal.StrategyName,
                GroupId = signal.GroupId,
                Symbol = order.Symbol,
                Direction = reverseDirection,
                Quantity = remainder,
                AveragePrice = fillPrice,
                LastMarkPrice = fillPrice,
                RealizedPnl = 0m,
                UnrealizedPnl = 0m,
                Status = "Open",
                OpenedUtc = clock,
                UpdatedUtc = clock
            };

            await _dbContext.PaperPositions.AddAsync(newPos, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    // ---------------------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------------------

    private Task<bool> IsReplayRunAsync(long simulationRunId, CancellationToken cancellationToken)
        => _dbContext.SimulationRuns
            .AsNoTracking()
            .Where(x => x.Id == simulationRunId)
            .Select(x => x.Mode == OfflineReplayMode)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Lot sizes to book with. An OfflineReplay run carries the ONE lot size it
    /// was started with in its parametersJson ("lot_size"); the runner's ledger
    /// uses the same number for every contract, so the stored P&amp;L matches
    /// the P&amp;L that drove its stop-loss / target. Live runs (and old replay
    /// rows without the key) resolve per symbol from the instrument master.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveLotSizesForRunAsync(
        long simulationRunId,
        IEnumerable<string> symbols,
        CancellationToken cancellationToken)
    {
        var wanted = symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0) return new Dictionary<string, LotSizeInfo>(StringComparer.Ordinal);

        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .Where(x => x.Id == simulationRunId)
            .Select(x => new { x.Mode, x.ParametersJson })
            .FirstOrDefaultAsync(cancellationToken);

        int? frozen = run is not null && IsReplay(run.Mode) ? ReplayLotSizeOf(run.ParametersJson) : null;
        if (frozen is > 0)
        {
            var fixedSizes = new Dictionary<string, LotSizeInfo>(StringComparer.Ordinal);
            foreach (var symbol in wanted)
            {
                fixedSizes[symbol] = new LotSizeInfo(frozen.Value, "run", UnderlyingCatalog.InferUnderlying(symbol));
            }
            return fixedSizes;
        }

        return await _lotSizeResolver.ResolveManyAsync(wanted, cancellationToken);
    }

    /// <summary>The "lot_size" the backtest was started with, or null when the row predates it.</summary>
    private static int? ReplayLotSizeOf(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("lot_size", out var el)) return null;
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.Number when el.TryGetInt32(out var n) && n > 0 => n,
                System.Text.Json.JsonValueKind.String when int.TryParse(el.GetString(), out var s) && s > 0 => s,
                _ => null
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, decimal?>> LoadLiveQuotesAsync(List<string> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0) return new Dictionary<string, decimal?>(StringComparer.Ordinal);

        var rows = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol))
            .Select(x => new { x.Symbol, x.LastTradedPrice })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[row.Symbol] = row.LastTradedPrice;
        }
        return result;
    }

    /// <summary>Applies mark prices to the open positions that have one; others are left untouched.</summary>
    private static void MarkOpenPositions(
        IEnumerable<PaperPosition> positions,
        IReadOnlyDictionary<string, decimal?> prices,
        IReadOnlyDictionary<string, LotSizeInfo> lotSizes,
        DateTime atUtc)
    {
        foreach (var pos in positions)
        {
            if (pos.Status != "Open") continue;
            if (!prices.TryGetValue(pos.Symbol, out var price) || !price.HasValue) continue;

            int lotSize = LotSizeOf(lotSizes, pos.Symbol);
            pos.LastMarkPrice = price.Value;
            pos.UnrealizedPnl = CalculateUnrealizedPnl(pos.Direction, pos.AveragePrice, price.Value, pos.Quantity, lotSize);
            pos.UpdatedUtc = atUtc;
        }
    }

    private static SimulationPortfolioResponse BuildPortfolio(
        SimulationRun run,
        List<PaperPosition> positions,
        List<PaperOrder> orders,
        IReadOnlyDictionary<string, LotSizeInfo> lotSizes)
    {
        decimal usedCapital = 0m;
        decimal realizedPnl = 0m;
        decimal unrealizedPnl = 0m;

        foreach (var pos in positions)
        {
            // RealizedPnl is already lot-size adjusted at close time.
            realizedPnl += pos.RealizedPnl;

            if (pos.Status == "Open")
            {
                usedCapital += UsedCapitalOf(pos, lotSizes);
                unrealizedPnl += pos.UnrealizedPnl;
            }
        }

        decimal totalPnl = realizedPnl + unrealizedPnl;
        decimal currentEquity = run.InitialCapital + totalPnl;
        decimal availableCapital = run.InitialCapital + realizedPnl - usedCapital;
        decimal returnPercent = run.InitialCapital > 0
            ? (totalPnl / run.InitialCapital) * 100m
            : 0m;

        var groupSummaries = positions
            .GroupBy(x => new { x.GroupId, x.StrategyName })
            .Select(g =>
            {
                var open = g.Where(x => x.Status == "Open").ToList();
                var closed = g.Where(x => x.Status == "Closed").ToList();

                return new PositionGroupSummaryResponse
                {
                    GroupId = g.Key.GroupId,
                    StrategyName = g.Key.StrategyName,
                    OpenPositionCount = open.Count,
                    ClosedPositionCount = closed.Count,
                    UsedCapital = open.Sum(x => UsedCapitalOf(x, lotSizes)),
                    RealizedPnl = g.Sum(x => x.RealizedPnl),
                    UnrealizedPnl = open.Sum(x => x.UnrealizedPnl),
                    Status = open.Count > 0 ? "Open" : "Closed"
                };
            })
            .OrderByDescending(x => x.Status)
            .ThenBy(x => x.GroupId)
            .ToList();

        return new SimulationPortfolioResponse
        {
            SimulationRunId = run.Id,
            StrategyName = run.StrategyName,
            RunStatus = run.Status,

            InitialCapital = run.InitialCapital,
            UsedCapital = usedCapital,
            AvailableCapital = availableCapital,

            RealizedPnl = realizedPnl,
            UnrealizedPnl = unrealizedPnl,
            TotalPnl = totalPnl,

            CurrentEquity = currentEquity,
            ReturnPercent = returnPercent,

            TotalOrders = orders.Count,
            FilledOrders = orders.Count(x => x.Status == "Filled"),

            OpenPositions = positions.Count(x => x.Status == "Open"),
            ClosedPositions = positions.Count(x => x.Status == "Closed"),

            Groups = groupSummaries
        };
    }

    private static decimal UsedCapitalOf(PaperPosition pos, IReadOnlyDictionary<string, LotSizeInfo> lotSizes)
        => pos.Direction == "SHORT"
            ? GetMarginHeuristic(pos.Symbol) * pos.Quantity
            : Math.Abs(pos.AveragePrice * pos.Quantity * LotSizeOf(lotSizes, pos.Symbol));

    private static decimal CalculateRealizedPnl(string direction, decimal avgPrice, decimal exitPrice, int qty, int lotSize)
    {
        return direction == "LONG"
            ? (exitPrice - avgPrice) * qty * lotSize
            : (avgPrice - exitPrice) * qty * lotSize;
    }

    private static decimal CalculateUnrealizedPnl(string direction, decimal avgPrice, decimal markPrice, int qty, int lotSize)
    {
        return direction == "LONG"
            ? (markPrice - avgPrice) * qty * lotSize
            : (avgPrice - markPrice) * qty * lotSize;
    }

    /// <summary>
    /// Lot size from a batch resolved by <see cref="ILotSizeResolver.ResolveManyAsync"/>;
    /// 1 when the symbol was not part of the batch.
    /// </summary>
    private static int LotSizeOf(IReadOnlyDictionary<string, LotSizeInfo> lotSizes, string symbol)
        => lotSizes.TryGetValue(symbol, out var info) && info.LotSize > 0 ? info.LotSize : 1;

    private static decimal GetMarginHeuristic(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return 100000m;
        var s = symbol.ToUpperInvariant();
        if (s.Contains("BANKNIFTY")) return 150000m;
        if (s.Contains("FINNIFTY")) return 100000m;
        if (s.Contains("MIDCPNIFTY")) return 75000m;
        if (s.Contains("NIFTY")) return 120000m;
        if (s.Contains("SENSEX")) return 100000m;
        if (s.Contains("BANKEX")) return 150000m;
        return 100000m;
    }

    private static SimulationSignalResponse MapSignal(SimulationSignal row)
    {
        return new SimulationSignalResponse
        {
            Id = row.Id,
            SimulationRunId = row.SimulationRunId,
            StrategyName = row.StrategyName,
            SignalType = row.SignalType,
            TimestampUtc = row.TimestampUtc,
            GroupId = row.GroupId,
            MetadataJson = row.MetadataJson,
            CreatedUtc = row.CreatedUtc
        };
    }

    private static PaperOrderResponse MapOrder(PaperOrder row)
    {
        return new PaperOrderResponse
        {
            Id = row.Id,
            SimulationRunId = row.SimulationRunId,
            SimulationSignalId = row.SimulationSignalId,
            StrategyName = row.StrategyName,
            GroupId = row.GroupId,
            Symbol = row.Symbol,
            Side = row.Side,
            Quantity = row.Quantity,
            OrderType = row.OrderType,
            Status = row.Status,
            RequestedPrice = row.RequestedPrice,
            FillPrice = row.FillPrice,
            CreatedUtc = row.CreatedUtc,
            FilledUtc = row.FilledUtc
        };
    }

    private static PaperPositionResponse MapPosition(PaperPosition row)
    {
        return new PaperPositionResponse
        {
            Id = row.Id,
            SimulationRunId = row.SimulationRunId,
            StrategyName = row.StrategyName,
            GroupId = row.GroupId,
            Symbol = row.Symbol,
            Direction = row.Direction,
            Quantity = row.Quantity,
            AveragePrice = row.AveragePrice,
            LastMarkPrice = row.LastMarkPrice,
            RealizedPnl = row.RealizedPnl,
            UnrealizedPnl = row.UnrealizedPnl,
            Status = row.Status,
            OpenedUtc = row.OpenedUtc,
            ClosedUtc = row.ClosedUtc,
            UpdatedUtc = row.UpdatedUtc
        };
    }
}
