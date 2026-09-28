// src/AlgoTrading.Api/Services/OrdersBuilder.cs
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// What GET /api/Orders asks for. Both accounts are already resolved by the
/// controller with <c>User.ScopeUserId</c>: <paramref name="VisibleUserId"/>
/// is every account the caller may see (null for an admin, their own id for
/// a trader), which the counts cover; <paramref name="UserId"/> is the
/// account the rows are from (an admin's pick, null for every account; a
/// trader's own id whatever they asked). <paramref name="Status"/> is null
/// for any, or one of <see cref="OrdersBuilder.Statuses"/> in its own spelling.
/// </summary>
public sealed record OrdersFilter(
    DateOnly Date,
    string Mode,
    long? VisibleUserId,
    long? UserId,
    long? RunId,
    string? Symbol,
    string? Status,
    int Skip,
    int Take)
{
    public const int DefaultTake = 200;
    public const int MaxTake = 500;
}

/// <summary>
/// One IST day of orders across runs and manual books (GET /api/Orders): the
/// paper orders booked, and the orders the risk gate refused.
/// </summary>
/// <remarks>
/// <para>
/// Until 28 Sep Trade → Orders had no such read: it asked each of the day's
/// runs for its own ledger and merged them, and found the manual books it
/// knew of through the open positions, so another account's book with no
/// open leg was never asked, and a refused order was nowhere.
/// </para>
/// <para>
/// A refused order writes no paper order: the signal and its fills commit or
/// roll back together (PaperTradingService). The risk gate's refusals (kill
/// switch, order rate, daily loss) leave an <c>OrderRejected</c> risk event,
/// written outside that transaction, and those are the rejections listed
/// here. A signal refused for a stale quote or a stopped run is answered to
/// the runner and kept in its log only; there is nothing here to read.
/// </para>
/// <para>
/// A day is the order's placed time on the IST calendar (a replay's order is
/// stamped with its bar's time, a recap's with the replayed session's), so a
/// manual book opened last week shows today's orders under today.
/// </para>
/// </remarks>
public sealed class OrdersBuilder
{
    public const string KindOrder = "order";
    public const string KindRejection = "rejection";
    public const string RejectedStatus = "Rejected";

    /// <summary>Every status the filter accepts: the paper order's own (PaperOrder.Status), and a rejection's.</summary>
    public static readonly IReadOnlyList<string> Statuses = new[] { "Filled", "Pending", "Cancelled", RejectedStatus };

    private readonly TradingDbContext _dbContext;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly StrategyProcessRegistry _registry;

    public OrdersBuilder(TradingDbContext dbContext, ILotSizeResolver lotSizeResolver, StrategyProcessRegistry registry)
    {
        _dbContext = dbContext;
        _lotSizeResolver = lotSizeResolver;
        _registry = registry;
    }

    public async Task<OrdersResponse> BuildAsync(OrdersFilter filter, CancellationToken cancellationToken)
    {
        var dayStartUtc = IstTime.StartOfDayUtc(filter.Date);
        var dayEndUtc = IstTime.StartOfDayUtc(filter.Date.AddDays(1));
        bool replay = PaperTradingService.IsReplay(filter.Mode);

        var visibleRuns = RunsOf(filter.Mode, filter.VisibleUserId);

        // What the filters can offer, before they narrow anything (the
        // account among them): the day's rows by run and status, over every
        // account the caller may see. Two GROUP BYs, whatever the day holds.
        var orderCounts = await DayOrders(visibleRuns, dayStartUtc, dayEndUtc)
            .GroupBy(o => new { o.SimulationRunId, o.Status })
            .Select(g => new { g.Key.SimulationRunId, g.Key.Status, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var rejectionCounts = await DayRejections(visibleRuns, dayStartUtc, dayEndUtc)
            .GroupBy(e => e.SimulationRunId!.Value)
            .Select(g => new { RunId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var rowRuns = filter.UserId == filter.VisibleUserId ? visibleRuns : RunsOf(filter.Mode, filter.UserId);
        var orders = DayOrders(rowRuns, dayStartUtc, dayEndUtc);
        var rejections = DayRejections(rowRuns, dayStartUtc, dayEndUtc);
        if (filter.RunId.HasValue)
        {
            long runId = filter.RunId.Value;
            orders = orders.Where(o => o.SimulationRunId == runId);
            rejections = rejections.Where(e => e.SimulationRunId == runId);
        }
        if (!string.IsNullOrEmpty(filter.Symbol))
        {
            string symbol = filter.Symbol;
            orders = orders.Where(o => o.Symbol == symbol);
            rejections = rejections.Where(e => e.Symbol == symbol);
        }

        bool rejectedOnly = filter.Status == RejectedStatus;
        bool wantOrders = !rejectedOnly;
        bool wantRejections = filter.Status is null || rejectedOnly;
        if (filter.Status is { } status && wantOrders)
        {
            orders = orders.Where(o => o.Status == status);
        }

        int orderTotal = wantOrders ? await orders.CountAsync(cancellationToken) : 0;
        int rejectionTotal = wantRejections ? await rejections.CountAsync(cancellationToken) : 0;

        // Newest first across both sources: the newest skip + take of each
        // hold every row of the page, so each is cut there and the two merged.
        int window = filter.Skip + filter.Take;
        var orderRows = wantOrders && orderTotal > 0
            ? await orders
                .OrderByDescending(o => o.CreatedUtc).ThenByDescending(o => o.Id)
                .Take(window)
                .Select(o => new
                {
                    o.Id, o.SimulationRunId, o.SimulationSignalId, o.GroupId, o.Symbol, o.Side, o.Quantity,
                    o.OrderType, o.Status, o.RequestedPrice, o.FillPrice, o.MetadataJson, o.CreatedUtc, o.FilledUtc
                })
                .ToListAsync(cancellationToken)
            : [];
        var rejectionRows = wantRejections && rejectionTotal > 0
            ? await rejections
                .OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.Id)
                .Take(window)
                .Select(e => new { e.Id, RunId = e.SimulationRunId!.Value, e.Symbol, e.Reason, e.DetailsJson, e.OccurredUtc })
                .ToListAsync(cancellationToken)
            : [];

        var page = orderRows
            .Select(o =>
            {
                var source = PaperFill.ReadSource(o.MetadataJson);
                return new OrderRowResponse
                {
                    Kind = KindOrder,
                    Id = o.Id,
                    AtUtc = o.CreatedUtc,
                    FilledUtc = o.FilledUtc,
                    RunId = o.SimulationRunId,
                    Symbol = o.Symbol,
                    Side = o.Side,
                    Lots = o.Quantity,
                    OrderType = o.OrderType,
                    Status = o.Status,
                    RequestedPrice = o.RequestedPrice,
                    FillPrice = o.FillPrice,
                    PriceRule = source?.Rule,
                    PriceNote = source?.Note,
                    QuoteAgeSeconds = source?.QuoteAgeSeconds,
                    StaleQuote = source?.StaleQuote ?? false,
                    GroupId = string.IsNullOrEmpty(o.GroupId) ? null : o.GroupId,
                    SignalId = o.SimulationSignalId
                };
            })
            .Concat(rejectionRows.Select(e =>
            {
                var (side, lots) = RejectionDetails.Read(e.DetailsJson);
                return new OrderRowResponse
                {
                    Kind = KindRejection,
                    Id = e.Id,
                    AtUtc = e.OccurredUtc,
                    RunId = e.RunId,
                    Symbol = e.Symbol ?? string.Empty,
                    Side = side,
                    Lots = lots,
                    Status = RejectedStatus,
                    Reason = e.Reason
                };
            }))
            .OrderByDescending(x => x.AtUtc)
            .ThenBy(x => x.Kind == KindOrder ? 0 : 1)
            .ThenByDescending(x => x.Id)
            .Skip(filter.Skip)
            .Take(filter.Take)
            .ToList();

        var runIds = orderCounts.Select(x => x.SimulationRunId)
            .Concat(rejectionCounts.Select(x => x.RunId))
            .Distinct()
            .ToList();
        var runs = runIds.Count == 0
            ? new Dictionary<long, RunInfo>()
            : (await _dbContext.SimulationRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id))
                .Select(r => new { r.Id, r.UserId, r.StrategyName, r.Symbol, r.ParametersJson, r.Status })
                .ToListAsync(cancellationToken))
            .ToDictionary(r => r.Id, r => new RunInfo(
                r.Id, r.UserId, r.StrategyName, r.Status,
                IsBook: r.StrategyName == ManualOrdersController.BookStrategyName,
                Underlying: r.StrategyName == ManualOrdersController.BookStrategyName
                    ? null
                    : UnderlyingOf(r.Id, r.Symbol, r.ParametersJson, replay),
                FrozenLotSize: replay ? BacktestRunParameters.Parse(r.ParametersJson).LotSize : null));

        var userIds = runs.Values.Select(r => r.UserId).Distinct().ToList();
        var userNames = userIds.Count == 0
            ? new Dictionary<long, string>()
            : await _dbContext.AppUsers.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);

        var signalIds = page.Where(x => x.SignalId.HasValue).Select(x => x.SignalId!.Value).Distinct().ToList();
        var clientIds = signalIds.Count == 0
            ? new Dictionary<long, string?>()
            : await _dbContext.SimulationSignals.AsNoTracking()
                .Where(s => signalIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.ClientSignalId, cancellationToken);

        var lotSizeOf = await LotSizesAsync(page, runs, replay, cancellationToken);

        foreach (var row in page)
        {
            if (runs.TryGetValue(row.RunId, out var run))
            {
                row.StrategyName = run.StrategyName;
                row.IsManualBook = run.IsBook;
                row.Underlying = run.Underlying;
                row.UserId = run.UserId;
                row.UserName = userNames.GetValueOrDefault(run.UserId);
            }

            row.LotSize = lotSizeOf(row);
            row.Quantity = row.Lots * row.LotSize;
            if (row.SignalId is { } signalId) row.ClientSignalId = clientIds.GetValueOrDefault(signalId);
        }

        var rejectedByRun = rejectionCounts.ToDictionary(x => x.RunId, x => x.Count);
        var runFacets = runIds
            .Where(runs.ContainsKey)
            .Select(id =>
            {
                var run = runs[id];
                int rejected = rejectedByRun.GetValueOrDefault(id);
                return new OrderRunFacet
                {
                    RunId = id,
                    UserId = run.UserId,
                    UserName = userNames.GetValueOrDefault(run.UserId),
                    StrategyName = run.StrategyName,
                    Underlying = run.Underlying,
                    IsManualBook = run.IsBook,
                    RunStatus = run.Status,
                    Orders = orderCounts.Where(x => x.SimulationRunId == id).Sum(x => x.Count) + rejected,
                    Filled = orderCounts.Where(x => x.SimulationRunId == id && x.Status == "Filled").Sum(x => x.Count),
                    Rejected = rejected
                };
            })
            .OrderBy(x => x.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.UserId)
            .ThenBy(x => x.RunId)
            .ToList();

        var statusFacets = orderCounts
            .GroupBy(x => x.Status, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OrderStatusCount { Status = g.Key, Orders = g.Sum(x => x.Count) })
            .ToList();
        int rejectedToday = rejectionCounts.Sum(x => x.Count);
        if (rejectedToday > 0) statusFacets.Add(new OrderStatusCount { Status = RejectedStatus, Orders = rejectedToday });

        return new OrdersResponse
        {
            Date = filter.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DayStartUtc = dayStartUtc,
            Mode = filter.Mode,
            Total = orderTotal + rejectionTotal,
            Skip = filter.Skip,
            Take = filter.Take,
            Orders = page,
            Statuses = statusFacets.OrderBy(x => StatusRank(x.Status)).ThenBy(x => x.Status, StringComparer.Ordinal).ToList(),
            Runs = runFacets
        };
    }

    /// <summary>The runs of one mode, of one account or of every account.</summary>
    private IQueryable<SimulationRun> RunsOf(string mode, long? userId)
    {
        var runs = _dbContext.SimulationRuns.AsNoTracking().Where(r => r.Mode == mode);
        if (userId.HasValue)
        {
            long scopeUserId = userId.Value;
            runs = runs.Where(r => r.UserId == scopeUserId);
        }
        return runs;
    }

    /// <summary>The paper orders of these runs placed on the day (IST, as UTC bounds).</summary>
    private IQueryable<PaperOrder> DayOrders(IQueryable<SimulationRun> runs, DateTime fromUtc, DateTime toUtc)
        => from o in _dbContext.PaperOrders.AsNoTracking()
           join r in runs on o.SimulationRunId equals r.Id
           where o.CreatedUtc >= fromUtc && o.CreatedUtc < toUtc
           select o;

    /// <summary>The risk gate's refusals of these runs' orders on the day.</summary>
    private IQueryable<RiskEvent> DayRejections(IQueryable<SimulationRun> runs, DateTime fromUtc, DateTime toUtc)
        => from e in _dbContext.RiskEvents.AsNoTracking()
           join r in runs on e.SimulationRunId equals (long?)r.Id
           where e.Kind == RiskManagementService.OrderRejectedKind
                 && e.OccurredUtc >= fromUtc && e.OccurredUtc < toUtc
           select e;

    private sealed record RunInfo(long Id, long UserId, string StrategyName, string Status, bool IsBook, string? Underlying, int? FrozenLotSize);

    /// <summary>Filled first, a rejection last, anything else between: the order a status filter lists them in.</summary>
    private static int StatusRank(string status)
        => status == "Filled" ? 0 : status == RejectedStatus ? 2 : 1;

    /// <summary>A live run's underlying as its run list names it; a backtest's as its results page does.</summary>
    private string UnderlyingOf(long runId, string? symbol, string? parametersJson, bool replay)
    {
        if (replay)
        {
            var underlying = BacktestRunParameters.Parse(parametersJson).Underlying
                             ?? UnderlyingCatalog.UnderlyingForSpot(symbol)
                             ?? UnderlyingCatalog.InferUnderlying(symbol);
            return underlying.Trim().ToUpperInvariant();
        }

        var running = _registry.Get(runId);
        var exit = running is null ? _registry.GetExitByRun(runId) : null;
        return LiveRunHistoryBuilder.DeriveUnderlying(running, exit, LiveRunParameters.Parse(parametersJson), symbol);
    }

    /// <summary>
    /// The lot size each row is read at. A live run's fills are valued at the
    /// contract's lot size as its run card values them (PositionViewBuilder,
    /// one lookup for the page's symbols); a backtest books every contract at
    /// the lot size frozen into the run at its start.
    /// </summary>
    private async Task<Func<OrderRowResponse, int?>> LotSizesAsync(
        IReadOnlyList<OrderRowResponse> page,
        IReadOnlyDictionary<long, RunInfo> runs,
        bool replay,
        CancellationToken cancellationToken)
    {
        if (page.Count == 0) return _ => null;

        if (!replay)
        {
            var bySymbol = await _lotSizeResolver.ResolveManyAsync(
                page.Select(x => x.Symbol).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal), cancellationToken);
            return row => bySymbol.TryGetValue(row.Symbol, out var lot) ? lot.LotSize : null;
        }

        var byRun = new Dictionary<long, int>();
        var byUnderlying = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (long runId in page.Select(x => x.RunId).Distinct())
        {
            if (!runs.TryGetValue(runId, out var run)) continue;
            if (run.FrozenLotSize is > 0)
            {
                byRun[runId] = run.FrozenLotSize.Value;
                continue;
            }

            string underlying = run.Underlying ?? string.Empty;
            if (!byUnderlying.TryGetValue(underlying, out int lotSize))
            {
                lotSize = (await _lotSizeResolver.ResolveForUnderlyingAsync(underlying, cancellationToken)).LotSize;
                byUnderlying[underlying] = lotSize;
            }
            byRun[runId] = lotSize;
        }
        return row => byRun.TryGetValue(row.RunId, out int lot) ? lot : null;
    }
}
