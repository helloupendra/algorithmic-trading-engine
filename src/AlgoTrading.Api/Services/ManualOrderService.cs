// src/AlgoTrading.Api/Services/ManualOrderService.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Domain.Entities;
using Npgsql;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>One order for a user's manual book, as the ticket (or the AI Trader) asks for it.</summary>
/// <param name="UserId">Whose book it goes into.</param>
/// <param name="UserName">Who placed it, recorded on the order's signal.</param>
/// <param name="Quantity">Lots for a derivative, shares for equity.</param>
/// <param name="LimitPrice">
/// Omitted, the order fills at the far side of the book (a buy at the ask, a sell at the bid). Given, it fills there
/// only if that is no worse than the limit (a buy at or below it, a sell at or above it), and is refused otherwise.
/// </param>
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
    Func<ManualOrderPrice, string?>? Confirm = null,
    Guid? ClientOrderId = null);

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
public sealed record ManualOrderOutcome(ManualOrderFill? Fill, object? Refusal, string? RefusalMessage, int StatusCode = 400)
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
/// <para>
/// Since 2 Nov the price comes from the engine's own live-fill rules (<see cref="IPaperTradingService.QuoteLiveFillAsync"/>),
/// and the leg is booked live, not as final: a quote older than the paper-fill limit while the market is open is
/// refused, as it is for a strategy, and a limit fills at the book only when the book has reached it. There are no
/// resting orders: a limit the book has not reached is refused, never booked at the limit.
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
        if (command.ClientOrderId is not { } key) return await PlaceCoreAsync(command, cancellationToken);
        await using var held = await ManualOrderRequestLock.AcquireAsync(db, command.UserId, key, cancellationToken);
        return await PlaceCoreAsync(command, cancellationToken);
    }

    private async Task<ManualOrderOutcome> PlaceCoreAsync(ManualOrderCommand command, CancellationToken cancellationToken)
    {
        string requestHash = RequestHash(command);
        if (command.ClientOrderId is { } key)
        {
            if (key == Guid.Empty) return ManualOrderOutcome.Refused("clientOrderId must be a non-empty UUID.");
            if (await ReadReceiptAsync(command.UserId, key, requestHash, cancellationToken) is { } previous) return previous;
        }

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

        // The fill price, and why it is that price. A limit is first a price the
        // exchange would take: a price off the tick grid is refused, since booking
        // it here would flatter every fill that followed.
        if (command.LimitPrice is { } limit)
        {
            if (limit <= 0) return ManualOrderOutcome.Refused("limitPrice must be greater than zero.");

            if (instrument.TickSize is { } tick && tick > 0)
            {
                decimal steps = limit / tick;
                if (Math.Abs(steps - Math.Round(steps)) > 0.0001m)
                {
                    string offGrid = $"limitPrice {limit} is not a multiple of this instrument's tick size ({tick}).";
                    return ManualOrderOutcome.Refused(new { message = offGrid, tickSize = tick }, offGrid);
                }
            }
        }

        // Priced the way a strategy's live leg is (2 Nov, review F2): the far side
        // of the book, else the last trade across half the spread, and never on a
        // quote older than the paper-fill limit while the contract's market is
        // open. Until then the ticket's price went to the engine as final, so a
        // market order took a quote of any age (a feed stall's frozen ask) and a
        // limit was booked AT the limit whatever the book said: a BUY limit of
        // 0.05 on a 150 option was a position 149.95 in profit the moment it was
        // marked. The engine prices the leg again as it books it, on the same
        // rules (and holds it to the limit), so this is the price the order gets.
        var live = await paperTrading.QuoteLiveFillAsync(symbol, side, cancellationToken);
        if (live.Price is not { } price)
        {
            // Inventing a price here would defeat the same refusal the strategy
            // path makes for an unpriced opening group.
            return ManualOrderOutcome.Refused(live.QuoteUtc is null
                ? $"No live price for {symbol}. Add it to the watchlist and wait for a tick."
                : $"No live price for {symbol}: {live.Refusal}. Wait for the next tick and try again.");
        }

        // A limit is a cap, not a price: it fills at the book when the book has
        // reached it, and the ticket keeps no resting orders to wait for it.
        if (command.LimitPrice is { } cap && PaperFillPricing.BeyondLimit(side, price, live.Rule, cap) is { } beyond)
        {
            string notMarketable = $"Not filled: {beyond}. A limit fills only when the book has reached it "
                                   + "(a buy at the ask, a sell at the bid), and this ticket keeps no resting orders.";
            return ManualOrderOutcome.Refused(new { message = notMarketable, limitPrice = cap, bid = live.Bid, ask = live.Ask },
                notMarketable);
        }

        string priceBasis = Basis(live.Rule);

        if (command.Confirm?.Invoke(new ManualOrderPrice(price, priceBasis, live.Bid, live.Ask, live.Ltp, live.QuoteUtc)) is { } refused)
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
            if (mustBeBelow && value >= price) return ManualOrderOutcome.Refused($"{name} {value} must be BELOW the {Money(price)} fill for a {side}.");
            if (!mustBeBelow && value <= price) return ManualOrderOutcome.Refused($"{name} {value} must be ABOVE the {Money(price)} fill for a {side}.");
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
            ["bid"] = live.Bid,
            ["ask"] = live.Ask,
            ["ltp"] = live.Ltp,
            ["quoteUtc"] = live.QuoteUtc,
        };
        if (command.LimitPrice is not null) fields["limitPrice"] = command.LimitPrice;
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
            // Priced live by the engine, like a strategy's leg (not as final):
            // the stale-quote refusal and the spread apply, and a limit rides on
            // the leg so a book that moved past it since the check above is
            // refused there rather than filled beyond it.
            Legs = new List<SimulationSignalLegRequest>
            {
                new()
                {
                    Symbol = symbol, Side = side, Quantity = command.Quantity, Price = price,
                    LimitPrice = command.LimitPrice, StopLossPrice = command.StopLossPrice,
                    TargetPrice = command.TargetPrice, CarryForward = command.CarryForward
                }
            },
        };

        SimulationSignalResponse result;
        ManualOrderFill? receiptFill = null;
        try
        {
            result = command.ClientOrderId is null
                ? await paperTrading.CreateSignalAsync(signal, cancellationToken)
                : await paperTrading.CreateSignalWithReceiptAsync(signal, async (response, ct) =>
                {
                    receiptFill = await BuildFillAsync(response, ct);
                    db.ManualOrderReceipts.Add(new ManualOrderReceipt
                    {
                        UserId = command.UserId, ClientOrderId = command.ClientOrderId.Value,
                        RequestHash = requestHash, FillJson = JsonSerializer.Serialize(receiptFill), CreatedUtc = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);
                }, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "pk_manual_order_receipts" })
        {
            // The losing fill transaction rolled back. Read the winner through a clean tracker.
            db.ChangeTracker.Clear();
            return await ReadReceiptAsync(command.UserId, command.ClientOrderId!.Value, requestHash, cancellationToken)
                ?? throw new InvalidOperationException("The committed order receipt could not be read.");
        }
        catch (InvalidOperationException ex)
        {
            // The engine refused the signal as a whole and booked nothing: most
            // often the quote went stale, or the book moved past the limit, in
            // the moment since it was read above.
            logger.LogWarning("Manual {Side} of {Symbol} by {By} not booked: {Reason}", side, symbol, userName, ex.Message);
            return ManualOrderOutcome.Refused($"Not filled: {ex.Message}");
        }

        // The fill, its levels and the carry tick have committed together.
        // These events describe that completed booking; no follow-up write is
        // needed before the guard or a page can read the protected position.
        if (command.StopLossPrice is not null || command.TargetPrice is not null || command.CarryForward)
        {
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

        var fill = receiptFill ?? await BuildFillAsync(result, cancellationToken);
        logger.LogInformation("Manual {Side} of {Symbol} by {By} committed to book {RunId}, signal {SignalId}.",
            side, symbol, userName, fill.RunId, fill.SignalId);
        return new ManualOrderOutcome(fill, null, null);

        async Task<ManualOrderFill> BuildFillAsync(SimulationSignalResponse result, CancellationToken cancellationToken)
        {
            var positionIds = await db.PaperPositions.AsNoTracking()
                .Where(x => x.SimulationRunId == book.Id && x.GroupId == groupId)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            var orders = await db.PaperOrders.AsNoTracking()
                .Where(x => x.SimulationRunId == book.Id && x.SimulationSignalId == result.Id)
                .Select(x => new { x.Id, x.FillPrice })
                .ToListAsync(cancellationToken);
            var orderIds = orders.Select(x => x.Id).ToList();

            // What the engine booked, which is what the person is told: the same
            // quote as the check above, unless it ticked in between.
            if (orders.FirstOrDefault()?.FillPrice is { } bookedPrice && bookedPrice > 0) price = bookedPrice;

            int units = command.Quantity * Math.Max(1, lot.LotSize);
            string unitWord = IsDerivative(instrument.InstrumentType) ? "lot" : "share";

            string message = $"{side} {command.Quantity} {unitWord}{(command.Quantity == 1 ? "" : "s")} "
                             + $"({units} qty) of {symbol} at {Money(price)} — filled on the {priceBasis}"
                             + (command.LimitPrice is { } within ? $", within your limit {Money(within)}. " : ". ")
                             + (command.CarryForward
                                 ? "Carried forward: held overnight until you close it."
                                 : $"Intraday: squared off at the {PositionCarryForward.CloseName(symbol, IstTime.DateOf(DateTime.UtcNow), instrument.ExpiryDate)}.");

            return new ManualOrderFill(
                book.Id, groupId, symbol, side, command.Quantity, lot.LotSize, units, price, priceBasis, command.StopLossPrice,
                command.TargetPrice, command.CarryForward, result.Id, message, unitWord, live.Bid, live.Ask, live.Ltp,
                live.QuoteUtc, positionIds, orderIds);
        }
    }

    public async Task<ManualOrderOutcome?> ReadReceiptAsync(long userId, Guid key, string? requestHash, CancellationToken ct)
    {
        var receipt = await db.ManualOrderReceipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.UserId == userId && r.ClientOrderId == key, ct);
        if (receipt is null) return null;
        if (requestHash is not null && receipt.RequestHash != requestHash)
            return new(null, new { message = "clientOrderId was already used for a different order." },
                "clientOrderId was already used for a different order.", 409);
        return new(JsonSerializer.Deserialize<ManualOrderFill>(receipt.FillJson)
            ?? throw new InvalidOperationException("Invalid order receipt."), null, null);
    }

    private static string RequestHash(ManualOrderCommand command)
    {
        string? Number(decimal? value) => value?.ToString("G29", CultureInfo.InvariantCulture);
        var payload = JsonSerializer.Serialize(new
        {
            Symbol = command.Symbol?.Trim(), Side = command.Side?.Trim().ToUpperInvariant(), command.Quantity,
            Limit = Number(command.LimitPrice), Stop = Number(command.StopLossPrice), Target = Number(command.TargetPrice),
            command.CarryForward
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>How the fill was priced, in the words the reply uses ("filled on the ask").</summary>
    private static string Basis(string? rule) => rule switch
    {
        "ask" => "ask",
        "bid" => "bid",
        "ltp-plus-half-spread" => "last trade plus half the spread",
        "ltp-less-half-spread" => "last trade less half the spread",
        _ => rule ?? "quote",
    };

    private static string Level(decimal? price) =>
        price is { } value ? value.ToString("0.00", CultureInfo.InvariantCulture) : "none";

    /// <summary>A price as the reply words it: two places, more only when the fill has them (the half-spread).</summary>
    private static string Money(decimal price) => price.ToString("0.00##", CultureInfo.InvariantCulture);

    public static bool IsDerivative(string? instrumentType) =>
        instrumentType is "CE" or "PE" or "FUT";
}
