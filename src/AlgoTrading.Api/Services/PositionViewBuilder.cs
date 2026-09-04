// src/AlgoTrading.Api/Services/PositionViewBuilder.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Decorates paper positions into the position-based rows the live runner and
/// the backtest results page both show: decoded contract, BUY/SELL side, lots x
/// lot size, entry, mark, value and P&amp;L (rupees, premium points, percent).
/// A closed leg is the same row with lots 0 (its value uses the quantity that
/// was opened). Live views mark open rows against LiveQuotesLatest; replays
/// never do. Also sums what the open legs tie up (capital used, premium
/// outlay / received) and the per-group P&amp;L.
/// </summary>
public sealed class PositionViewBuilder
{
    private readonly TradingDbContext _dbContext;
    private readonly ILotSizeResolver _lotSizeResolver;

    public PositionViewBuilder(TradingDbContext dbContext, ILotSizeResolver lotSizeResolver)
    {
        _dbContext = dbContext;
        _lotSizeResolver = lotSizeResolver;
    }

    public sealed record Result<T>(
        List<T> Positions,
        decimal? SpotLtp,
        DateTime? SpotUpdatedUtc,
        IReadOnlyDictionary<string, LotSizeInfo> LotSizes,
        decimal CapitalUsed,
        decimal PremiumOutlay,
        decimal PremiumReceived,
        List<LiveGroupResponse> Groups) where T : LivePositionResponse;

    /// <summary>
    /// Builds the rows (open first, then newest first). With
    /// <paramref name="useLiveQuotes"/> the open rows' Ltp comes from the latest
    /// live quote (falling back to the stored mark) and the spot LTP is looked
    /// up too; otherwise Ltp is the stored LastMarkPrice and no quote is read.
    /// <paramref name="lotSizeOverride"/> applies one lot size to every row
    /// (a backtest books every contract with the run's frozen lot size).
    /// </summary>
    public async Task<Result<T>> BuildAsync<T>(
        IReadOnlyList<PaperPositionResponse> positions,
        bool useLiveQuotes,
        string? spotSymbol,
        CancellationToken cancellationToken,
        int? lotSizeOverride = null) where T : LivePositionResponse, new()
    {
        var symbols = positions.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();

        var quoteBySymbol = new Dictionary<string, (decimal? Ltp, DateTime UpdatedUtc)>(StringComparer.Ordinal);
        decimal? spotLtp = null;
        DateTime? spotUpdatedUtc = null;

        if (useLiveQuotes)
        {
            var quoteSymbols = symbols.ToList();
            if (!string.IsNullOrWhiteSpace(spotSymbol)) quoteSymbols.Add(spotSymbol);

            if (quoteSymbols.Count > 0)
            {
                var quotes = await _dbContext.LiveQuotesLatest.AsNoTracking()
                    .Where(x => quoteSymbols.Contains(x.Symbol))
                    .Select(x => new { x.Symbol, x.LastTradedPrice, x.UpdatedUtc })
                    .ToListAsync(cancellationToken);

                foreach (var q in quotes)
                {
                    quoteBySymbol.TryAdd(q.Symbol, (q.LastTradedPrice, q.UpdatedUtc));
                }
            }

            if (!string.IsNullOrWhiteSpace(spotSymbol) && quoteBySymbol.TryGetValue(spotSymbol, out var spotQuote))
            {
                spotLtp = spotQuote.Ltp;
                spotUpdatedUtc = spotQuote.UpdatedUtc;
            }
        }

        var instruments = symbols.Count == 0
            ? new List<InstrumentLite>()
            : await _dbContext.Instruments.AsNoTracking()
                .Where(x => symbols.Contains(x.Symbol))
                .Select(x => new InstrumentLite(x.Symbol, x.Underlying, x.StrikePrice, x.OptionType, x.ExpiryDate))
                .ToListAsync(cancellationToken);
        var instrumentBySymbol = instruments
            .GroupBy(x => x.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var lotSizes = await _lotSizeResolver.ResolveManyAsync(symbols, cancellationToken);

        var openedLots = await LoadOpenedLotsAsync(positions, cancellationToken);

        decimal capitalUsed = 0m, premiumOutlay = 0m, premiumReceived = 0m;

        var rows = new List<T>(positions.Count);
        foreach (var pos in positions)
        {
            bool isOpen = string.Equals(pos.Status, "Open", StringComparison.OrdinalIgnoreCase);
            int lotSize = lotSizeOverride is > 0
                ? lotSizeOverride.Value
                : lotSizes.TryGetValue(pos.Symbol, out var ls) ? ls.LotSize : 1;
            instrumentBySymbol.TryGetValue(pos.Symbol, out var inst);

            decimal? ltp = null;
            DateTime? ltpUpdatedUtc = null;
            if (isOpen)
            {
                if (useLiveQuotes && quoteBySymbol.TryGetValue(pos.Symbol, out var quote) && quote.Ltp.HasValue)
                {
                    ltp = quote.Ltp;
                    ltpUpdatedUtc = quote.UpdatedUtc;
                }
                else
                {
                    ltp = pos.LastMarkPrice;
                    ltpUpdatedUtc = useLiveQuotes ? null : pos.UpdatedUtc;
                }
            }

            bool isBuy = string.Equals(pos.Direction, "LONG", StringComparison.OrdinalIgnoreCase);
            int lots = isOpen ? pos.Quantity : 0;
            int quantity = lots * lotSize;

            // Value of a closed row: the quantity that was opened (replayed from
            // the run's fills), so the column still reads for finished legs.
            // Unknown (nothing to replay) reads as null, never as ₹0.
            int? valuedLots = isOpen
                ? pos.Quantity
                : openedLots.TryGetValue(pos.Id, out var replayed) && replayed > 0 ? replayed : null;
            int? valuedQuantity = valuedLots.HasValue ? valuedLots.Value * lotSize : null;

            decimal? entryValue = valuedQuantity.HasValue ? pos.AveragePrice * valuedQuantity.Value : null;
            decimal? currentValue = isOpen && ltp.HasValue ? ltp.Value * quantity : null;

            decimal? pnlPoints = null;
            if (isOpen)
            {
                if (ltp.HasValue) pnlPoints = isBuy ? ltp.Value - pos.AveragePrice : pos.AveragePrice - ltp.Value;
            }
            else if (valuedQuantity is > 0)
            {
                pnlPoints = pos.RealizedPnl / valuedQuantity.Value;
            }

            decimal? pnlPercent = pnlPoints.HasValue && pos.AveragePrice > 0
                ? Math.Round(pnlPoints.Value / pos.AveragePrice * 100m, 2)
                : null;

            if (isOpen)
            {
                capitalUsed += PaperTradingService.UsedCapitalOf(pos.Direction, pos.Symbol, pos.AveragePrice, pos.Quantity, lotSize);
                decimal openValue = entryValue ?? 0m;
                if (isBuy) premiumOutlay += openValue;
                else premiumReceived += openValue;
            }

            rows.Add(new T
            {
                Id = pos.Id,
                GroupId = pos.GroupId,
                Symbol = pos.Symbol,
                Contract = BuildContract(pos.Symbol, inst),
                Side = isBuy ? "BUY" : "SELL",
                Lots = lots,
                LotSize = lotSize,
                Quantity = quantity,
                Status = isOpen ? "Open" : "Closed",
                EntryPrice = pos.AveragePrice,
                Ltp = ltp,
                LtpUpdatedUtc = ltpUpdatedUtc,
                Pnl = isOpen ? pos.UnrealizedPnl : pos.RealizedPnl,
                EntryValue = entryValue,
                CurrentValue = currentValue,
                PnlPoints = pnlPoints.HasValue ? Math.Round(pnlPoints.Value, 2) : null,
                PnlPercent = pnlPercent,
                OpenedUtc = pos.OpenedUtc,
                ClosedUtc = pos.ClosedUtc
            });
        }

        rows = rows
            .OrderBy(x => x.Status == "Open" ? 0 : 1)
            .ThenByDescending(x => x.OpenedUtc)
            .ThenByDescending(x => x.Id)
            .ToList();

        var groups = positions
            .GroupBy(x => x.GroupId, StringComparer.Ordinal)
            .Select(g =>
            {
                var open = g.Where(x => string.Equals(x.Status, "Open", StringComparison.OrdinalIgnoreCase)).ToList();
                return new
                {
                    Group = new LiveGroupResponse
                    {
                        GroupId = g.Key,
                        Pnl = g.Sum(x => x.RealizedPnl) + open.Sum(x => x.UnrealizedPnl),
                        OpenLegs = open.Count,
                        ClosedLegs = g.Count() - open.Count
                    },
                    NewestOpenedUtc = g.Max(x => x.OpenedUtc)
                };
            })
            .OrderBy(x => x.Group.OpenLegs > 0 ? 0 : 1)
            .ThenByDescending(x => x.NewestOpenedUtc)
            .Select(x => x.Group)
            .ToList();

        return new Result<T>(rows, spotLtp, spotUpdatedUtc, lotSizes, capitalUsed, premiumOutlay, premiumReceived, groups);
    }

    /// <summary>
    /// Lots opened per CLOSED position (by position id). Open rows carry their
    /// live quantity and need no lookup.
    ///
    /// Reconstructed by replaying the run's filled orders per (run, group,
    /// symbol) in fill order with the same rules PaperTradingService applies
    /// them (open → add on the same side → reduce on the opposite side → the
    /// remainder of a larger opposite fill opens a reverse position). The k-th
    /// position the replay produces is the k-th position the run created for
    /// that key, so a position opened by the remainder of a reversing order is
    /// credited with the remainder only — not the order's whole quantity. When
    /// the replay and the stored rows disagree (rows written by an older
    /// build), the position's opening-side fills inside its open → close
    /// window are used instead; a position that matches neither is left out
    /// (its value is reported as unknown, not zero).
    /// </summary>
    internal async Task<Dictionary<long, int>> LoadOpenedLotsAsync(
        IReadOnlyList<PaperPositionResponse> positions,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, int>();
        if (!positions.Any(x => !string.Equals(x.Status, "Open", StringComparison.OrdinalIgnoreCase))) return result;

        var runIds = positions.Select(x => x.SimulationRunId).Distinct().ToList();
        var orders = await _dbContext.PaperOrders.AsNoTracking()
            .Where(o => runIds.Contains(o.SimulationRunId) && o.Status == "Filled")
            .Select(o => new OrderLite(o.Id, o.SimulationRunId, o.GroupId, o.Symbol, o.Side, o.Quantity, o.FilledUtc ?? o.CreatedUtc))
            .ToListAsync(cancellationToken);

        return ReconstructOpenedLots(positions, orders);
    }

    /// <summary>A filled order, as much of it as the replay needs.</summary>
    internal sealed record OrderLite(long Id, long SimulationRunId, string GroupId, string Symbol, string Side, int Quantity, DateTime At);

    /// <summary>Pure replay of <see cref="LoadOpenedLotsAsync"/> over in-memory rows.</summary>
    internal static Dictionary<long, int> ReconstructOpenedLots(
        IReadOnlyList<PaperPositionResponse> positions,
        IReadOnlyList<OrderLite> orders)
    {
        var result = new Dictionary<long, int>();

        var ordersByKey = orders
            .GroupBy(o => (o.SimulationRunId, o.GroupId, o.Symbol))
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.At).ThenBy(o => o.Id).ToList());

        foreach (var keyGroup in positions.GroupBy(p => (p.SimulationRunId, p.GroupId, p.Symbol)))
        {
            // Creation order: ids are assigned by the database as positions are opened.
            var keyPositions = keyGroup.OrderBy(p => p.Id).ToList();
            var closedPositions = keyPositions
                .Where(p => !string.Equals(p.Status, "Open", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (closedPositions.Count == 0) continue;

            if (!ordersByKey.TryGetValue(keyGroup.Key, out var keyOrders)) continue;

            var replayed = ReplayOpenedLots(keyOrders);
            bool consistent = replayed.Count == keyPositions.Count;
            if (consistent)
            {
                for (int i = 0; i < keyPositions.Count; i++)
                {
                    var pos = keyPositions[i];
                    var sim = replayed[i];
                    bool directionMatches = string.Equals(pos.Direction, sim.Direction, StringComparison.OrdinalIgnoreCase);
                    bool openStateMatches = string.Equals(pos.Status, "Open", StringComparison.OrdinalIgnoreCase) == sim.StillOpen;
                    if (!directionMatches || !openStateMatches)
                    {
                        consistent = false;
                        break;
                    }
                }
            }

            if (consistent)
            {
                for (int i = 0; i < keyPositions.Count; i++)
                {
                    var pos = keyPositions[i];
                    if (string.Equals(pos.Status, "Open", StringComparison.OrdinalIgnoreCase)) continue;
                    if (replayed[i].OpenedLots > 0) result[pos.Id] = replayed[i].OpenedLots;
                }
                continue;
            }

            // Fallback for rows the replay cannot account for: the opening-side
            // fills inside the position's own open → close window.
            foreach (var pos in closedPositions)
            {
                var openingSide = string.Equals(pos.Direction, "LONG", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
                var from = pos.OpenedUtc.AddSeconds(-1);
                var to = (pos.ClosedUtc ?? pos.UpdatedUtc).AddSeconds(1);

                int lots = keyOrders
                    .Where(o => o.Side == openingSide && o.At >= from && o.At <= to)
                    .Sum(o => o.Quantity);

                if (lots > 0) result[pos.Id] = lots;
            }
        }

        return result;
    }

    /// <summary>One position the replay produced: what direction it had, how many lots were ever opened into it, and whether it is still open at the end.</summary>
    internal sealed record ReplayedPosition(string Direction, int OpenedLots, bool StillOpen);

    /// <summary>
    /// Replays one key's fills (already in fill order) with
    /// PaperTradingService's position rules and returns the positions it
    /// produced, in creation order.
    /// </summary>
    internal static List<ReplayedPosition> ReplayOpenedLots(IReadOnlyList<OrderLite> keyOrders)
    {
        var produced = new List<ReplayedPosition>();
        string? direction = null;
        int quantity = 0;
        int opened = 0;

        foreach (var order in keyOrders)
        {
            if (order.Quantity <= 0) continue;
            var side = order.Side.Trim().ToUpperInvariant();
            if (side != "BUY" && side != "SELL") continue;
            var orderDirection = side == "BUY" ? "LONG" : "SHORT";

            if (direction is null)
            {
                direction = orderDirection;
                quantity = order.Quantity;
                opened = order.Quantity;
                continue;
            }

            if (direction == orderDirection)
            {
                quantity += order.Quantity;
                opened += order.Quantity;
                continue;
            }

            int closing = Math.Min(quantity, order.Quantity);
            quantity -= closing;
            if (quantity == 0)
            {
                produced.Add(new ReplayedPosition(direction, opened, StillOpen: false));
                direction = null;
                opened = 0;
            }

            int remainder = order.Quantity - closing;
            if (remainder > 0)
            {
                // The reversing order's remainder opens the reverse position:
                // only that remainder was ever opened into it.
                direction = orderDirection;
                quantity = remainder;
                opened = remainder;
            }
        }

        if (direction is not null)
        {
            produced.Add(new ReplayedPosition(direction, opened, StillOpen: true));
        }

        return produced;
    }

    public sealed record InstrumentLite(string Symbol, string Underlying, decimal? StrikePrice, string OptionType, DateOnly? ExpiryDate);

    /// <summary>
    /// Decoded contract for display: the instrument master wins, the FYERS
    /// symbol grammar fills in for contracts the master no longer holds.
    /// </summary>
    public static ContractInfo BuildContract(string symbol, InstrumentLite? inst)
    {
        var parsed = UnderlyingCatalog.ParseOptionSymbol(symbol);

        var underlying = !string.IsNullOrWhiteSpace(inst?.Underlying)
            ? inst!.Underlying.Trim().ToUpperInvariant()
            : parsed?.Underlying ?? UnderlyingCatalog.InferUnderlying(symbol);

        var strike = inst?.StrikePrice is > 0 ? inst.StrikePrice : parsed?.Strike;
        var optionType = !string.IsNullOrWhiteSpace(inst?.OptionType)
            ? inst!.OptionType.Trim().ToUpperInvariant()
            : parsed?.OptionType ?? string.Empty;
        var expiry = inst?.ExpiryDate ?? parsed?.Expiry;

        bool looksLikeOption = strike.HasValue && (optionType == "CE" || optionType == "PE");

        return new ContractInfo
        {
            Underlying = underlying,
            Strike = strike,
            OptionType = optionType,
            ExpiryDate = expiry,
            Label = looksLikeOption
                ? UnderlyingCatalog.ContractLabel(underlying, strike, optionType, expiry)
                : symbol
        };
    }
}
