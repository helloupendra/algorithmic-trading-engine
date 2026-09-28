// src/AlgoTrading.Infrastructure/Services/PaperTradingService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Backtest;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;

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
///
/// Every write to a run's positions (signal fills, guard closes, flatten,
/// marks) is serialized per run through <see cref="SimulationRunLocks"/>:
/// the writers come from different scopes (runner requests, the risk guard,
/// the stop pipeline) and PaperPosition has no concurrency token.
/// CLOSE_GROUP legs are reduce-only, as in the backtest ledger.
///
/// A live fill is priced here, from the latest quote, never taken as the
/// runner sent it: the bid for a SELL, the ask for a BUY, else the last trade
/// less or plus half the spread (<see cref="PaperFillOptions"/>), and never
/// from a quote too old to be a price while the market is open. Each order
/// row records which rule priced it.
///
/// Every write a desk page shows — a booked signal and its fills, a closed or
/// settled position, a carry — is announced once it has committed
/// (<see cref="IDeskEventPublisher"/>), so the page fetches again at once
/// instead of on its next poll. Replays announce nothing: a backtest books
/// thousands of fills that no desk page is watching.
/// </summary>
public class PaperTradingService : IPaperTradingService
{
    public const string LivePaperMode = "LivePaper";
    public const string OfflineReplayMode = "OfflineReplay";
    public const string BacktestSummarySignalType = "BACKTEST_SUMMARY";

    /// <summary>A strategy run's leg that was moved to the owner's manual book at the close.</summary>
    public const string CarriedStatus = "Carried";

    /// <summary>The carry-forward tick of a position was changed (who, when, which way).</summary>
    public const string CarryForwardSignalType = "CARRY_FORWARD";

    /// <summary>In the run: a ticked leg left for the owner's manual book at the close.</summary>
    public const string CarryOutSignalType = "CARRY_OUT";

    /// <summary>In the manual book: a leg arrived from a strategy run at the close.</summary>
    public const string CarryInSignalType = "CARRY_IN";

    private const string RunStatusStopping = "Stopping";
    private const string RunStatusStopped = "Stopped";
    private const string RunStatusCompleted = "Completed";
    private const string RunStatusFailed = "Failed";

    private const int MaxEquitySnapshotBatch = 5000;

    /// <summary>Longest <see cref="CreateSimulationSignalRequest.ClientSignalId"/> accepted (the column's width).</summary>
    public const int MaxClientSignalIdLength = 64;

    private readonly TradingDbContext _dbContext;
    private readonly IRiskManagementService _riskManagementService;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly IMarketSessionService? _marketSessions;
    private readonly PaperFillOptions _fills;
    private readonly TimeProvider _time;
    private readonly IDeskEventPublisher? _deskEvents;

    /// <param name="marketSessions">
    /// Says whether a contract's market is open, which is when a quote's age
    /// is judged. Always registered in the API; null only in tests that
    /// predate it, where no quote is refused for its age.
    /// </param>
    /// <param name="deskEvents">
    /// Tells the console what changed. Registered in the API; null where
    /// nobody is watching (tests, and any host without the hub).
    /// </param>
    public PaperTradingService(
        TradingDbContext dbContext,
        IRiskManagementService riskManagementService,
        ILotSizeResolver lotSizeResolver,
        IMarketSessionService? marketSessions = null,
        IOptions<PaperFillOptions>? fillOptions = null,
        TimeProvider? time = null,
        IDeskEventPublisher? deskEvents = null)
    {
        _dbContext = dbContext;
        _riskManagementService = riskManagementService;
        _lotSizeResolver = lotSizeResolver;
        _marketSessions = marketSessions;
        _fills = fillOptions?.Value ?? new PaperFillOptions();
        _time = time ?? TimeProvider.System;
        _deskEvents = deskEvents;
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

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
        string? clientSignalId = string.IsNullOrWhiteSpace(request.ClientSignalId) ? null : request.ClientSignalId.Trim();
        if (clientSignalId is { Length: > MaxClientSignalIdLength })
            throw new InvalidOperationException($"clientSignalId may be at most {MaxClientSignalIdLength} characters.");

        // Serialized with the run's other position writers (guard closes, the
        // stop's flatten): the status check, the lookups and the fills below
        // must see one consistent state of the run's positions.
        using var gate = await SimulationRunLocks.AcquireAsync(request.SimulationRunId, cancellationToken);

        // A retry of a signal this run already booked. The runner retries when
        // an answer is lost — a timeout, a dropped connection — and cannot tell
        // whether the first post was booked; this is how it finds out, without
        // booking an OPEN_GROUP twice. Looked up before the status check: a
        // signal booked just before a stop began was still booked.
        if (clientSignalId is not null)
        {
            var booked = await _dbContext.SimulationSignals
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.SimulationRunId == request.SimulationRunId && x.ClientSignalId == clientSignalId,
                    cancellationToken);
            if (booked is not null) return MapSignal(booked);
        }

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
        var legs = request.Legs ?? new List<SimulationSignalLegRequest>();

        var signal = new SimulationSignal
        {
            SimulationRunId = request.SimulationRunId,
            StrategyName = request.StrategyName,
            SignalType = request.SignalType,
            TimestampUtc = timestampUtc,
            GroupId = request.GroupId,
            MetadataJson = string.IsNullOrWhiteSpace(request.MetadataJson) ? "{}" : request.MetadataJson,
            ClientSignalId = clientSignalId,
            CreatedUtc = UtcNow
        };

        // A CLOSE_GROUP (or any square-off / risk-rule signal) may only shrink
        // or close what is open — exactly like the backtest ledger. The strategy
        // builds its closing legs from its own state, so after the risk guard
        // has already closed a leg the strategy's later CLOSE_GROUP for that leg
        // must be skipped, not filled as a fresh reverse position.
        bool reduceOnly = IsReduceOnlySignal(request.SignalType, signal.MetadataJson);

        // Every leg gets a real price before ANY of them fills. Done inside the
        // loop instead, a group could half-fill — leg one booked, leg two
        // rejected — and a one-legged "straddle" is a worse position to wake up
        // to than no position at all. A replay's bar closes and the manual
        // ticket's prices are filled as given; a live run's legs are priced
        // from the latest quote.
        var fills = await PriceLegsAsync(legs, signal, reduceOnly,
            pricesAreFinal: replay || request.LegPricesAreFinal, cancellationToken);

        // The signal and all its legs commit together or none of them do.
        //
        // Each leg used to SaveChanges on its own, so a group that failed half
        // way — a risk refusal, a lost connection, an unpriceable strike — left
        // the legs before it filled and the legs after it absent. A one-legged
        // "straddle" is naked risk that nobody chose, and it survives in the
        // database looking like a real position. The signal row is inside too:
        // a refused signal left behind with its client id would answer the
        // runner's retry as if it had been booked.
        //
        // Cheap to hold: the legs of one signal are a handful of rows, and the
        // per-run lock above already serialises every other writer.
        var bookedLegs = new List<BookedLeg>(legs.Count);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await _dbContext.SimulationSignals.AddAsync(signal, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Convert signal -> orders -> positions. Replays are clocked by the
            // bar time and skip the wall-clock risk gate (rate limit / daily
            // loss). A recap is clocked by the replayed session the runner
            // stamped the signal with, but keeps the gate: it trades in real
            // time, like a live run.
            bool recap = !replay && RecapClock.IsRecap(run.ParametersJson);
            DateTime? clock = replay || recap ? timestampUtc : null;

            for (int i = 0; i < legs.Count; i++)
            {
                await CreateOrderAndApplyPositionAsync(signal, legs[i], fills[i], cancellationToken,
                    bypassRiskCheck: replay, reduceOnly: reduceOnly, atUtc: clock, booked: bookedLegs);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        PublishBooked(run, signal, bookedLegs, reason: null);

        return MapSignal(signal);
    }

    /// <summary>
    /// A fill price for every leg, in order, or the whole signal is refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A live leg is priced from the contract's latest quote whether or not the
    /// runner sent a price: the bid for a SELL, the ask for a BUY, else the last
    /// trade less or plus the half-spread (<see cref="PaperFillPricing"/>). What
    /// the runner sent is kept as the order's requested price, and used only
    /// when there is no quote at all.
    /// </para>
    /// <para>
    /// While the contract's market is open, a quote older than
    /// <see cref="PaperFillOptions.MaxQuoteAgeSeconds"/> is not a price. On 24
    /// Sep the feed stalled from 11:27:36 to 11:34:06 and every quote in the
    /// table froze with it, still looking current. Such a leg is left unpriced
    /// and the signal refused with the quote's age; the runner puts the
    /// strategy back as it was and it asks again on a later tick. The stops a
    /// person or the close asks for do not come through here — they always
    /// close (<see cref="CloseOpenPositionsAsync"/>).
    /// </para>
    /// <para>
    /// Never zero: filled at the zero a null used to become, a sold straddle
    /// booked no premium and the run's entire P&amp;L was fiction, silently.
    /// An opening leg with no price has nothing honest to fall back to. A
    /// closing leg outside market hours may still fall back to the position's
    /// last mark, then its entry — getting flat is worth a stale price when
    /// there is no market to be wrong against.
    /// </para>
    /// </remarks>
    private async Task<List<PaperFill>> PriceLegsAsync(
        List<SimulationSignalLegRequest> legs,
        SimulationSignal signal,
        bool reduceOnly,
        bool pricesAreFinal,
        CancellationToken cancellationToken)
    {
        if (legs.Count == 0) return new List<PaperFill>();

        foreach (var leg in legs) ValidateLeg(leg);

        var symbols = legs
            .Where(x => !pricesAreFinal || x.Price is null or <= 0m)
            .Select(x => x.Symbol)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var quotes = await LoadLiveQuotesAsync(symbols, cancellationToken);

        // Only a closing leg may look to the position it is closing.
        Dictionary<string, PaperPosition> openBySymbol = reduceOnly && symbols.Count > 0
            ? await _dbContext.PaperPositions
                .AsNoTracking()
                .Where(x => x.SimulationRunId == signal.SimulationRunId
                            && x.GroupId == signal.GroupId
                            && x.Status == "Open"
                            && symbols.Contains(x.Symbol))
                .ToDictionaryAsync(x => x.Symbol, StringComparer.Ordinal, cancellationToken)
            : new Dictionary<string, PaperPosition>(StringComparer.Ordinal);

        var now = UtcNow;
        var fills = new List<PaperFill>(legs.Count);
        var unpriced = new List<string>();

        foreach (var leg in legs)
        {
            QuoteSnapshot? quote = quotes.TryGetValue(leg.Symbol, out var q) ? q : null;
            openBySymbol.TryGetValue(leg.Symbol, out var open);

            string? why = "no live quote";
            var fill = pricesAreFinal
                ? PriceAsGiven(leg, quote, open)
                : PriceLive(leg, quote, open, now, out why);

            if (fill is null)
            {
                unpriced.Add($"{leg.Symbol} ({why})");
                continue;
            }

            fills.Add(fill);
        }

        if (unpriced.Count > 0)
        {
            throw new InvalidOperationException(
                $"No fill price for {string.Join(", ", unpriced)}; the {signal.SignalType} signal was rejected rather "
                + "than filled at a stale or invented price. Check that the feed is running and subscribed to the contract.");
        }

        return fills;
    }

    /// <summary>A live leg's fill from the latest quote; null, with the reason, when there is none it may use.</summary>
    private PaperFill? PriceLive(
        SimulationSignalLegRequest leg,
        QuoteSnapshot? quote,
        PaperPosition? open,
        DateTime nowUtc,
        out string? why)
    {
        why = null;
        bool judged = QuoteAgeApplies(leg.Symbol, nowUtc);

        if (quote is { } q)
        {
            double age = q.AgeSeconds(nowUtc);
            if (judged && age > _fills.MaxQuoteAgeSeconds)
            {
                why = $"its latest quote is {PaperFillPricing.Seconds(age)} s old; while the market is open a fill "
                      + $"needs one under {_fills.MaxQuoteAgeSeconds} s";
                return null;
            }

            if (PaperFillPricing.FromQuote(leg.Side, q, _fills, nowUtc) is { } fromQuote)
                return fromQuote;
        }

        // No usable quote: a price the strategy put on the leg itself is not a
        // quote whose age can be judged, and it is what it asked for.
        if (leg.Price is > 0m)
            return PaperFillPricing.FromReference(leg.Side, leg.Price.Value, "signal", _fills);

        if (!judged && open is not null)
        {
            if (open.LastMarkPrice is > 0m)
                return PaperFillPricing.FromReference(leg.Side, open.LastMarkPrice.Value, "mark", _fills);
            if (open.AveragePrice > 0m)
                return PaperFillPricing.FromReference(leg.Side, open.AveragePrice, "entry", _fills);
        }

        why = "no live quote";
        return null;
    }

    /// <summary>
    /// A replay's or the manual ticket's leg: its own price as it came. One
    /// sent without a price takes the last trade, then (closing only) the
    /// position's last mark, then its entry — as before fills had a spread.
    /// </summary>
    private static PaperFill? PriceAsGiven(SimulationSignalLegRequest leg, QuoteSnapshot? quote, PaperPosition? open)
    {
        if (leg.Price is > 0m) return PaperFillPricing.AsGiven(leg.Price.Value);

        if (quote?.LastTradedPrice is { } ltp && ltp > 0m)
            return PaperFillPricing.AsGiven(ltp, "ltp", "no price with the signal: filled at the last trade");

        if (open?.LastMarkPrice is { } mark && mark > 0m)
            return PaperFillPricing.AsGiven(mark, "mark", "no price with the signal: filled at the position's last mark");

        if (open is { AveragePrice: > 0m })
            return PaperFillPricing.AsGiven(open.AveragePrice, "entry", "no price with the signal: filled at the entry price");

        return null;
    }

    /// <summary>
    /// Whether a quote's age decides if it may price a fill of this contract:
    /// while its market is open. Out of hours every quote is old and the last
    /// one is the only price there is. A contract whose market this desk keeps
    /// no hours for is judged, since nothing says its quote may be old.
    /// </summary>
    private bool QuoteAgeApplies(string symbol, DateTime nowUtc)
    {
        if (_marketSessions is null) return false;

        int colon = symbol.IndexOf(':');
        string exchange = colon > 0 ? symbol[..colon].Trim().ToUpperInvariant() : string.Empty;

        try
        {
            return _marketSessions.IsMarketOpen(nowUtc, exchange, exchange == "MCX" ? "COM" : "FO");
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return true;
        }
    }

    /// <summary>The checks every leg must pass before anything is priced or written; returns its side, upper case.</summary>
    private static string ValidateLeg(SimulationSignalLegRequest leg)
    {
        if (string.IsNullOrWhiteSpace(leg.Symbol))
            throw new InvalidOperationException("Paper leg symbol is required.");

        if (string.IsNullOrWhiteSpace(leg.Side))
            throw new InvalidOperationException("Paper leg side is required.");

        if (leg.Quantity <= 0)
            throw new InvalidOperationException("Paper leg quantity must be greater than zero.");

        string side = leg.Side.Trim().ToUpperInvariant();
        if (side != "BUY" && side != "SELL")
            throw new InvalidOperationException("Paper leg side must be BUY or SELL.");

        return side;
    }

    /// <summary>
    /// True for signals whose legs only ever close positions: CLOSE_GROUP, and
    /// anything whose metadata marks it as a square-off or a risk-rule action.
    /// </summary>
    internal static bool IsReduceOnlySignal(string? signalType, string? metadataJson)
    {
        if (string.Equals(signalType?.Trim(), "CLOSE_GROUP", StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(metadataJson)) return false;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return false;

            if (doc.RootElement.TryGetProperty("square_off", out var squareOff)
                && squareOff.ValueKind is System.Text.Json.JsonValueKind.True)
                return true;

            return doc.RootElement.TryGetProperty("risk_rule", out var riskRule)
                   && riskRule.ValueKind == System.Text.Json.JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(riskRule.GetString());
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
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
        // The marks below are written back; under the run's gate so a leg
        // closed concurrently is never re-marked as open by a stale snapshot.
        using var gate = await SimulationRunLocks.AcquireAsync(simulationRunId, cancellationToken);

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

            MarkOpenPositions(rows, latestQuotes, lotSizes);

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
            MarkOpenPositions(positions, latestQuotes, lotSizes);
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
            MarkOpenPositions(positions, latestQuotes, lotSizes);
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
                SnapshotUtc = UtcNow,
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
            int flattened = await FlattenRunAsync(runId, "GLOBAL_KILL_SWITCH", cancellationToken);
            if (flattened > 0)
            {
                await PublishAsync(runId, DeskEventKinds.Risk, symbol: null,
                    $"Kill switch: {Plural(flattened, "position")} squared off");
            }
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

        // Snapshot and square-off under the run's gate: nothing else can fill
        // or close a leg of this run between the two.
        using var gate = await SimulationRunLocks.AcquireAsync(simulationRunId, cancellationToken);

        var openPositions = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId && x.Status == "Open")
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var metadata = System.Text.Json.JsonSerializer.Serialize(new { reason, system = true });
        return await CloseOpenPositionsAsync(run, openPositions, metadata, reason, cancellationToken);
    }

    public async Task<int> ClosePositionsAsync(
        long simulationRunId,
        IEnumerable<long> positionIds,
        string reason,
        string by,
        CancellationToken cancellationToken = default)
    {
        var ids = positionIds.Distinct().ToList();
        if (ids.Count == 0) return 0;

        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        // Under the run's gate, so a runner's CLOSE_GROUP or another stopper
        // cannot fill the same leg between this snapshot and the fills. Only
        // what is still open: a leg another closer got to first is skipped
        // here and again (reduce-only) at fill time.
        using var gate = await SimulationRunLocks.AcquireAsync(simulationRunId, cancellationToken);

        var openPositions = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId && x.Status == "Open" && ids.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var metadata = System.Text.Json.JsonSerializer.Serialize(new { reason, by, system = true });
        return await CloseOpenPositionsAsync(run, openPositions, metadata, reason, cancellationToken);
    }

    /// <summary>
    /// The shared square-off path of <see cref="FlattenRunAsync"/> and
    /// <see cref="ClosePositionsAsync"/>: one CLOSE_GROUP signal per group with
    /// a reduce-only closing leg per open position at the latest live quote
    /// (replays: the last stored mark), falling back to the last mark, then the
    /// entry. Returns the number of positions actually closed.
    /// </summary>
    /// <remarks>
    /// Every caller here is someone asking to get out — a person's Stop or
    /// close, the market close, the risk guard, the kill switch — so a quote too
    /// old to open a position on still closes one: a leg left open overnight
    /// because its feed stalled at 11:27 is worse than a leg closed at 11:27's
    /// price. The fill says so ("priced on a stale quote (N s old)"), and like
    /// any live market fill it crosses the spread.
    /// </remarks>
    private async Task<int> CloseOpenPositionsAsync(
        SimulationRun run,
        List<PaperPosition> openPositions,
        string metadata,
        string reason,
        CancellationToken cancellationToken)
    {
        if (openPositions.Count == 0) return 0;

        long simulationRunId = run.Id;
        bool replay = IsReplay(run.Mode);

        // Live runs square off at the latest quote; replays at the last stored
        // bar-close mark, stamped at the time of that mark so the closing rows
        // stay on the historical timeline. A recap squares off at the latest
        // quote too, stamped at the replayed session's time so its exits sit on
        // the same timeline as the entries the runner stamped.
        var latestQuotes = replay
            ? new Dictionary<string, QuoteSnapshot>(StringComparer.Ordinal)
            : await LoadLiveQuotesAsync(openPositions.Select(x => x.Symbol).Distinct().ToList(), cancellationToken);

        DateTime? recapNow = replay ? null : await RecapClock.NowAsync(_dbContext, run, cancellationToken);

        var now = UtcNow;
        DateTime atUtc = replay
            ? openPositions.Max(x => x.UpdatedUtc)
            : recapNow ?? now;

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
                CreatedUtc = now
            };

            await _dbContext.SimulationSignals.AddAsync(signal, cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);

            var booked = new List<BookedLeg>();
            foreach (var pos in group)
            {
                var leg = new SimulationSignalLegRequest
                {
                    Symbol = pos.Symbol,
                    Side = pos.Direction == "LONG" ? "SELL" : "BUY",
                    Quantity = pos.Quantity,
                };

                var fill = replay
                    ? PaperFillPricing.AsGiven(pos.LastMarkPrice ?? pos.AveragePrice, "mark", "filled at the last stored mark")
                    : SquareOffFill(leg.Side, pos, latestQuotes.TryGetValue(pos.Symbol, out var q) ? q : null, now);

                // This IS the risk action, so it bypasses the risk evaluation.
                // Reduce-only: if another stopper closed this position between the
                // snapshot above and now, the closing leg is skipped instead of
                // opening a reverse position on a run that is being stopped.
                bool closedLeg = await CreateOrderAndApplyPositionAsync(
                    signal, leg, fill, cancellationToken, bypassRiskCheck: true, reduceOnly: true,
                    atUtc: replay || recapNow.HasValue ? atUtc : null, booked: booked);
                if (closedLeg) closed++;
            }

            // Each leg was saved on its own above, so the group's fills are all in.
            PublishBooked(run, signal, booked, reason);
        }

        return closed;
    }

    /// <summary>
    /// A live square-off's fill: the quote whatever its age (and saying how old
    /// it was), else the position's last mark, else its entry — across the
    /// spread either way.
    /// </summary>
    private PaperFill SquareOffFill(string side, PaperPosition pos, QuoteSnapshot? quote, DateTime nowUtc)
    {
        if (quote is { } q && PaperFillPricing.FromQuote(side, q, _fills, nowUtc) is { } fromQuote)
            return fromQuote;

        return pos.LastMarkPrice is > 0m
            ? PaperFillPricing.FromReference(side, pos.LastMarkPrice.Value, "mark", _fills)
            : PaperFillPricing.FromReference(side, pos.AveragePrice, "entry", _fills);
    }

    // ---------------------------------------------------------------------
    // EXPIRY SETTLEMENT
    // ---------------------------------------------------------------------

    /// <summary>
    /// Settles one expired position at <paramref name="settlementPrice"/>
    /// under the run's gate. No order row is written, on purpose.
    /// </summary>
    /// <remarks>
    /// Every other close in this class books a fill, and RunCharges charges
    /// every fill: brokerage per order, plus turnover charges on its premium.
    /// An expiry is not a trade. The exchange settles it; no order is placed and
    /// no brokerage is paid, so a settlement booked as a fill would be charged
    /// as a second round trip that never happened (and an out-of-the-money leg
    /// settling at 0 would still pay a whole order's brokerage).
    /// <para>
    /// The row reads like any other closed leg: quantity 0, realized P&amp;L at
    /// the settlement price, the settlement price as its exit (the last mark),
    /// and one CLOSE_GROUP signal whose reason names the settlement. The
    /// position view counts the lots a closed leg traded from its opening
    /// fills, which are untouched.
    /// </para>
    /// </remarks>
    public async Task<bool> SettleExpiredPositionAsync(
        long simulationRunId,
        long positionId,
        decimal settlementPrice,
        string metadataJson,
        DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        if (settlementPrice < 0m)
            throw new InvalidOperationException($"A settlement price cannot be negative ({settlementPrice}).");

        using var gate = await SimulationRunLocks.AcquireAsync(simulationRunId, cancellationToken);

        var position = await _dbContext.PaperPositions
            .FirstOrDefaultAsync(x => x.Id == positionId && x.SimulationRunId == simulationRunId, cancellationToken);

        // Already closed (by hand, by the guard, or by an earlier pass of the
        // settler): nothing to do, and nothing written.
        if (position is null || position.Status != "Open" || position.Quantity <= 0) return false;

        var lotSizes = await ResolveLotSizesForRunAsync(simulationRunId, new[] { position.Symbol }, cancellationToken);
        int lotSize = LotSizeOf(lotSizes, position.Symbol);

        var signal = new SimulationSignal
        {
            SimulationRunId = simulationRunId,
            StrategyName = position.StrategyName,
            SignalType = "CLOSE_GROUP",
            TimestampUtc = atUtc,
            GroupId = position.GroupId,
            MetadataJson = string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson,
            CreatedUtc = UtcNow
        };

        position.RealizedPnl += CalculateRealizedPnl(
            position.Direction, position.AveragePrice, settlementPrice, position.Quantity, lotSize);
        position.Quantity = 0;
        position.Status = "Closed";
        position.ClosedUtc = atUtc;
        position.LastMarkPrice = settlementPrice;
        position.UnrealizedPnl = 0m;
        position.UpdatedUtc = atUtc;

        // One SaveChanges: the signal and the close land together or not at all.
        await _dbContext.SimulationSignals.AddAsync(signal, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await PublishAsync(simulationRunId, DeskEventKinds.Position, position.Symbol,
            $"Settled at expiry at {Price(settlementPrice)}");
        return true;
    }

    // ---------------------------------------------------------------------
    // CARRY FORWARD
    // ---------------------------------------------------------------------

    /// <remarks>
    /// UpdatedUtc is left alone on purpose: the position views read it as the
    /// age of the stored mark, and a tick changed this morning must not make
    /// yesterday's price look fresh.
    /// </remarks>
    public async Task<CarryForwardUpdate> SetCarryForwardAsync(
        long simulationRunId,
        long positionId,
        bool carryForward,
        string metadataJson,
        DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        using var gate = await SimulationRunLocks.AcquireAsync(simulationRunId, cancellationToken);

        // Read under the gate, after any stop has marked the run: a tick that
        // arrives once the close has begun to stop the run is refused rather
        // than recorded as if it could still make a difference.
        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);
        if (run is null) return CarryForwardUpdate.PositionNotOpen;
        if (IsClosedStatus(run.Status)) return CarryForwardUpdate.RunNotRunning;

        var position = await _dbContext.PaperPositions
            .FirstOrDefaultAsync(x => x.Id == positionId && x.SimulationRunId == simulationRunId, cancellationToken);
        if (position is null || position.Status != "Open" || position.Quantity <= 0)
            return CarryForwardUpdate.PositionNotOpen;

        if (position.CarryForward == carryForward) return CarryForwardUpdate.Unchanged;

        position.CarryForward = carryForward;
        position.CarryForwardChangedUtc = atUtc;

        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = simulationRunId,
            StrategyName = position.StrategyName,
            SignalType = CarryForwardSignalType,
            TimestampUtc = atUtc,
            GroupId = position.GroupId,
            MetadataJson = string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson,
            CreatedUtc = UtcNow
        }, cancellationToken);

        // The tick and its audit row land together or not at all.
        await _dbContext.SaveChangesAsync(cancellationToken);

        Publish(run, DeskEventKinds.Carry, position.Symbol,
            carryForward ? "Carry forward ticked: held past the close" : "Carry forward unticked: intraday");
        return CarryForwardUpdate.Changed;
    }

    public async Task<int> CloseIntradayPositionsAsync(
        long simulationRunId,
        IEnumerable<long> positionIds,
        string reason,
        string by,
        CancellationToken cancellationToken = default)
    {
        var ids = positionIds.Distinct().ToList();
        if (ids.Count == 0) return 0;

        var run = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == simulationRunId, cancellationToken);

        if (run is null)
            throw new InvalidOperationException($"Simulation run {simulationRunId} was not found.");

        using var gate = await SimulationRunLocks.AcquireAsync(simulationRunId, cancellationToken);

        // The tick is read here, under the gate, not from the sweep's snapshot:
        // an owner who ticks "carry" at 15:30:05 while the sweep is on its way
        // keeps the position.
        var intraday = await _dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.SimulationRunId == simulationRunId && x.Status == "Open" && !x.CarryForward && ids.Contains(x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var metadata = System.Text.Json.JsonSerializer.Serialize(new { reason, by, system = true, intraday = true });
        return await CloseOpenPositionsAsync(run, intraday, metadata, reason, cancellationToken);
    }

    /// <remarks>
    /// <para>
    /// Why a move and not a close and a re-open: a close books an exit fill in
    /// the run and a re-open an entry fill in the book, and RunCharges charges
    /// every fill — a leg carried overnight would pay a round trip that never
    /// happened, and the book's entry would be the close's price rather than
    /// the one the leg was actually opened at.
    /// </para>
    /// <para>
    /// So the run keeps the ENTRY fill (and its charges), and its P&amp;L
    /// simply stops counting the leg: the carried row has no unrealized P&amp;L
    /// and realizes nothing. The book's row starts at the same entry, so the
    /// whole P&amp;L of the trade, entry to exit, lands in the book, and so do
    /// the exit fill's charges when the owner closes it. Across the two, the
    /// trade is counted once and charged once.
    /// </para>
    /// <para>
    /// Both gates are held, the run's first. Nothing else ever holds two, so the
    /// order cannot deadlock.
    /// </para>
    /// </remarks>
    public async Task<PaperPositionResponse?> CarryPositionAsync(
        long fromRunId,
        long positionId,
        long toRunId,
        string toGroupId,
        string fromMetadataJson,
        string toMetadataJson,
        DateTime atUtc,
        CancellationToken cancellationToken = default)
    {
        if (fromRunId == toRunId)
            throw new InvalidOperationException($"A position cannot be carried from run {fromRunId} into itself.");
        if (string.IsNullOrWhiteSpace(toGroupId))
            throw new InvalidOperationException("A carried position needs a group in the book.");

        using var fromGate = await SimulationRunLocks.AcquireAsync(fromRunId, cancellationToken);
        using var toGate = await SimulationRunLocks.AcquireAsync(toRunId, cancellationToken);

        var book = await _dbContext.SimulationRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == toRunId, cancellationToken);
        if (book is null)
            throw new InvalidOperationException($"Simulation run {toRunId} was not found.");
        if (IsClosedStatus(book.Status))
            throw new InvalidOperationException($"Simulation run {toRunId} is {book.Status.ToLowerInvariant()}; nothing can be carried into it.");

        var source = await _dbContext.PaperPositions
            .FirstOrDefaultAsync(x => x.Id == positionId && x.SimulationRunId == fromRunId, cancellationToken);

        // Closed by the guard, unticked a moment ago, or already carried by an
        // earlier attempt: nothing to move, and nothing written.
        if (source is null || source.Status != "Open" || source.Quantity <= 0 || !source.CarryForward)
            return null;

        var carried = new PaperPosition
        {
            SimulationRunId = toRunId,
            StrategyName = book.StrategyName,
            GroupId = toGroupId,
            Symbol = source.Symbol,
            Direction = source.Direction,
            Quantity = source.Quantity,
            AveragePrice = source.AveragePrice,
            LastMarkPrice = source.LastMarkPrice,
            RealizedPnl = 0m,
            // Same contract, quantity, entry and mark: the same unrealized.
            UnrealizedPnl = source.UnrealizedPnl,
            Status = "Open",
            // When the trade was actually entered, not when it changed books.
            OpenedUtc = source.OpenedUtc,
            // The mark's age travels with the mark.
            UpdatedUtc = source.UpdatedUtc,
            StopLossPrice = source.StopLossPrice,
            TargetPrice = source.TargetPrice,
            CarryForward = true,
            CarryForwardChangedUtc = atUtc,
            CarriedFromPositionId = source.Id
        };

        source.Status = CarriedStatus;
        source.UnrealizedPnl = 0m;
        source.ClosedUtc = atUtc;

        await _dbContext.PaperPositions.AddAsync(carried, cancellationToken);
        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = fromRunId,
            StrategyName = source.StrategyName,
            SignalType = CarryOutSignalType,
            TimestampUtc = atUtc,
            GroupId = source.GroupId,
            MetadataJson = string.IsNullOrWhiteSpace(fromMetadataJson) ? "{}" : fromMetadataJson,
            CreatedUtc = UtcNow
        }, cancellationToken);
        await _dbContext.SimulationSignals.AddAsync(new SimulationSignal
        {
            SimulationRunId = toRunId,
            StrategyName = book.StrategyName,
            SignalType = CarryInSignalType,
            TimestampUtc = atUtc,
            GroupId = toGroupId,
            MetadataJson = string.IsNullOrWhiteSpace(toMetadataJson) ? "{}" : toMetadataJson,
            CreatedUtc = UtcNow
        }, cancellationToken);

        // One save: the leg is in exactly one book at every moment.
        await _dbContext.SaveChangesAsync(cancellationToken);

        await PublishAsync(fromRunId, DeskEventKinds.Carry, source.Symbol, $"Carried forward to the manual book (run #{toRunId})");
        Publish(book, DeskEventKinds.Carry, source.Symbol, $"Carried forward from run #{fromRunId}");
        return MapPosition(carried);
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

        using var gate = await SimulationRunLocks.AcquireAsync(simulationRunId, cancellationToken);

        // A replay's marks are bar closes, all as of the bar the runner names.
        var atUtc = request.AtUtc.ToUniversalTime();
        var prices = new Dictionary<string, QuoteSnapshot>(StringComparer.Ordinal);
        foreach (var mark in request.Marks)
        {
            if (string.IsNullOrWhiteSpace(mark.Symbol)) continue;
            prices[mark.Symbol.Trim()] = new QuoteSnapshot(mark.Price, null, null, atUtc);
        }

        var open = await _dbContext.PaperPositions
            .Where(x => x.SimulationRunId == simulationRunId && x.Status == "Open")
            .ToListAsync(cancellationToken);

        var touched = open.Where(x => prices.ContainsKey(x.Symbol)).ToList();
        if (touched.Count == 0) return 0;

        var lotSizes = await ResolveLotSizesForRunAsync(simulationRunId, touched.Select(x => x.Symbol), cancellationToken);
        MarkOpenPositions(touched, prices, lotSizes);

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

        var now = UtcNow;

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
    /// Fills one leg as a paper order at <paramref name="fill"/> and applies it
    /// to the run's positions. With <paramref name="reduceOnly"/> the leg may
    /// only shrink or close an existing open position in its group: quantity is
    /// clamped to what is open and, when nothing is open (already closed by a
    /// concurrent stop), the leg is skipped and <c>false</c> is returned.
    /// <paramref name="atUtc"/> is the market's clock: the bar time for replays,
    /// the replayed session's time for a recap (see <see cref="RecapClock"/>);
    /// null means the wall clock. A leg that filled is added to
    /// <paramref name="booked"/>, for the desk events sent once it is committed.
    /// </summary>
    private async Task<bool> CreateOrderAndApplyPositionAsync(
        SimulationSignal signal,
        SimulationSignalLegRequest leg,
        PaperFill fill,
        CancellationToken cancellationToken,
        bool bypassRiskCheck = false,
        bool reduceOnly = false,
        DateTime? atUtc = null,
        List<BookedLeg>? booked = null)
    {
        string normalizedSide = ValidateLeg(leg);

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
                // reduceOnly IS the closing signal: the leg has already been
                // matched against an open position above and clamped to it.
                isClosing: reduceOnly,
                cancellationToken);
        }

        // Symbol, side and quantity all fail loudly above; price used to be the
        // one that did not, becoming a zero that reached FillPrice, AveragePrice,
        // the notional and the run's P&L without ever looking wrong. Callers
        // price every leg before filling anything (see PriceLegsAsync), so this
        // should be unreachable — which is the point of asserting it.
        if (fill.Price <= 0m)
            throw new InvalidOperationException(
                $"Paper leg price for {leg.Symbol} must be greater than zero; refusing to fill at {fill.Price}.");

        decimal fillPrice = fill.Price;
        DateTime clock = atUtc ?? UtcNow;

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
            // What the signal asked for, when it named a price; the fill can
            // differ by the spread, or by the move since the runner looked.
            RequestedPrice = leg.Price is > 0m ? leg.Price : fillPrice,
            FillPrice = fillPrice,
            MetadataJson = fill.ToMetadataJson(),
            CreatedUtc = clock,
            FilledUtc = clock
        };

        await _dbContext.PaperOrders.AddAsync(order, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var (applied, closedPosition) = await ApplyPositionAsync(signal, order, cancellationToken, reduceOnly, clock);
        if (!applied)
        {
            // The position vanished between the lookup and the apply (closed by a
            // concurrent stopper). A filled order that moved nothing would only
            // confuse the order history, so drop it.
            _dbContext.PaperOrders.Remove(order);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            booked?.Add(new BookedLeg(order.Symbol, order.Side, order.Quantity, fillPrice, closedPosition));
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
    /// Not applied only in reduce-only mode when there is no open position to
    /// reduce; otherwise a fresh position is opened. Closed when the order took
    /// an open position to zero. Every timestamp written here is
    /// <paramref name="clock"/> (bar time for replays, the replayed session's
    /// time for a recap).
    /// </summary>
    private async Task<(bool Applied, bool Closed)> ApplyPositionAsync(
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
                return (false, false);
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
            return (true, false);
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
                return (false, false);
            }

            int newQty = existing.Quantity + order.Quantity;
            decimal oldNotional = existing.AveragePrice * existing.Quantity;
            decimal newNotional = (order.FillPrice ?? 0m) * order.Quantity;

            existing.AveragePrice = (oldNotional + newNotional) / newQty;
            existing.Quantity = newQty;
            existing.LastMarkPrice = order.FillPrice;
            existing.UpdatedUtc = clock;

            await _dbContext.SaveChangesAsync(cancellationToken);
            return (true, false);
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

        return (true, existing.Quantity == 0);
    }

    // ---------------------------------------------------------------------
    // DESK EVENTS
    // ---------------------------------------------------------------------

    /// <summary>A leg that filled, as the desk event reports it.</summary>
    private sealed record BookedLeg(string Symbol, string Side, int Quantity, decimal Price, bool ClosedPosition);

    /// <summary>
    /// A committed signal's events: one <c>order</c>, a <c>fill</c> per leg
    /// that filled, and a <c>position</c> per position it closed. Nothing for
    /// a signal that filled nothing (every leg skipped as already closed).
    /// </summary>
    private void PublishBooked(SimulationRun run, SimulationSignal signal, IReadOnlyList<BookedLeg> booked, string? reason)
    {
        if (_deskEvents is null || booked.Count == 0 || IsReplay(run.Mode)) return;

        var symbols = booked.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();
        string because = string.IsNullOrWhiteSpace(reason) ? string.Empty : $" — {reason}";

        Publish(run, DeskEventKinds.Order, symbols.Count == 1 ? symbols[0] : null,
            $"{signal.SignalType}: {Plural(booked.Count, "leg")} filled{because}");

        foreach (var leg in booked)
        {
            Publish(run, DeskEventKinds.Fill, leg.Symbol, $"{leg.Side} {leg.Quantity} at {Price(leg.Price)}");
            if (leg.ClosedPosition)
            {
                Publish(run, DeskEventKinds.Position, leg.Symbol, $"Position closed{because}");
            }
        }
    }

    /// <summary>One event about <paramref name="run"/>, told to its owner and the admins.</summary>
    private void Publish(SimulationRun run, string kind, string? symbol, string? detail)
    {
        if (_deskEvents is null || IsReplay(run.Mode)) return;
        _deskEvents.TryPublish(new DeskEvent(kind, run.Id, run.UserId, symbol, UtcNow, detail));
    }

    /// <summary>
    /// As <see cref="Publish(SimulationRun, string, string?, string?)"/> for a
    /// run that is not at hand: its owner is looked up, so the owner is still
    /// told. Never throws — the write it reports has committed.
    /// </summary>
    private async Task PublishAsync(long runId, string kind, string? symbol, string? detail)
    {
        if (_deskEvents is null) return;

        try
        {
            var run = await _dbContext.SimulationRuns
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == runId, CancellationToken.None);
            if (run is not null) Publish(run, kind, symbol, detail);
        }
        catch (Exception)
        {
            // The event is a hint to fetch again; the write it reports is in.
        }
    }

    private static string Price(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

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

    /// <summary>
    /// The latest quote of each symbol that has one, with its bid, ask and the
    /// time it was written: a fill needs the book, and everything that reads a
    /// quote needs to know how old it is.
    /// </summary>
    private async Task<Dictionary<string, QuoteSnapshot>> LoadLiveQuotesAsync(List<string> symbols, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, QuoteSnapshot>(StringComparer.Ordinal);
        if (symbols.Count == 0) return result;

        var rows = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol))
            .Select(x => new { x.Symbol, x.LastTradedPrice, x.BidPrice, x.AskPrice, x.UpdatedUtc })
            .ToListAsync(cancellationToken);

        foreach (var row in rows)
        {
            var updatedUtc = row.UpdatedUtc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(row.UpdatedUtc, DateTimeKind.Utc)
                : row.UpdatedUtc.ToUniversalTime();
            result[row.Symbol] = new QuoteSnapshot(row.LastTradedPrice, row.BidPrice, row.AskPrice, updatedUtc);
        }
        return result;
    }

    /// <summary>
    /// Marks the open positions that have a price at it; others are left
    /// untouched. A position's UpdatedUtc becomes the time of the price it is
    /// marked at, not the time it was marked: the risk guard and the position
    /// views read it as the age of the mark, and a frozen quote re-applied every
    /// few seconds must not look fresh.
    /// </summary>
    private static void MarkOpenPositions(
        IEnumerable<PaperPosition> positions,
        IReadOnlyDictionary<string, QuoteSnapshot> quotes,
        IReadOnlyDictionary<string, LotSizeInfo> lotSizes)
    {
        foreach (var pos in positions)
        {
            if (pos.Status != "Open") continue;
            if (!quotes.TryGetValue(pos.Symbol, out var quote) || quote.LastTradedPrice is not { } price) continue;

            int lotSize = LotSizeOf(lotSizes, pos.Symbol);
            pos.LastMarkPrice = price;
            pos.UnrealizedPnl = CalculateUnrealizedPnl(pos.Direction, pos.AveragePrice, price, pos.Quantity, lotSize);
            pos.UpdatedUtc = quote.UpdatedUtc;
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
        => UsedCapitalOf(pos.Direction, pos.Symbol, pos.AveragePrice, pos.Quantity, LotSizeOf(lotSizes, pos.Symbol));

    /// <summary>
    /// Capital one open position ties up — the same formula the portfolio
    /// summary's UsedCapital sums: premium paid (entry × lots × lot size) for a
    /// LONG leg, a per-underlying margin heuristic per lot for a SHORT leg.
    /// Shared with the position views so "capital used" reads the same everywhere.
    /// </summary>
    public static decimal UsedCapitalOf(string direction, string symbol, decimal averagePrice, int quantity, int lotSize)
        => string.Equals(direction, "SHORT", StringComparison.OrdinalIgnoreCase)
            ? GetMarginHeuristic(symbol) * quantity
            : Math.Abs(averagePrice * quantity * Math.Max(1, lotSize));

    // The arithmetic itself lives in PaperPnl, where it can be tested without a
    // database and where the run history can reach it instead of writing its own.
    private static decimal CalculateRealizedPnl(string direction, decimal avgPrice, decimal exitPrice, int qty, int lotSize)
        => PaperPnl.Realized(direction, avgPrice, exitPrice, qty, lotSize);

    private static decimal CalculateUnrealizedPnl(string direction, decimal avgPrice, decimal markPrice, int qty, int lotSize)
        => PaperPnl.Unrealized(direction, avgPrice, markPrice, qty, lotSize);

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
            ClientSignalId = row.ClientSignalId,
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
            MetadataJson = row.MetadataJson,
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
            StopLossPrice = row.StopLossPrice,
            TargetPrice = row.TargetPrice,
            CarryForward = row.CarryForward,
            CarriedFromPositionId = row.CarriedFromPositionId,
            UpdatedUtc = row.UpdatedUtc
        };
    }
}
