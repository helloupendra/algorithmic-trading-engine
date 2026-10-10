// src/AlgoTrading.Api/Services/ManualOrderService.cs
using System.Globalization;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>One order for a user's manual book, as the ticket (or the AI Trader) asks for it.</summary>
/// <param name="UserId">Whose book it goes into.</param>
/// <param name="UserName">Who placed it, recorded on the order's signal.</param>
/// <param name="Quantity">Lots for a derivative, shares for equity.</param>
/// <param name="LimitPrice">Omitted, the order fills at the far side of the book (a buy at the ask, a sell at the bid).</param>
/// <param name="Source">Recorded on the order's signal beside the rest (the AI Trader names its decision); null for the ticket.</param>
/// <param name="Confirm">
/// A last check on the price the order would fill at and the quote it came from, made just before it is booked: a
/// reason refuses the order, null books it. The AI Trader judges its rules here on the price it would actually pay.
/// </param>
public sealed record ManualOrderCommand(
    long UserId,
    string UserName,
    string Symbol,
    string Side,
    int Quantity,
    decimal? LimitPrice,
    decimal? StopLossPrice = null,
    decimal? TargetPrice = null,
    bool CarryForward = false,
    object? Source = null,
    Func<ManualOrderPrice, string?>? Confirm = null);

/// <summary>The price an order would fill at, why, and the quote it came from (null fields when there is none).</summary>
public sealed record ManualOrderPrice(decimal Price, string Basis, decimal? Bid, decimal? Ask, decimal? Ltp, DateTime? QuoteUtc);

/// <summary>A booked order: where it went, at what price, and the rows it made.</summary>
public sealed record ManualOrderFill(
    long RunId,
    string GroupId,
    string Symbol,
    string Side,
    int Quantity,
    int LotSize,
    int FilledQuantity,
    decimal Price,
    string PriceBasis,
    decimal? StopLossPrice,
    decimal? TargetPrice,
    bool CarryForward,
    long SignalId,
    string Message,
    string UnitWord,
    decimal? Bid,
    decimal? Ask,
    decimal? Ltp,
    DateTime? QuoteUtc,
    IReadOnlyList<long> PositionIds,
    IReadOnlyList<long> OrderIds);

/// <summary>
/// An order's outcome: the fill, or the refusal as the ticket answers it (<c>Refusal</c> is its 400 body,
/// <c>{ message, ... }</c>; <c>RefusalMessage</c> its words).
/// </summary>
public sealed record ManualOrderOutcome(ManualOrderFill? Fill, object? Refusal, string? RefusalMessage)
{
    public static ManualOrderOutcome Refused(string message) => new(null, new { message }, message);

    public static ManualOrderOutcome Refused(object body, string message) => new(null, body, message);
}

/// <summary>
/// Places one order into a user's manual book: priced and sized by the rules of its own segment, filled from the live
/// book (a buy pays the ask, a sell hits the bid), with an optional stop-loss and target on that position alone, and
/// intraday unless it is ticked to carry forward.
/// </summary>
/// <remarks>
/// <para>
/// Two callers since 10 Oct: the order ticket (<see cref="Controllers.ManualOrdersController.Place"/>), and the AI
/// Trader, which places its paper buys in its own book through here. Both book the same rows, so the risk guard reads
/// the levels off the position on its next sweep and the close's intraday square-off reads the carry tick, whoever
/// placed it.
/// </para>
/// <para>
/// The fills are paper, and deliberately taken from the BOOK, not the last trade: the last trade can be minutes old and
/// on the wrong side of a wide spread, and on an illiquid strike that is the difference between a fill you could have
/// had and one you could not. A price off the instrument's tick grid is refused, and so is a stop or target on the
/// wrong side of the fill, which would close the position on the guard's very next sweep.
/// </para>
/// </remarks>
public sealed class ManualOrderService(
    TradingDbContext db,
    IPaperTradingService paperTrading,
    ILotSizeResolver lotSizeResolver,
    ILogger logger,
    IDeskEventPublisher? deskEvents = null)
{
    public async Task<ManualOrderOutcome> PlaceAsync(ManualOrderCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Symbol)) return ManualOrderOutcome.Refused("symbol is required.");

        string symbol = command.Symbol.Trim();
        string side = (command.Side ?? string.Empty).Trim().ToUpperInvariant();

        if (side != "BUY" && side != "SELL") return ManualOrderOutcome.Refused("side must be BUY or SELL.");
        if (command.Quantity < 1) return ManualOrderOutcome.Refused("quantity must be at least 1.");

        var instrument = await db.Instruments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == symbol && x.IsEnabled, cancellationToken);

        if (instrument is null) return ManualOrderOutcome.Refused($"{symbol} is not in the instrument master.");

        if (instrument.ExpiryDate is { } contractExpiry
            && contractExpiry < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)))
        {
            string expired = $"{symbol} expired on {contractExpiry:dd MMM yyyy} and can no longer be traded.";
            return ManualOrderOutcome.Refused(new { message = expired, expiryDate = contractExpiry }, expired);
        }

        var quote = await db.LiveQuotesLatest
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == symbol, cancellationToken);

        // The fill price, and why it is that price.
        decimal? price;
        string priceBasis;
        if (command.LimitPrice is { } limit)
        {
            if (limit <= 0) return ManualOrderOutcome.Refused("limitPrice must be greater than zero.");

            // A price off the tick grid is not a price the exchange would take,
            // and booking it here would flatter every fill that followed.
            if (instrument.TickSize is { } tick && tick > 0)
            {
                decimal steps = limit / tick;
                if (Math.Abs(steps - Math.Round(steps)) > 0.0001m)
                {
                    string offGrid = $"limitPrice {limit} is not a multiple of this instrument's tick size ({tick}).";
                    return ManualOrderOutcome.Refused(new { message = offGrid, tickSize = tick }, offGrid);
                }
            }

            price = limit;
            priceBasis = "limit";
        }
        else
        {
            price = side == "BUY"
                ? quote?.AskPrice ?? quote?.LastTradedPrice
                : quote?.BidPrice ?? quote?.LastTradedPrice;

            priceBasis = side == "BUY"
                ? (quote?.AskPrice is not null ? "ask" : "last trade")
                : (quote?.BidPrice is not null ? "bid" : "last trade");
        }

        if (price is not > 0)
        {
            // Inventing a price here would defeat the same refusal the strategy
            // path makes for an unpriced opening group.
            return ManualOrderOutcome.Refused($"No live price for {symbol}. Add it to the watchlist and wait for a tick, or give a limitPrice.");
        }

        if (command.Confirm?.Invoke(new ManualOrderPrice(price.Value, priceBasis, quote?.BidPrice, quote?.AskPrice, quote?.LastTradedPrice,
                quote?.UpdatedUtc)) is { } refused)
        {
            return ManualOrderOutcome.Refused(refused);
        }

        // The stop and target belong to THIS order, so they are checked against
        // the side: a long is stopped below and takes profit above, a short the
        // other way round. Levels on the wrong side would close the position on
        // the guard's very next sweep, seconds after it opened.
        bool isBuy = side == "BUY";
        foreach (var (level, name, mustBeBelow) in new[]
                 {
                     (command.StopLossPrice, "stopLossPrice", isBuy),
                     (command.TargetPrice, "targetPrice", !isBuy)
                 })
        {
            if (level is not { } value) continue;

            if (value <= 0) return ManualOrderOutcome.Refused($"{name} must be greater than zero.");
            if (mustBeBelow && value >= price) return ManualOrderOutcome.Refused($"{name} {value} must be BELOW the {price} fill for a {side}.");
            if (!mustBeBelow && value <= price) return ManualOrderOutcome.Refused($"{name} {value} must be ABOVE the {price} fill for a {side}.");
        }

        var lot = await lotSizeResolver.ResolveAsync(symbol, cancellationToken);
        long userId = command.UserId;
        string userName = command.UserName;

        var book = await ManualBook.FindOrCreateAsync(db, userId, logger, cancellationToken);

        var groupId = $"MANUAL-{Guid.NewGuid():N}"[..20];
        var fields = new Dictionary<string, object?>
        {
            ["manual"] = true,
            ["by"] = userName,
            ["priceBasis"] = priceBasis,
            ["segment"] = instrument.Segment,
            ["instrumentType"] = instrument.InstrumentType,
            ["lotSize"] = lot.LotSize,
            ["tickSize"] = instrument.TickSize,
            ["bid"] = quote?.BidPrice,
            ["ask"] = quote?.AskPrice,
            ["ltp"] = quote?.LastTradedPrice,
        };
        if (command.Source is not null) fields["source"] = command.Source;
        var metadata = JsonSerializer.Serialize(fields);

        var signal = new CreateSimulationSignalRequest
        {
            SimulationRunId = book.Id,
            StrategyName = Controllers.ManualOrdersController.BookStrategyName,
            SignalType = "OPEN_GROUP",
            TimestampUtc = DateTime.UtcNow,
            Symbol = symbol,
            Price = price,
            GroupId = groupId,
            MetadataJson = metadata,
            // Quantity is LOTS to the engine, which multiplies by the lot size.
            // For a share the resolver gives 1, so this is the share count.
            Legs = new List<SimulationSignalLegRequest>
            {
                new() { Symbol = symbol, Side = side, Quantity = command.Quantity, Price = price }
            },
            // The ticket has decided the price — the limit, or the bid or ask
            // it just showed the person — and the reply quotes it.
            LegPricesAreFinal = true
        };

        var result = await paperTrading.CreateSignalAsync(signal, cancellationToken);

        // Written after the fill, because the position does not exist until the
        // signal is booked. The guard reads the levels off the position on its
        // next sweep, ahead of any rule the run carries; the close's intraday
        // square-off reads the carry tick.
        if (command.StopLossPrice is not null || command.TargetPrice is not null || command.CarryForward)
        {
            var booked = await db.PaperPositions
                .Where(x => x.SimulationRunId == book.Id && x.GroupId == groupId)
                .ToListAsync(cancellationToken);

            foreach (var position in booked)
            {
                if (command.StopLossPrice is not null || command.TargetPrice is not null)
                {
                    position.StopLossPrice = command.StopLossPrice;
                    position.TargetPrice = command.TargetPrice;
                    position.UpdatedUtc = DateTime.UtcNow;
                }

                // Either one asking to carry is enough. Each ticket order opens a
                // group of its own today, so this only ever meets the position it
                // just opened; should an order ever add to a held position, an
                // unticked add must not quietly turn a carried position intraday.
                position.CarryForward = position.CarryForward || command.CarryForward;
            }

            await db.SaveChangesAsync(cancellationToken);

            // The fill's own events went out when the signal was booked, a
            // moment before these levels and the tick were written; a page that
            // fetched on those could show the position without them. This one
            // says the position is now complete.
            var now = DateTime.UtcNow;
            if (command.StopLossPrice is not null || command.TargetPrice is not null)
            {
                deskEvents.TryPublish(new DeskEvent(DeskEventKinds.Position, book.Id, userId, symbol, now,
                    $"Own levels set: stop-loss {Level(command.StopLossPrice)}, target {Level(command.TargetPrice)}"));
            }
            if (command.CarryForward)
            {
                deskEvents.TryPublish(new DeskEvent(DeskEventKinds.Carry, book.Id, userId, symbol, now,
                    "Carry forward ticked: held overnight"));
            }
        }

        var positionIds = await db.PaperPositions.AsNoTracking()
            .Where(x => x.SimulationRunId == book.Id && x.GroupId == groupId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var orderIds = await db.PaperOrders.AsNoTracking()
            .Where(x => x.SimulationRunId == book.Id && x.SimulationSignalId == result.Id)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        int units = command.Quantity * Math.Max(1, lot.LotSize);
        string unitWord = IsDerivative(instrument.InstrumentType) ? "lot" : "share";

        logger.LogInformation(
            "Manual {Side} {Quantity} {Unit}(s) of {Symbol} at {Price} ({Basis}) by {By} into book {RunId}, {Product}.",
            side, command.Quantity, unitWord, symbol, price, priceBasis, userName, book.Id,
            command.CarryForward ? "carry forward" : "intraday");

        string message = $"{side} {command.Quantity} {unitWord}{(command.Quantity == 1 ? "" : "s")} "
                         + $"({units} qty) of {symbol} at {price} — filled on the {priceBasis}. "
                         + (command.CarryForward
                             ? "Carried forward: held overnight until you close it."
                             : $"Intraday: squared off at the {(symbol.StartsWith("MCX:", StringComparison.OrdinalIgnoreCase) ? "MCX close" : "close (15:30 IST)")}.");

        return new ManualOrderOutcome(new ManualOrderFill(
            book.Id, groupId, symbol, side, command.Quantity, lot.LotSize, units, price.Value, priceBasis, command.StopLossPrice,
            command.TargetPrice, command.CarryForward, result.Id, message, unitWord, quote?.BidPrice, quote?.AskPrice, quote?.LastTradedPrice,
            quote?.UpdatedUtc, positionIds, orderIds), null, null);
    }

    private static string Level(decimal? price) =>
        price is { } value ? value.ToString("0.00", CultureInfo.InvariantCulture) : "none";

    public static bool IsDerivative(string? instrumentType) =>
        instrumentType is "CE" or "PE" or "FUT";
}
