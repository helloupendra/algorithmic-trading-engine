// src/AlgoTrading.Api/Controllers/ManualOrdersController.cs

using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Orders placed by hand, in any segment, into a paper book of their own.
/// </summary>
/// <remarks>
/// Everything the platform books until now comes from a strategy. This is the
/// other half: an order the operator decides on — an MCX future, an equity, an
/// option — priced and sized by the rules of ITS OWN segment rather than by one
/// set of assumptions borrowed from index options.
///
/// Two of those rules are per-instrument and are read from the master rather
/// than guessed:
///   - tick size: MCX crude trades in 1.00 on the future and 0.10 on its
///     options, a BANKNIFTY option in 0.05, an NSE share in 0.10. A price that
///     is not a multiple of it is not a price the exchange would take.
///   - lot size: derivatives trade in lots, equity in shares. The resolver
///     returns 1 for a share, which makes "quantity" mean shares there and
///     lots everywhere else without the caller having to know the difference.
///
/// The fills are paper, and deliberately taken from the BOOK, not the last
/// trade: a buy pays the ask and a sell hits the bid. The last trade can be
/// minutes old and on the wrong side of a wide spread, and on an illiquid
/// strike that is the difference between a fill you could have had and one you
/// could not. The bid and ask arrive on every tick and were being dropped by
/// the API until this feature needed them.
///
/// Manual trades live in their own run — one book per user — so they never mix
/// into a strategy's P&amp;L, and so every tool that already understands a run
/// works on them unchanged: the position rows, the live P&amp;L, and the
/// per-position square-off.
/// </remarks>
[RequireModule(PlatformModules.Strategies)]
[ApiController]
[Route("api/[controller]")]
public class ManualOrdersController : ControllerBase
{
    /// <summary>The StrategyName a manual book carries, so it is recognisable everywhere.</summary>
    public const string BookStrategyName = "Manual";

    private readonly TradingDbContext _dbContext;
    private readonly IPaperTradingService _paperTrading;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly UpsertWatchlistItemUseCase _upsertWatchlistItem;
    private readonly ILogger<ManualOrdersController> _logger;

    public ManualOrdersController(
        TradingDbContext dbContext,
        IPaperTradingService paperTrading,
        ILotSizeResolver lotSizeResolver,
        UpsertWatchlistItemUseCase upsertWatchlistItem,
        ILogger<ManualOrdersController> logger)
    {
        _dbContext = dbContext;
        _paperTrading = paperTrading;
        _lotSizeResolver = lotSizeResolver;
        _upsertWatchlistItem = upsertWatchlistItem;
        _logger = logger;
    }

    // ----------------------------------------------------------- instrument --

    /// <summary>
    /// What it costs to trade one instrument right now, and the rules its
    /// segment imposes: tick size, lot size, and what a unit of quantity means.
    /// </summary>
    [HttpGet("instrument")]
    public async Task<IActionResult> GetInstrument(
        [FromQuery] string symbol,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return BadRequest(new { message = "symbol is required." });

        symbol = symbol.Trim();

        var instrument = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x => x.Symbol == symbol && x.IsEnabled)
            .Select(x => new
            {
                x.Symbol,
                x.Exchange,
                x.Segment,
                x.InstrumentType,
                x.Underlying,
                x.StrikePrice,
                x.ExpiryDate,
                x.TickSize
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (instrument is null)
            return NotFound(new { message = $"{symbol} is not in the instrument master." });

        var quote = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => x.Symbol == symbol)
            .Select(x => new
            {
                x.LastTradedPrice,
                x.BidPrice,
                x.AskPrice,
                x.BidSize,
                x.AskSize,
                x.Open,
                x.High,
                x.Low,
                x.Close,
                x.Volume,
                x.UpdatedUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Nothing quotes an instrument nobody asked for. The feed carries only
        // what is on the watchlist - about twenty symbols - so picking anything
        // else showed a ticket with three empty prices and no explanation. Put
        // it on the feed the moment it is looked at, exactly as the strategy
        // runner does for the contracts it resolves, and the first tick lands
        // within seconds.
        var todayIst = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        bool expired = instrument.ExpiryDate is { } expiry && expiry < todayIst;

        bool subscribing = false;
        if (quote is null && !expired)
        {
            try
            {
                await _upsertWatchlistItem.ExecuteAsync(new UpsertWatchlistItemRequest
                {
                    Symbol = symbol,
                    DataType = "symbolUpdate",
                    IsActive = true,
                    Priority = 10
                }, cancellationToken);
                subscribing = true;
                _logger.LogInformation("Manual ticket subscribed {Symbol} to the live feed.", symbol);
            }
            catch (Exception ex)
            {
                // Worth reporting, not worth failing the lookup: the rest of
                // the ticket (segment, tick, lot size) is still correct.
                _logger.LogWarning(ex, "Could not subscribe {Symbol} for the manual ticket.", symbol);
            }
        }

        // The day's move, worked out here so the ticket shows one number rather
        // than making the reader subtract two. Close is the PREVIOUS close in
        // this feed, which is what a change is measured from.
        decimal? change = quote?.LastTradedPrice is { } ltpNow && quote.Close is { } prevClose && prevClose != 0
            ? ltpNow - prevClose
            : null;
        decimal? changePercent = change is { } move && quote?.Close is { } basis && basis != 0
            ? move / basis * 100m
            : null;

        var lot = await _lotSizeResolver.ResolveAsync(symbol, cancellationToken);
        bool derivative = IsDerivative(instrument.InstrumentType);

        return Ok(new
        {
            symbol = instrument.Symbol,
            exchange = instrument.Exchange,
            segment = instrument.Segment,
            segmentLabel = SegmentLabel(instrument.Exchange, instrument.Segment, instrument.InstrumentType),
            instrumentType = instrument.InstrumentType,
            underlying = instrument.Underlying,
            strikePrice = instrument.StrikePrice,
            expiryDate = instrument.ExpiryDate,

            // The two segment rules the ticket has to obey.
            tickSize = instrument.TickSize,
            lotSize = lot.LotSize,
            lotSizeSource = lot.Source,
            // Derivatives are ordered in lots; a share is ordered in shares.
            // The resolver returns 1 for a share, so the arithmetic is the same
            // either way and only the WORD changes.
            quantityUnit = derivative ? "lots" : "shares",

            // Null rather than a stale number: an index is not quoted, and an
            // untraded strike has an empty book.
            ltp = quote?.LastTradedPrice,
            bid = quote?.BidPrice,
            ask = quote?.AskPrice,
            bidSize = quote?.BidSize,
            askSize = quote?.AskSize,
            open = quote?.Open,
            high = quote?.High,
            low = quote?.Low,
            prevClose = quote?.Close,
            volume = quote?.Volume,
            change,
            changePercent,
            quoteUpdatedUtc = quote?.UpdatedUtc,
            // True when this request put the symbol on the feed: the ticket
            // says "waiting for the first tick" instead of showing blanks.
            subscribing,
            // A contract the calendar has already retired. It will never quote
            // again, so the ticket says so plainly instead of waiting.
            expired,

            // What a market order would actually pay right now.
            buyAt = quote?.AskPrice ?? quote?.LastTradedPrice,
            sellAt = quote?.BidPrice ?? quote?.LastTradedPrice,
            tradable = !expired && quote is not null && (quote.AskPrice ?? quote.LastTradedPrice) is > 0
        });
    }

    // ---------------------------------------------------------------- book --

    /// <summary>The caller's manual book, or null when they have never placed one.</summary>
    [HttpGet("book")]
    public async Task<IActionResult> GetBook(CancellationToken cancellationToken)
    {
        long userId = User.GetRequiredUserId();
        var book = await FindBookAsync(userId, cancellationToken);

        if (book is null)
            return Ok(new { runId = (long?)null, message = "No manual trades placed yet." });

        return Ok(new { runId = book.Id, status = book.Status, startedUtc = book.StartedUtc });
    }

    // --------------------------------------------------------------- place --

    /// <param name="Symbol">Exact broker symbol, e.g. "MCX:CRUDEOIL26SEPFUT".</param>
    /// <param name="Side">BUY or SELL.</param>
    /// <param name="Quantity">Lots for a derivative, shares for equity.</param>
    /// <param name="LimitPrice">
    /// Optional. Omitted, the order fills at the far side of the book (a buy at
    /// the ask, a sell at the bid), which is what a market order would get.
    /// </param>
    /// <param name="StopLossPrice">Optional. A price level for THIS position only.</param>
    /// <param name="TargetPrice">Optional. A price level for THIS position only.</param>
    public sealed record PlaceManualOrderRequest(
        string Symbol,
        string Side,
        int Quantity,
        decimal? LimitPrice,
        decimal? StopLossPrice = null,
        decimal? TargetPrice = null);

    /// <summary>Books one paper order into the caller's manual book.</summary>
    [HttpPost]
    public async Task<IActionResult> Place(
        [FromBody] PlaceManualOrderRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Symbol))
            return BadRequest(new { message = "symbol is required." });

        string symbol = request.Symbol.Trim();
        string side = (request.Side ?? string.Empty).Trim().ToUpperInvariant();

        if (side != "BUY" && side != "SELL")
            return BadRequest(new { message = "side must be BUY or SELL." });

        if (request.Quantity < 1)
            return BadRequest(new { message = "quantity must be at least 1." });

        var instrument = await _dbContext.Instruments
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == symbol && x.IsEnabled, cancellationToken);

        if (instrument is null)
            return BadRequest(new { message = $"{symbol} is not in the instrument master." });

        if (instrument.ExpiryDate is { } contractExpiry
            && contractExpiry < DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)))
        {
            return BadRequest(new
            {
                message = $"{symbol} expired on {contractExpiry:dd MMM yyyy} and can no longer be traded.",
                expiryDate = contractExpiry
            });
        }

        var quote = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Symbol == symbol, cancellationToken);

        // The fill price, and why it is that price.
        decimal? price;
        string priceBasis;
        if (request.LimitPrice is { } limit)
        {
            if (limit <= 0)
                return BadRequest(new { message = "limitPrice must be greater than zero." });

            // A price off the tick grid is not a price the exchange would take,
            // and booking it here would flatter every fill that followed.
            if (instrument.TickSize is { } tick && tick > 0)
            {
                decimal steps = limit / tick;
                if (Math.Abs(steps - Math.Round(steps)) > 0.0001m)
                {
                    return BadRequest(new
                    {
                        message = $"limitPrice {limit} is not a multiple of this instrument's tick size ({tick}).",
                        tickSize = tick
                    });
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
            return BadRequest(new
            {
                message = $"No live price for {symbol}. Add it to the watchlist and wait for a tick, or give a limitPrice.",
            });
        }

        // The stop and target belong to THIS order, so they are checked against
        // the side: a long is stopped below and takes profit above, a short the
        // other way round. Levels on the wrong side would close the position on
        // the guard's very next sweep, seconds after it opened.
        bool isBuy = side == "BUY";
        foreach (var (level, name, mustBeBelow) in new[]
                 {
                     (request.StopLossPrice, "stopLossPrice", isBuy),
                     (request.TargetPrice, "targetPrice", !isBuy)
                 })
        {
            if (level is not { } value) continue;

            if (value <= 0)
                return BadRequest(new { message = $"{name} must be greater than zero." });

            if (mustBeBelow && value >= price)
                return BadRequest(new { message = $"{name} {value} must be BELOW the {price} fill for a {side}." });

            if (!mustBeBelow && value <= price)
                return BadRequest(new { message = $"{name} {value} must be ABOVE the {price} fill for a {side}." });
        }

        var lot = await _lotSizeResolver.ResolveAsync(symbol, cancellationToken);
        long userId = User.GetRequiredUserId();
        string userName = User.GetUserName() ?? "unknown";

        var book = await FindBookAsync(userId, cancellationToken)
                   ?? await CreateBookAsync(userId, cancellationToken);

        var groupId = $"MANUAL-{Guid.NewGuid():N}"[..20];
        var metadata = JsonSerializer.Serialize(new
        {
            manual = true,
            by = userName,
            priceBasis,
            segment = instrument.Segment,
            instrumentType = instrument.InstrumentType,
            lotSize = lot.LotSize,
            tickSize = instrument.TickSize,
            bid = quote?.BidPrice,
            ask = quote?.AskPrice,
            ltp = quote?.LastTradedPrice
        });

        var signal = new CreateSimulationSignalRequest
        {
            SimulationRunId = book.Id,
            StrategyName = BookStrategyName,
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
                new() { Symbol = symbol, Side = side, Quantity = request.Quantity, Price = price }
            }
        };

        var result = await _paperTrading.CreateSignalAsync(signal, cancellationToken);

        // Written after the fill, because the position does not exist until the
        // signal is booked. The guard reads these off the position on its next
        // sweep, ahead of any rule the run carries.
        if (request.StopLossPrice is not null || request.TargetPrice is not null)
        {
            var booked = await _dbContext.PaperPositions
                .Where(x => x.SimulationRunId == book.Id && x.GroupId == groupId)
                .ToListAsync(cancellationToken);

            foreach (var position in booked)
            {
                position.StopLossPrice = request.StopLossPrice;
                position.TargetPrice = request.TargetPrice;
                position.UpdatedUtc = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        int units = request.Quantity * Math.Max(1, lot.LotSize);
        string unitWord = IsDerivative(instrument.InstrumentType) ? "lot" : "share";

        _logger.LogInformation(
            "Manual {Side} {Quantity} {Unit}(s) of {Symbol} at {Price} ({Basis}) by {By} into book {RunId}.",
            side, request.Quantity, unitWord, symbol, price, priceBasis, userName, book.Id);

        return Ok(new
        {
            message = $"{side} {request.Quantity} {unitWord}{(request.Quantity == 1 ? "" : "s")} "
                      + $"({units} qty) of {symbol} at {price} — filled on the {priceBasis}.",
            runId = book.Id,
            groupId,
            symbol,
            side,
            quantity = request.Quantity,
            lotSize = lot.LotSize,
            filledQuantity = units,
            price,
            priceBasis,
            stopLossPrice = request.StopLossPrice,
            targetPrice = request.TargetPrice,
            signalId = result.Id
        });
    }

    // -------------------------------------------------------------- helpers --

    private Task<SimulationRun?> FindBookAsync(long userId, CancellationToken cancellationToken) =>
        _dbContext.SimulationRuns
            .Where(x => x.UserId == userId
                        && x.StrategyName == BookStrategyName
                        && x.Status == "Running")
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Opens the caller's book. One per user and long-lived: manual positions
    /// are held across days, so a book per session would scatter one running
    /// position history over many runs.
    /// </summary>
    private async Task<SimulationRun> CreateBookAsync(long userId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var book = new SimulationRun
        {
            UserId = userId,
            Mode = "LivePaper",
            // Not one instrument: the book holds whatever is traded into it.
            Symbol = "MANUAL",
            Resolution = "1m",
            ReplaySpeed = string.Empty,
            Status = "Running",
            StrategyName = BookStrategyName,
            ParametersJson = "{}",
            LastError = string.Empty,
            InitialCapital = 1_000_000m,
            CreatedUtc = now,
            StartedUtc = now
        };

        _dbContext.SimulationRuns.Add(book);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Opened manual book run {RunId} for user {UserId}.", book.Id, userId);
        return book;
    }

    private static bool IsDerivative(string? instrumentType) =>
        instrumentType is "CE" or "PE" or "FUT";

    /// <summary>"MCX commodity option", "NSE equity", ... — for the ticket's header.</summary>
    private static string SegmentLabel(string? exchange, string? segment, string? instrumentType)
    {
        string kind = instrumentType switch
        {
            "CE" or "PE" => "option",
            "FUT" => "future",
            _ => segment == "CM" ? "equity" : "instrument"
        };

        string family = segment switch
        {
            "COM" => "commodity",
            "FO" => "F&O",
            "CM" => string.Empty,
            _ => segment ?? string.Empty
        };

        return string.Join(' ', new[] { exchange, family, kind }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }
}
