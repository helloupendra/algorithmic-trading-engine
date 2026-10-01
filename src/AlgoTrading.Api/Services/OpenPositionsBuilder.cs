// src/AlgoTrading.Api/Services/OpenPositionsBuilder.cs
using AlgoTrading.Api.Controllers;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Every open leg of every live run and manual book in scope, in one answer
/// (GET /api/Positions/open).
/// </summary>
/// <remarks>
/// <para>
/// The Desk used to ask <c>/api/OptionChain/positions</c> once per underlying
/// it knew of, so a leg on any other underlying (a hand-placed share, a stock
/// option) never reached it. This reads the open legs once, whatever they are
/// written on.
/// </para>
/// <para>
/// The rows go through <see cref="PositionViewBuilder"/>, the builder behind
/// every run card: the same contract decoding, lot size, mark (latest live
/// quote, else the stored mark, with that mark's own time), carried-from and
/// greeks. The unrealized P&amp;L is <see cref="PaperPnl.Unrealized"/> at that
/// mark, the function the fills are booked with.
/// </para>
/// </remarks>
public sealed class OpenPositionsBuilder
{
    private const string OpenStatus = "Open";

    private readonly TradingDbContext _dbContext;
    private readonly PositionViewBuilder _views;

    public OpenPositionsBuilder(TradingDbContext dbContext, PositionViewBuilder views)
    {
        _dbContext = dbContext;
        _views = views;
    }

    /// <summary>
    /// <paramref name="userId"/> is already resolved by the controller: a
    /// trader always gets their own id; null is every account.
    /// </summary>
    public async Task<OpenPositionsResponse> BuildAsync(long? userId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var response = new OpenPositionsResponse { AsOfUtc = nowUtc };

        // A recap run is a test of a past day, priced from the replay on its own page: here it would be marked at
        // today's prices beside live trading (RecapRuns).
        var runs = _dbContext.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == StrategyRunControl.LivePaperMode
                        && (r.Status == StrategyRunControl.RunStatusRunning || r.Status == StrategyRunControl.RunStatusStopping))
            .WithoutRecaps();
        if (userId.HasValue)
        {
            long scopeUserId = userId.Value;
            runs = runs.Where(r => r.UserId == scopeUserId);
        }

        var rows = await (
                from p in _dbContext.PaperPositions.AsNoTracking()
                join r in runs on p.SimulationRunId equals r.Id
                where p.Status == OpenStatus && p.Quantity > 0
                select new
                {
                    r.UserId,
                    RunStrategy = r.StrategyName,
                    Position = new PaperPositionResponse
                    {
                        Id = p.Id,
                        SimulationRunId = p.SimulationRunId,
                        StrategyName = p.StrategyName,
                        GroupId = p.GroupId,
                        Symbol = p.Symbol,
                        Direction = p.Direction,
                        Quantity = p.Quantity,
                        AveragePrice = p.AveragePrice,
                        LastMarkPrice = p.LastMarkPrice,
                        RealizedPnl = p.RealizedPnl,
                        UnrealizedPnl = p.UnrealizedPnl,
                        Status = p.Status,
                        OpenedUtc = p.OpenedUtc,
                        ClosedUtc = p.ClosedUtc,
                        StopLossPrice = p.StopLossPrice,
                        TargetPrice = p.TargetPrice,
                        CarryForward = p.CarryForward,
                        CarriedFromPositionId = p.CarriedFromPositionId,
                        UpdatedUtc = p.UpdatedUtc
                    }
                })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return response;

        var built = await _views.BuildAsync<LivePositionResponse>(
            rows.Select(x => x.Position).ToList(), useLiveQuotes: true, spotSymbol: null, cancellationToken);
        var viewById = built.Positions.ToDictionary(x => x.Id);

        var userIds = rows.Select(x => x.UserId).Distinct().ToList();
        var userNames = await _dbContext.AppUsers.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);

        foreach (var row in rows)
        {
            var p = row.Position;
            var view = viewById[p.Id];
            long? age = view.LtpUpdatedUtc is { } markUtc
                ? Math.Max(0L, (long)Math.Floor((nowUtc - markUtc).TotalSeconds))
                : null;

            response.Positions.Add(new OpenPositionResponse
            {
                PositionId = p.Id,
                RunId = p.SimulationRunId,
                StrategyName = row.RunStrategy,
                IsManualBook = row.RunStrategy == ManualOrdersController.BookStrategyName,
                UserId = row.UserId,
                UserName = userNames.GetValueOrDefault(row.UserId),
                GroupId = p.GroupId,
                Symbol = p.Symbol,
                Underlying = view.Contract.Underlying,
                ExpiryDate = view.Contract.ExpiryDate,
                Strike = view.Contract.Strike,
                OptionType = view.Contract.OptionType,
                Label = view.Contract.Label,
                Direction = PaperPnl.IsLong(p.Direction) ? PaperPnl.Long : PaperPnl.Short,
                Lots = view.Lots,
                LotSize = view.LotSize,
                Quantity = view.Quantity,
                EntryPrice = p.AveragePrice,
                MarkPrice = view.Ltp,
                MarkUtc = view.LtpUpdatedUtc,
                MarkAgeSeconds = age,
                UnrealizedPnl = view.Ltp is { } mark
                    ? PaperPnl.Unrealized(p.Direction, p.AveragePrice, mark, view.Lots, view.LotSize)
                    : null,
                CarryForward = p.CarryForward,
                CarriedFromRunId = view.CarriedFromRunId,
                CarriedFromStrategy = view.CarriedFromStrategy,
                StopLossPrice = p.StopLossPrice,
                TargetPrice = p.TargetPrice,
                OpenedUtc = p.OpenedUtc,
                Greeks = view.Greeks
            });
        }

        response.Positions = response.Positions
            .OrderBy(x => x.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.UserId)
            .ThenBy(x => x.RunId)
            .ThenBy(x => x.OpenedUtc)
            .ThenBy(x => x.PositionId)
            .ToList();

        return response;
    }
}
