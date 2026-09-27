// src/AlgoTrading.Api/Services/PositionCarryForward.cs
using System.Globalization;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The carry-forward tick on a position, and what the market close does with
/// a strategy run's ticked legs.
/// </summary>
/// <remarks>
/// <para>
/// The owner's request, 27 Sep: "Put a carry-forward system in strategies and
/// in manual orders: if I want to carry forward, there should be a tick there
/// and ticking it is enough. In strategies, even a single leg — I should be
/// able to do it." Built as a broker's intraday (MIS) vs carry-forward (NRML)
/// product, one flag per position.
/// </para>
/// <para>
/// In the manual book an unticked position is intraday and is squared off at
/// its exchange's close (<see cref="ManualIntradaySquareOff"/>). In a strategy
/// run, the stop the market close makes moves the ticked legs into the run
/// owner's manual book instead of squaring them off
/// (<see cref="CarryTickedLegsAsync"/>); the rest of the run is flattened as
/// before. Every other stop — the Stop button, a risk rule, a runner that died,
/// an API restart that finds the runner gone — flattens everything, ticked or
/// not: those are protective stops, and a protective stop that leaves legs
/// behind is not one.
/// </para>
/// </remarks>
public sealed class PositionCarryForward
{
    /// <summary>Who the close's carry is attributed to: the same name as the stop it is part of.</summary>
    public const string CloseBy = "market-hours";

    /// <summary>What a request to change the tick did.</summary>
    public enum Outcome
    {
        Changed,
        Unchanged,
        /// <summary>Not an open position of this run.</summary>
        PositionNotOpen,
        /// <summary>The run is being stopped or has ended.</summary>
        RunNotRunning,
        /// <summary>A recap run: its positions are priced from a replayed session.</summary>
        Recap
    }

    public sealed record ChangeResult(Outcome Outcome, string Message);

    private readonly TradingDbContext _dbContext;
    private readonly IPaperTradingService _paperTrading;
    private readonly ILogger<PositionCarryForward> _logger;

    public PositionCarryForward(
        TradingDbContext dbContext,
        IPaperTradingService paperTrading,
        ILogger<PositionCarryForward> logger)
    {
        _dbContext = dbContext;
        _paperTrading = paperTrading;
        _logger = logger;
    }

    // ------------------------------------------------------------ the tick --

    /// <summary>
    /// Whether the tick on a run's positions can mean anything now: the run is
    /// live (<paramref name="active"/>: its runner is in the registry, or it is
    /// the open manual book), its row still says Running, and it is not a recap.
    /// </summary>
    public static bool IsChangeable(SimulationRun run, bool active) =>
        active
        && string.Equals(run.Status, StrategyRunControl.RunStatusRunning, StringComparison.OrdinalIgnoreCase)
        && !RecapClock.IsRecap(run.ParametersJson);

    /// <summary>
    /// Ticks or unticks one open position of <paramref name="run"/>, recording
    /// who did it and when as a CARRY_FORWARD row on the run's activity. Who
    /// may is the caller's decision (the owner, or an admin — the same rule as
    /// squaring a position off); this decides whether it can mean anything.
    /// </summary>
    public async Task<ChangeResult> SetAsync(
        SimulationRun run,
        long positionId,
        bool carryForward,
        string by,
        CancellationToken cancellationToken)
    {
        // A recap replays an earlier session in the evening: its fills are that
        // day's prices. Carried into the book, a replayed leg would sit beside
        // real positions and be marked against today's quotes.
        if (RecapClock.IsRecap(run.ParametersJson))
            return new ChangeResult(Outcome.Recap,
                $"Run {run.Id} is a recap of an earlier session; its positions cannot be carried forward.");

        if (!string.Equals(run.Status, StrategyRunControl.RunStatusRunning, StringComparison.OrdinalIgnoreCase))
            return new ChangeResult(Outcome.RunNotRunning,
                $"Run {run.Id} is {run.Status.ToLowerInvariant()}; nothing will carry its positions now.");

        var position = await _dbContext.PaperPositions.AsNoTracking()
            .Where(x => x.Id == positionId && x.SimulationRunId == run.Id)
            .Select(x => new { x.Symbol, x.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (position is null || position.Status != "Open")
            return new ChangeResult(Outcome.PositionNotOpen,
                $"Position {positionId} is not an open position of run {run.Id} — the console may be showing a stale view.");

        bool book = IsManualBook(run);
        string label = (await ContractLabelsAsync(new[] { position.Symbol }, cancellationToken))[position.Symbol];
        string reason = DescribeChange(label, position.Symbol, carryForward, book, by);
        string metadata = JsonSerializer.Serialize(new { reason, by, positionId, symbol = position.Symbol, carryForward });

        var update = await _paperTrading.SetCarryForwardAsync(
            run.Id, positionId, carryForward, metadata, DateTime.UtcNow, cancellationToken);

        return update switch
        {
            CarryForwardUpdate.Changed => new ChangeResult(Outcome.Changed, reason),
            CarryForwardUpdate.Unchanged => new ChangeResult(Outcome.Unchanged,
                carryForward ? $"{label} is already carried forward." : $"{label} is already intraday."),
            CarryForwardUpdate.RunNotRunning => new ChangeResult(Outcome.RunNotRunning,
                $"Run {run.Id} is being stopped; nothing will carry its positions now."),
            _ => new ChangeResult(Outcome.PositionNotOpen,
                $"Position {positionId} is no longer open.")
        };
    }

    /// <summary>
    /// The activity line for a change of the tick. Says what will happen at the
    /// close, because that is the only thing the tick changes.
    /// </summary>
    public static string DescribeChange(string label, string symbol, bool carryForward, bool manualBook, string by)
    {
        string what = carryForward ? "ticked" : "unticked";
        string then = (carryForward, manualBook) switch
        {
            (true, true) => "held overnight",
            (false, true) => $"intraday — squared off at the {CloseName(symbol)}",
            (true, false) => "moves to the manual book at the close instead of being squared off",
            (false, false) => "squared off with the run at the close"
        };
        return $"Carry forward {what} by {by}: {label} — {then}";
    }

    /// <summary>"close (15:30 IST)" for NSE and BSE, "MCX close" for commodities.</summary>
    public static string CloseName(string symbol) =>
        MarketCloseRules.ExchangeOf(null, symbol) == MarketCloseRules.Mcx ? "MCX close" : "close (15:30 IST)";

    // ------------------------------------------------------------ the close --

    /// <summary>
    /// Moves the ticked open legs of a run that its market's close is stopping
    /// into the run owner's manual book — creating the book when the owner has
    /// none — at their own entry prices. Returns how many moved. Called by the
    /// stop pipeline after the runner has been stopped and before the flatten,
    /// so whatever is not moved here (unticked, or a move that failed) is
    /// squared off as it always was.
    /// </summary>
    public async Task<int> CarryTickedLegsAsync(long runId, DateTime closedAtUtc, CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is null || IsManualBook(run)) return 0;

        var ticked = await _dbContext.PaperPositions.AsNoTracking()
            .Where(x => x.SimulationRunId == runId && x.Status == "Open" && x.CarryForward && x.Quantity > 0)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (ticked.Count == 0) return 0;

        if (RecapClock.IsRecap(run.ParametersJson))
        {
            // The endpoint refuses the tick on a recap; a row ticked some other
            // way is still not carried into the real book.
            _logger.LogWarning("Run {RunId} is a recap; its {Count} ticked leg(s) are squared off, not carried.", runId, ticked.Count);
            return 0;
        }

        var book = await ManualBook.FindOrCreateAsync(_dbContext, run.UserId, _logger, cancellationToken);
        var labels = await ContractLabelsAsync(ticked.Select(x => x.Symbol), cancellationToken);
        string at = IstTime.ToIst(closedAtUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;

        int moved = 0;
        foreach (var leg in ticked)
        {
            string what = $"{labels[leg.Symbol]} — {(leg.Direction == "LONG" ? "BUY" : "SELL")} "
                          + $"{leg.Quantity} lot{(leg.Quantity == 1 ? "" : "s")} at {leg.AveragePrice.ToString("0.00", CultureInfo.InvariantCulture)}";
            string fromMetadata = JsonSerializer.Serialize(new
            {
                reason = $"Carried forward to the manual book at the close ({at} IST): {what}",
                by = CloseBy,
                system = true,
                carryForward = true,
                positionId = leg.Id,
                toRunId = book.Id
            });
            string toMetadata = JsonSerializer.Serialize(new
            {
                reason = $"Carried forward from run #{run.Id} ({run.StrategyName}) at the close ({at} IST): {what}",
                by = CloseBy,
                system = true,
                carryForward = true,
                fromRunId = run.Id,
                fromPositionId = leg.Id
            });

            try
            {
                var carried = await _paperTrading.CarryPositionAsync(
                    run.Id, leg.Id, book.Id, BookGroupId(run.Id, leg.GroupId), fromMetadata, toMetadata, now, cancellationToken);
                if (carried is null) continue;

                moved++;
                _logger.LogInformation(
                    "Run {RunId} ({Strategy}): carried {Symbol} ({Side} {Lots} lot(s) at {Entry}) to manual book {BookId} at the close.",
                    run.Id, run.StrategyName, leg.Symbol, leg.Direction, leg.Quantity, leg.AveragePrice, book.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Left open, the flatten that follows squares it off: a leg
                // that could not be moved ends the day as it did before the
                // tick existed, never stranded on a stopped run.
                _logger.LogError(ex, "Run {RunId}: could not carry position {PositionId} ({Symbol}) forward; it is squared off with the run.",
                    run.Id, leg.Id, leg.Symbol);
            }
        }

        return moved;
    }

    /// <summary>
    /// The group a carried leg lives in inside the book: the run's own group,
    /// prefixed with the run, so the legs of one straddle stay one group there
    /// and two runs' "G1" never meet.
    /// </summary>
    public static string BookGroupId(long runId, string? groupId)
    {
        string id = $"CARRY-{runId}-{groupId}";
        return id.Length <= 100 ? id : id[..100];
    }

    private static bool IsManualBook(SimulationRun run) =>
        run.StrategyName == ManualOrdersController.BookStrategyName;

    /// <summary>"NIFTY 24500 CE · 29 Sep" per symbol — the label the position tables show.</summary>
    private async Task<Dictionary<string, string>> ContractLabelsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        var wanted = symbols.Distinct(StringComparer.Ordinal).ToList();
        var instruments = await _dbContext.Instruments.AsNoTracking()
            .Where(x => wanted.Contains(x.Symbol))
            .Select(x => new PositionViewBuilder.InstrumentLite(x.Symbol, x.Underlying, x.StrikePrice, x.OptionType, x.ExpiryDate, x.Exchange, x.InstrumentType))
            .ToListAsync(cancellationToken);
        var bySymbol = instruments
            .GroupBy(x => x.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return wanted.ToDictionary(
            s => s,
            s => PositionViewBuilder.BuildContract(s, bySymbol.GetValueOrDefault(s)).Label,
            StringComparer.Ordinal);
    }
}
