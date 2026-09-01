// src/AlgoTrading.Infrastructure/Services/PaperTradingService.cs
using AlgoTrading.Application.Interfaces;
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
/// </summary>
public class PaperTradingService : IPaperTradingService
{
    private readonly TradingDbContext _dbContext;
    private readonly IRiskManagementService _riskManagementService;

    public PaperTradingService(TradingDbContext dbContext, IRiskManagementService riskManagementService)
    {
        _dbContext = dbContext;
        _riskManagementService = riskManagementService;
    }

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

        var signal = new SimulationSignal
        {
            SimulationRunId = request.SimulationRunId,
            StrategyName = request.StrategyName,
            SignalType = request.SignalType,
            TimestampUtc = request.TimestampUtc.ToUniversalTime(),
            GroupId = request.GroupId,
            MetadataJson = string.IsNullOrWhiteSpace(request.MetadataJson) ? "{}" : request.MetadataJson,
            CreatedUtc = DateTime.UtcNow
        };

        await _dbContext.SimulationSignals.AddAsync(signal, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Convert signal -> orders -> positions
        if (request.Legs is not null && request.Legs.Count > 0)
        {
            foreach (var leg in request.Legs)
            {
                await CreateOrderAndApplyPositionAsync(signal, leg, cancellationToken);
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

        // Mark-to-market open positions using latest live quote
        var symbols = rows
            .Where(x => x.Status == "Open")
            .Select(x => x.Symbol)
            .Distinct()
            .ToList();

        if (symbols.Count > 0)
        {
            var latestQuotes = await _dbContext.LiveQuotesLatest
                .AsNoTracking()
                .Where(x => symbols.Contains(x.Symbol))
                .ToDictionaryAsync(x => x.Symbol, x => x.LastTradedPrice, cancellationToken);

            foreach (var pos in rows.Where(x => x.Status == "Open"))
            {
                if (latestQuotes.TryGetValue(pos.Symbol, out var lastPrice) && lastPrice.HasValue)
                {
                    int lotSize = GetLotSizeHeuristic(pos.Symbol);
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

        var positions = await _dbContext.PaperPositions
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        var orders = await _dbContext.PaperOrders
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId)
            .ToListAsync(cancellationToken);

        var symbols = positions
            .Where(x => x.Status == "Open")
            .Select(x => x.Symbol)
            .Distinct()
            .ToList();

        var latestQuotes = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol))
            .ToDictionaryAsync(x => x.Symbol, x => x.LastTradedPrice, cancellationToken);

        decimal usedCapital = 0m;
        decimal realizedPnl = 0m;
        decimal unrealizedPnl = 0m;

        foreach (var pos in positions)
        {
            int lotSize = GetLotSizeHeuristic(pos.Symbol);
            
            // Re-calculate realized Pnl using LotSize just in case
            // (Note: To be fully accurate we would also update RealizedPnl when closing the position)
            realizedPnl += pos.RealizedPnl;

            if (pos.Status == "Open")
            {
                if (pos.Direction == "SHORT")
                {
                    usedCapital += GetMarginHeuristic(pos.Symbol) * pos.Quantity;
                }
                else
                {
                    usedCapital += Math.Abs(pos.AveragePrice * pos.Quantity * lotSize);
                }

                if (latestQuotes.TryGetValue(pos.Symbol, out var lastPrice) && lastPrice.HasValue)
                {
                    pos.LastMarkPrice = lastPrice.Value;
                    pos.UnrealizedPnl = CalculateUnrealizedPnl(
                        pos.Direction,
                        pos.AveragePrice,
                        lastPrice.Value,
                        pos.Quantity,
                        lotSize);
                }

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

                decimal groupUsed = open.Sum(x => 
                {
                    int ls = GetLotSizeHeuristic(x.Symbol);
                    return x.Direction == "SHORT" 
                        ? GetMarginHeuristic(x.Symbol) * x.Quantity 
                        : Math.Abs(x.AveragePrice * x.Quantity * ls);
                });
                decimal groupRealized = g.Sum(x => x.RealizedPnl);
                decimal groupUnrealized = open.Sum(x => x.UnrealizedPnl);

                string status = open.Count > 0 ? "Open" : "Closed";

                return new PositionGroupSummaryResponse
                {
                    GroupId = g.Key.GroupId,
                    StrategyName = g.Key.StrategyName,
                    OpenPositionCount = open.Count,
                    ClosedPositionCount = closed.Count,
                    UsedCapital = groupUsed,
                    RealizedPnl = groupRealized,
                    UnrealizedPnl = groupUnrealized,
                    Status = status
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

        var symbols = positions
            .Where(x => x.Status == "Open")
            .Select(x => x.Symbol)
            .Distinct()
            .ToList();

        var latestQuotes = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol))
            .ToDictionaryAsync(x => x.Symbol, x => x.LastTradedPrice, cancellationToken);

        decimal usedCapital = 0m;
        decimal realizedPnl = 0m;
        decimal unrealizedPnl = 0m;

        foreach (var pos in positions)
        {
            int lotSize = GetLotSizeHeuristic(pos.Symbol);
            realizedPnl += pos.RealizedPnl;

            if (pos.Status == "Open")
            {
                if (pos.Direction == "SHORT")
                {
                    usedCapital += GetMarginHeuristic(pos.Symbol) * pos.Quantity;
                }
                else
                {
                    usedCapital += Math.Abs(pos.AveragePrice * pos.Quantity * lotSize);
                }

                if (latestQuotes.TryGetValue(pos.Symbol, out var lastPrice) && lastPrice.HasValue)
                {
                    pos.LastMarkPrice = lastPrice.Value;
                    pos.UnrealizedPnl = CalculateUnrealizedPnl(
                        pos.Direction,
                        pos.AveragePrice,
                        lastPrice.Value,
                        pos.Quantity,
                        lotSize);

                    pos.UpdatedUtc = DateTime.UtcNow;
                }

                unrealizedPnl += pos.UnrealizedPnl;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        decimal totalPnl = realizedPnl + unrealizedPnl;
        decimal currentEquity = run.InitialCapital + totalPnl;
        decimal availableCapital = run.InitialCapital + realizedPnl - usedCapital;
        decimal returnPercent = run.InitialCapital > 0
            ? (totalPnl / run.InitialCapital) * 100m
            : 0m;

        var snapshot = new SimulationEquitySnapshot
        {
            SimulationRunId = run.Id,
            SnapshotUtc = DateTime.UtcNow,
            InitialCapital = run.InitialCapital,
            UsedCapital = usedCapital,
            AvailableCapital = availableCapital,
            RealizedPnl = realizedPnl,
            UnrealizedPnl = unrealizedPnl,
            TotalPnl = totalPnl,
            CurrentEquity = currentEquity,
            OpenPositions = positions.Count(x => x.Status == "Open"),
            ClosedPositions = positions.Count(x => x.Status == "Closed")
        };

        await _dbContext.SimulationEquitySnapshots.AddAsync(snapshot, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var groupSummaries = positions
            .GroupBy(x => new { x.GroupId, x.StrategyName })
            .Select(g =>
            {
                var open = g.Where(x => x.Status == "Open").ToList();
                var closed = g.Where(x => x.Status == "Closed").ToList();

                decimal groupUsed = open.Sum(x => 
                {
                    int ls = GetLotSizeHeuristic(x.Symbol);
                    return x.Direction == "SHORT" 
                        ? GetMarginHeuristic(x.Symbol) * x.Quantity 
                        : Math.Abs(x.AveragePrice * x.Quantity * ls);
                });
                decimal groupRealized = g.Sum(x => x.RealizedPnl);
                decimal groupUnrealized = open.Sum(x => x.UnrealizedPnl);

                string status = open.Count > 0 ? "Open" : "Closed";

                return new PositionGroupSummaryResponse
                {
                    GroupId = g.Key.GroupId,
                    StrategyName = g.Key.StrategyName,
                    OpenPositionCount = open.Count,
                    ClosedPositionCount = closed.Count,
                    UsedCapital = groupUsed,
                    RealizedPnl = groupRealized,
                    UnrealizedPnl = groupUnrealized,
                    Status = status
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
        var openPositions = await _dbContext.PaperPositions
            .Where(x => x.Status == "Open")
            .ToListAsync(cancellationToken);

        if (openPositions.Count == 0) return;

        foreach (var pos in openPositions)
        {
            var closingSide = pos.Direction == "LONG" ? "SELL" : "BUY";
            decimal fillPrice = pos.LastMarkPrice ?? pos.AveragePrice; // Heuristic fallback

            var signal = new SimulationSignal
            {
                SimulationRunId = pos.SimulationRunId,
                StrategyName = "SYSTEM_RMS_FLATTEN",
                SignalType = "EXIT",
                TimestampUtc = DateTime.UtcNow,
                GroupId = pos.GroupId,
                MetadataJson = "{\"Reason\": \"GLOBAL_KILL_SWITCH\"}",
                CreatedUtc = DateTime.UtcNow
            };

            await _dbContext.SimulationSignals.AddAsync(signal, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var leg = new SimulationSignalLegRequest
            {
                Symbol = pos.Symbol,
                Side = closingSide,
                Quantity = pos.Quantity,
                Price = fillPrice
            };

            // Directly call internal helper bypassing risk evaluation 
            // since this IS a risk management action.
            await CreateOrderAndApplyPositionAsync(signal, leg, cancellationToken, bypassRiskCheck: true);
        }
    }

    // ---------------------------------------------------------------------
    // INTERNAL ORDER / POSITION APPLICATION
    // ---------------------------------------------------------------------

    private async Task CreateOrderAndApplyPositionAsync(
        SimulationSignal signal,
        SimulationSignalLegRequest leg,
        CancellationToken cancellationToken,
        bool bypassRiskCheck = false)
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

        var order = new PaperOrder
        {
            SimulationRunId = signal.SimulationRunId,
            SimulationSignalId = signal.Id,
            StrategyName = signal.StrategyName,
            GroupId = signal.GroupId,
            Symbol = leg.Symbol,
            Side = normalizedSide,
            Quantity = leg.Quantity,
            OrderType = "MARKET_SIM",
            Status = "Filled",
            RequestedPrice = leg.Price,
            FillPrice = fillPrice,
            CreatedUtc = DateTime.UtcNow,
            FilledUtc = DateTime.UtcNow
        };

        await _dbContext.PaperOrders.AddAsync(order, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await ApplyPositionAsync(signal, order, cancellationToken);
    }

    private async Task ApplyPositionAsync(
        SimulationSignal signal,
        PaperOrder order,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.PaperPositions
            .FirstOrDefaultAsync(x =>
                x.SimulationRunId == signal.SimulationRunId &&
                x.GroupId == signal.GroupId &&
                x.Symbol == order.Symbol &&
                x.Status == "Open",
                cancellationToken);

        if (existing is null)
        {
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
                OpenedUtc = order.FilledUtc ?? DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            await _dbContext.PaperPositions.AddAsync(pos, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        bool sameDirection =
            (existing.Direction == "LONG" && order.Side == "BUY") ||
            (existing.Direction == "SHORT" && order.Side == "SELL");

        if (sameDirection)
        {
            int newQty = existing.Quantity + order.Quantity;
            decimal oldNotional = existing.AveragePrice * existing.Quantity;
            decimal newNotional = (order.FillPrice ?? 0m) * order.Quantity;

            existing.AveragePrice = (oldNotional + newNotional) / newQty;
            existing.Quantity = newQty;
            existing.LastMarkPrice = order.FillPrice;
            existing.UpdatedUtc = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        int closingQty = Math.Min(existing.Quantity, order.Quantity);
        decimal fillPrice = order.FillPrice ?? 0m;

        int lotSize = GetLotSizeHeuristic(existing.Symbol);
        
        decimal realized = CalculateRealizedPnl(
            existing.Direction,
            existing.AveragePrice,
            fillPrice,
            closingQty,
            lotSize);

        existing.RealizedPnl += realized;
        existing.Quantity -= closingQty;
        existing.LastMarkPrice = fillPrice;
        existing.UpdatedUtc = DateTime.UtcNow;

        if (existing.Quantity == 0)
        {
            existing.Status = "Closed";
            existing.ClosedUtc = DateTime.UtcNow;
            existing.UnrealizedPnl = 0m;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        int remainder = order.Quantity - closingQty;
        if (remainder > 0)
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
                OpenedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };

            await _dbContext.PaperPositions.AddAsync(newPos, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------------
    // HELPERS
    // ---------------------------------------------------------------------

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

    private static int GetLotSizeHeuristic(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return 1;
        var s = symbol.ToUpperInvariant();
        if (s.Contains("BANKNIFTY")) return 15;
        if (s.Contains("FINNIFTY")) return 40;
        if (s.Contains("MIDCPNIFTY")) return 75;
        if (s.Contains("NIFTY")) return 25;
        if (s.Contains("SENSEX")) return 10;
        if (s.Contains("BANKEX")) return 15;
        return 1;
    }

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