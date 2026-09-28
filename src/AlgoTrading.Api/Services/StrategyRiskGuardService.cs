// src/AlgoTrading.Api/Services/StrategyRiskGuardService.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Enforces each running strategy's risk rules from the API side, so the
/// guard works even when the Python runner is wedged. Every sweep, per run:
/// leg rules (close that leg only) → group rules (close every open leg of
/// that group; the run keeps going) → overall rules (flatten everything and
/// end the run). Within each level the order is fixed stop-loss → trailing
/// stop → target. A position is never closed twice in one sweep; every trip is
/// logged; a failure on one run never skips the next.
///
/// Trailing peaks live on the run's registry entry (<see cref="RiskTrailState"/>)
/// and are never persisted, so an adopted run and a run whose rules were just
/// changed re-arm their trails from the P&amp;L of the next sweep.
///
/// A position is judged by its own, leg and group rules only on a fresh mark
/// (<see cref="StrategyRunnerOptions.RiskGuardMaxMarkAgeSeconds"/>): a mark
/// taken from a quote the feed stopped updating is a price the market may have
/// left, and a stop or target tripped on it closes at a level nobody could
/// trade. The overall rules still run on every sweep — they are the run's last
/// line, and what they trip is a square-off.
///
/// Whatever a rule closes is told to the console as a <c>risk</c> desk event,
/// with the rule's own reason, once the close has been booked.
/// </summary>
public sealed class StrategyRiskGuardService : BackgroundService
{
    public const string By = "risk-guard";

    private readonly StrategyProcessRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<StrategyRunnerOptions> _options;
    private readonly ILogger<StrategyRiskGuardService> _logger;

    /// <summary>
    /// Runs whose rules are paused on a stale mark, and since when: one alert
    /// per incident, not one per sweep. Only the sweep loop touches it.
    /// </summary>
    private readonly Dictionary<long, DateTime> _staleSince = new();

    public StrategyRiskGuardService(
        StrategyProcessRegistry registry,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<StrategyRunnerOptions> options,
        ILogger<StrategyRiskGuardService> logger)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StrategyRiskGuardService is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Risk guard sweep failed.");
            }

            var seconds = Math.Max(1, _options.CurrentValue.RiskGuardIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("StrategyRiskGuardService is stopping.");
    }

    /// <summary>One pass of the loop, for the tests.</summary>
    internal Task SweepOnceAsync(CancellationToken cancellationToken) => CheckAllAsync(cancellationToken);

    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        var guarded = _registry.List()
            .Where(x => x.Risk.HasAnyRule && !x.StopRequested)
            .ToList();

        // A run that has stopped takes its incident with it.
        var swept = guarded.Select(x => x.RunId).ToHashSet();
        foreach (var runId in _staleSince.Keys.Where(id => !swept.Contains(id)).ToList())
        {
            _staleSince.Remove(runId);
        }

        if (guarded.Count == 0) return;

        foreach (var entry in guarded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var paperTrading = scope.ServiceProvider.GetRequiredService<IPaperTradingService>();
                var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();
                var charges = scope.ServiceProvider.GetRequiredService<RunCharges>();

                await SweepAsync(entry, paperTrading, control, charges, scope.ServiceProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Risk guard check failed for strategy {StrategyId} run {RunId}.", entry.StrategyId, entry.RunId);
            }
        }

        await CheckOwnLevelsAsync(guarded.Select(x => x.RunId).ToHashSet(), cancellationToken);
    }

    /// <summary>
    /// Positions carrying their OWN stop / target, in runs the loop above did
    /// not already cover.
    /// </summary>
    /// <remarks>
    /// Two runs fall through that loop, and both can hold a position with its
    /// own levels:
    ///
    ///   - the manual book, which has no runner and therefore no registry entry
    ///     at all. Its per-order stops would never have fired: the order was
    ///     placed, the level was stored, the price went through it, and nothing
    ///     looked.
    ///   - a strategy run started with no run-level risk rules, which the loop
    ///     skips on `Risk.HasAnyRule`.
    ///
    /// One indexed query finds them: open positions that actually carry a level.
    /// Nothing here touches group, overall or trailing rules - those are
    /// properties of a run, and a run without them has not asked for them.
    /// </remarks>
    private async Task CheckOwnLevelsAsync(HashSet<long> alreadySwept, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var runIds = await dbContext.PaperPositions
            .AsNoTracking()
            .Where(x => x.Status == "Open" && (x.StopLossPrice != null || x.TargetPrice != null))
            .Select(x => x.SimulationRunId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var pending = runIds.Where(id => !alreadySwept.Contains(id)).ToList();
        if (pending.Count == 0) return;

        var paperTrading = scope.ServiceProvider.GetRequiredService<IPaperTradingService>();
        var deskEvents = scope.ServiceProvider.GetService<IDeskEventPublisher>();
        var maxAge = MaxMarkAge();

        foreach (var runId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var positions = await paperTrading.GetPaperPositionsAsync(runId, cancellationToken);
                var now = DateTime.UtcNow;

                foreach (var pos in positions.Where(IsOpen))
                {
                    if (pos.StopLossPrice is null && pos.TargetPrice is null) continue;

                    // Judged on a fresh mark only, as in the runs above. A book
                    // has no runner and nobody to page, and a carried leg's quote
                    // is hours old every night, so this is worth a debug line.
                    if (IsStale(pos, now, maxAge))
                    {
                        _logger.LogDebug("Own levels of position {PositionId} (run {RunId}) not checked: its mark is {Age:0}s old.",
                            pos.Id, runId, (now - pos.UpdatedUtc).TotalSeconds);
                        continue;
                    }

                    var reason = EvaluateOwnLevels(pos);
                    if (reason is null) continue;

                    _logger.LogWarning("Risk guard closing position {PositionId} of run {RunId}: {Reason}",
                        pos.Id, runId, reason);

                    int closed = await paperTrading.ClosePositionsAsync(
                        runId, new[] { pos.Id }, reason, "risk-guard", CancellationToken.None);

                    if (closed > 0 && deskEvents is not null)
                    {
                        PublishRisk(deskEvents, runId, await OwnerOfAsync(dbContext, runId), pos.Symbol, reason);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Own-level risk check failed for run {RunId}.", runId);
            }
        }
    }

    /// <summary>One run, one sweep: leg → group → overall.</summary>
    private async Task SweepAsync(
        RunningStrategy entry,
        IPaperTradingService paperTrading,
        StrategyRunControl control,
        RunCharges charges,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // The registry entry may have been replaced by a risk update since the
        // list was taken; always enforce the newest rules.
        var current = _registry.Get(entry.RunId) ?? entry;
        if (current.StopRequested) return;

        var rules = current.Risk;
        long runId = current.RunId;
        var trail = current.Trail;
        var deskEvents = services.GetService<IDeskEventPublisher>();

        // a. Marks open positions to market from the latest live quotes.
        var positions = await paperTrading.GetPaperPositionsAsync(runId, cancellationToken);
        var open = positions.Where(IsOpen).ToList();

        // Peaks of legs that have since closed (and of groups with no open leg
        // left) are dead weight, and a recycled position id must never inherit one.
        trail.Prune(
            open.Select(x => x.Id).ToHashSet(),
            open.Select(x => x.GroupId ?? string.Empty).ToHashSet(StringComparer.Ordinal));

        // a2. Only a fresh mark is judged. GetPaperPositionsAsync stamps each
        //     mark with the time of the quote it came from, so a quote the feed
        //     stopped updating shows its real age here however often it is
        //     re-applied. Its leg is left alone, and so is any group it is in.
        var now = DateTime.UtcNow;
        var maxAge = MaxMarkAge();
        var stale = open.Where(x => IsStale(x, now, maxAge)).ToList();
        var staleIds = stale.Select(x => x.Id).ToHashSet();
        await TrackStaleMarksAsync(current, stale, now, maxAge, services, cancellationToken);

        var closedThisSweep = new HashSet<long>();

        // b0. A position's OWN stop / target, set when the order was placed.
        //
        // These come first and apply whatever the run's rules say, because they
        // are the more specific instruction: the run's leg rules are one setting
        // shared by every position, which cannot be right for a book held by
        // hand where a share at 703 and an option at 2.90 sit side by side.
        foreach (var pos in open)
        {
            if (pos.StopLossPrice is null && pos.TargetPrice is null) continue;
            if (staleIds.Contains(pos.Id)) continue;

            var reason = EvaluateOwnLevels(pos);
            if (reason is null) continue;

            closedThisSweep.Add(pos.Id);
            await CloseAsync(current, paperTrading, new[] { pos.Id }, reason, pos.Symbol, deskEvents, cancellationToken);
        }

        if (closedThisSweep.Count > 0)
        {
            open = open.Where(x => !closedThisSweep.Contains(x.Id)).ToList();
        }

        // b. Leg rules → one close per tripped leg, each with its own reason.
        if (rules.Leg is { HasAnyRule: true } leg && open.Count > 0)
        {
            foreach (var pos in open)
            {
                if (staleIds.Contains(pos.Id)) continue;

                var reason = EvaluateLeg(pos, leg, trail);
                if (reason is null) continue;

                closedThisSweep.Add(pos.Id);
                await CloseAsync(current, paperTrading, new[] { pos.Id }, reason, pos.Symbol, deskEvents, cancellationToken);
            }
        }

        // c. Group rules over the same positions; legs already closed above are
        //    not closed again, but they still count in the group's P&L (they were
        //    open when this snapshot was taken).
        if (rules.Group is { HasAnyRule: true } group && open.Count > 0)
        {
            foreach (var g in positions.GroupBy(x => x.GroupId, StringComparer.Ordinal))
            {
                var openLegs = g.Where(IsOpen).ToList();
                if (openLegs.Count == 0) continue;

                // The group's P&L carries every open leg's mark; one stale leg
                // makes the whole figure one the market may not agree with.
                if (openLegs.Any(x => staleIds.Contains(x.Id))) continue;

                decimal groupPnl = g.Sum(x => x.RealizedPnl) + openLegs.Sum(x => x.UnrealizedPnl);
                var reason = EvaluateGroup(g.Key, groupPnl, group, trail);
                if (reason is null) continue;

                var ids = openLegs.Select(x => x.Id).Where(id => !closedThisSweep.Contains(id)).ToList();
                if (ids.Count == 0)
                {
                    _logger.LogInformation("Run {RunId}: {Reason} — every leg of the group was already closed by a leg rule this sweep.", runId, reason);
                    continue;
                }

                foreach (var id in ids) closedThisSweep.Add(id);
                var legSymbols = openLegs.Where(x => ids.Contains(x.Id)).Select(x => x.Symbol).Distinct().ToList();
                await CloseAsync(current, paperTrading, ids, reason, legSymbols.Count == 1 ? legSymbols[0] : null,
                    deskEvents, cancellationToken);
            }
        }

        // e. Overall — a fresh summary, so what the leg/group closes realized
        //    counts, and the run ends when the total crosses the line.
        if (rules.Overall is { HasAnyRule: true } overall)
        {
            var summary = await paperTrading.GetPortfolioSummaryAsync(runId, cancellationToken);
            decimal chargesSoFar = await charges.ForRunAsync(runId, cancellationToken);
            decimal totalPnl = OverallNet(summary.RealizedPnl, summary.UnrealizedPnl, chargesSoFar);

            var reason = EvaluateOverall(totalPnl, overall, trail);
            if (reason is null) return;

            _logger.LogWarning("Risk guard tripping strategy {StrategyId} ({Name}) run {RunId} on {Underlying}: {Reason}",
                current.StrategyId, current.Name, runId, current.Underlying, reason);

            var stop = await control.StopAsync(runId, reason, flatten: true, by: By, cancellationToken);
            if (stop.WasRunning)
            {
                PublishRisk(deskEvents, runId, current.UserId, symbol: null, $"Run stopped — {reason}");
            }
        }
    }

    /// <summary>
    /// Leg rule for one open position. Adverse move = BUY: entry − ltp, SELL:
    /// ltp − entry; percent of entry. Checked in the level's fixed → trailing →
    /// target order: SL points, SL percent, trailing points, trailing percent,
    /// target points, target percent — whichever trips first wins. The leg's two
    /// trailing tracks are advanced first, so the peaks stay current whatever
    /// trips. Null when nothing trips.
    /// </summary>
    /// <summary>
    /// The position's own stop / target, compared against its last mark.
    /// </summary>
    /// <remarks>
    /// Direction decides which side trips: a long is stopped BELOW its stop and
    /// takes profit ABOVE its target; a short is the mirror. Getting this the
    /// wrong way round would close every position the instant it was opened.
    /// </remarks>
    internal static string? EvaluateOwnLevels(PaperPositionResponse pos)
    {
        if (pos.LastMarkPrice is not { } mark || mark <= 0) return null;

        // A position's Direction is "LONG"/"SHORT" — the ORDER's side is
        // BUY/SELL. Reading it as BUY made every long look like a short, so the
        // stop tripped the instant the position opened: the very failure the
        // remark above warns about, caught by the first live test.
        bool isLong = string.Equals(pos.Direction, "LONG", StringComparison.OrdinalIgnoreCase);

        if (pos.StopLossPrice is { } stop)
        {
            bool tripped = isLong ? mark <= stop : mark >= stop;
            if (tripped)
                return $"Stop-loss on {pos.Symbol}: {mark:0.##} reached its {stop:0.##} stop";
        }

        if (pos.TargetPrice is { } target)
        {
            bool tripped = isLong ? mark >= target : mark <= target;
            if (tripped)
                return $"Target on {pos.Symbol}: {mark:0.##} reached its {target:0.##} target";
        }

        return null;
    }

    internal static string? EvaluateLeg(PaperPositionResponse pos, LegRiskDto leg, RiskTrailState trail)
    {
        if (pos.AveragePrice <= 0 || pos.LastMarkPrice is not > 0) return null;

        decimal entry = pos.AveragePrice;
        decimal ltp = pos.LastMarkPrice.Value;
        bool isBuy = string.Equals(pos.Direction, "LONG", StringComparison.OrdinalIgnoreCase);

        decimal adverse = isBuy ? entry - ltp : ltp - entry;
        decimal adversePct = adverse / entry * 100m;
        decimal pnlPoints = -adverse;
        decimal pnlPct = -adversePct;
        var label = ContractLabel(pos.Symbol);

        var tracks = leg.HasTrailingRule
            ? trail.ObserveLeg(pos.Id, pnlPoints, pnlPct, leg.TrailTriggerPoints, leg.TrailTriggerPercent)
            : default;

        if (leg.StopLossPoints.HasValue && adverse >= leg.StopLossPoints.Value)
        {
            return $"Leg stop-loss hit: {label} {Signed(pnlPoints)} pts ({Signed(pnlPct)}%) ≤ −{Number(leg.StopLossPoints.Value)} pts";
        }

        if (leg.StopLossPercent.HasValue && adversePct >= leg.StopLossPercent.Value)
        {
            return $"Leg stop-loss hit: {label} {Signed(pnlPoints)} pts ({Signed(pnlPct)}%) ≤ −{Number(leg.StopLossPercent.Value)}%";
        }

        if (leg.TrailStopLossPoints is { } trailPoints
            && tracks.Points.Armed
            && pnlPoints <= tracks.Points.Peak - trailPoints)
        {
            return $"Leg trailing stop hit: {label} {Signed(pnlPoints)} pts fell {Drop(tracks.Points.Peak, pnlPoints)} pts "
                 + $"from peak {Signed(tracks.Points.Peak)} pts (trail {Number(trailPoints)} pts)";
        }

        if (leg.TrailStopLossPercent is { } trailPercent
            && tracks.Percent.Armed
            && pnlPct <= tracks.Percent.Peak - trailPercent)
        {
            return $"Leg trailing stop hit: {label} {Signed(pnlPct)}% fell {Drop(tracks.Percent.Peak, pnlPct)}% "
                 + $"from peak {Signed(tracks.Percent.Peak)}% (trail {Number(trailPercent)}%)";
        }

        if (leg.TargetPoints.HasValue && pnlPoints >= leg.TargetPoints.Value)
        {
            return $"Leg target hit: {label} {Signed(pnlPoints)} pts ({Signed(pnlPct)}%) ≥ {Number(leg.TargetPoints.Value)} pts";
        }

        if (leg.TargetPercent.HasValue && pnlPct >= leg.TargetPercent.Value)
        {
            return $"Leg target hit: {label} {Signed(pnlPoints)} pts ({Signed(pnlPct)}%) ≥ {Number(leg.TargetPercent.Value)}%";
        }

        return null;
    }

    /// <summary>
    /// Group rule on the group's realized + open unrealized P&amp;L, in the
    /// fixed → trailing → target order. Null when nothing trips.
    /// </summary>
    internal static string? EvaluateGroup(string groupId, decimal groupPnl, GroupRiskDto group, RiskTrailState trail)
    {
        var name = string.IsNullOrWhiteSpace(groupId) ? "(no group)" : groupId;

        var track = group.HasTrailingRule
            ? trail.ObserveGroup(groupId ?? string.Empty, groupPnl, group.TrailTrigger)
            : TrailTrack.Idle;

        if (group.StopLoss.HasValue && groupPnl <= -group.StopLoss.Value)
        {
            return $"Group stop-loss hit: {name} P&L {Money(groupPnl)} ≤ −{Money(group.StopLoss.Value)}";
        }

        if (group.TrailStopLoss is { } trailStop && track.Armed && groupPnl <= track.Peak - trailStop)
        {
            return $"Group trailing stop hit: {name} P&L {Rupees(groupPnl)} fell {Rupees(track.Peak - groupPnl)} "
                 + $"from peak {Rupees(track.Peak)} (trail {Rupees(trailStop)})";
        }

        if (group.Target.HasValue && groupPnl >= group.Target.Value)
        {
            return $"Group target hit: {name} P&L {Money(groupPnl)} ≥ {Money(group.Target.Value)}";
        }

        return null;
    }

    /// <summary>
    /// The run's P&amp;L the overall rules are judged on: realized + unrealized,
    /// less the charges of every fill so far (RunCharges, the figure the run
    /// history and the run page call net).
    /// </summary>
    /// <remarks>
    /// Until 28 Sep the rules saw the gross while the backtest judged the same
    /// rules on net: a run 4,000 down with 6,000 of charges was 10,000 down and
    /// not stopped at a 5,000 stop, and a 5,000 target on the gross said "hit"
    /// with the run 1,000 down after charges. The owner chose net on 27 Sep.
    /// Leg rules are in premium points and group rules stay gross: charges are
    /// the run's, not a leg's or a group's.
    /// </remarks>
    public static decimal OverallNet(decimal realized, decimal unrealized, decimal charges) =>
        realized + unrealized - Math.Max(0m, charges);

    /// <summary>
    /// Whether the overall rules trip for a run with this realized, unrealized
    /// and charges: the reason, or null.
    /// </summary>
    public static string? EvaluateOverallNet(decimal realized, decimal unrealized, decimal charges, OverallRiskDto overall, RiskTrailState trail) =>
        EvaluateOverall(OverallNet(realized, unrealized, charges), overall, trail);

    /// <summary>
    /// Overall rule on the run's net P&amp;L (<see cref="OverallNet"/>), in the
    /// fixed → trailing → target order. A trip flattens the run. Null when
    /// nothing trips.
    /// </summary>
    internal static string? EvaluateOverall(decimal totalPnl, OverallRiskDto overall, RiskTrailState trail)
    {
        var track = overall.HasTrailingRule
            ? trail.ObserveOverall(totalPnl, overall.TrailTrigger)
            : TrailTrack.Idle;

        if (overall.StopLoss.HasValue && totalPnl <= -overall.StopLoss.Value)
        {
            return $"Stop loss hit: P&L {Money(totalPnl)} ≤ −{Money(overall.StopLoss.Value)}";
        }

        if (overall.TrailStopLoss is { } trailStop && track.Armed && totalPnl <= track.Peak - trailStop)
        {
            return $"Trailing stop hit: P&L {Rupees(totalPnl)} fell {Rupees(track.Peak - totalPnl)} "
                 + $"from peak {Rupees(track.Peak)} (trail {Rupees(trailStop)})";
        }

        if (overall.Target.HasValue && totalPnl >= overall.Target.Value)
        {
            return $"Target hit: P&L {Money(totalPnl)} ≥ {Money(overall.Target.Value)}";
        }

        return null;
    }

    /// <param name="symbol">The contract, when one leg (or a group of one contract) is closed.</param>
    private async Task CloseAsync(
        RunningStrategy entry,
        IPaperTradingService paperTrading,
        IReadOnlyList<long> positionIds,
        string reason,
        string? symbol,
        IDeskEventPublisher? deskEvents,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning("Risk guard closing {Count} position(s) of strategy {StrategyId} ({Name}) run {RunId} on {Underlying}: {Reason}",
            positionIds.Count, entry.StrategyId, entry.Name, entry.RunId, entry.Underlying, reason);
        _registry.AppendLog(entry.RunId, $"risk guard: {reason}");

        try
        {
            // The close is the risk action itself; a cancelled sweep must not
            // leave it half done.
            int closed = await paperTrading.ClosePositionsAsync(entry.RunId, positionIds, reason, By, CancellationToken.None);
            _registry.AppendLog(entry.RunId, $"risk guard closed {closed} position(s)");
            if (closed > 0)
            {
                PublishRisk(deskEvents, entry.RunId, entry.UserId, symbol, reason);
            }
            if (closed < positionIds.Count)
            {
                _logger.LogInformation("Run {RunId}: {Closed} of {Requested} position(s) closed ({Reason}); the rest were already closed.",
                    entry.RunId, closed, positionIds.Count, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Risk guard close failed for strategy {StrategyId} run {RunId}: {Reason}", entry.StrategyId, entry.RunId, reason);
            _registry.AppendLog(entry.RunId, $"risk guard close failed: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// A rule acted: told to the run's owner and the admins, with the rule's
    /// own words. After the close is booked; never throws.
    /// </summary>
    private static void PublishRisk(IDeskEventPublisher? deskEvents, long runId, long? ownerUserId, string? symbol, string reason)
        => deskEvents.TryPublish(new DeskEvent(DeskEventKinds.Risk, runId, ownerUserId, symbol, DateTime.UtcNow, reason));

    /// <summary>
    /// Whose run this is, for a run with no registry entry to say so (a
    /// manual book): the row knows, and its owner is who needs to be told.
    /// Null, and a debug line, when it cannot be read — the close is booked
    /// either way, and the admins are still told.
    /// </summary>
    private async Task<long?> OwnerOfAsync(TradingDbContext dbContext, long runId)
    {
        try
        {
            return await dbContext.SimulationRuns.AsNoTracking()
                .Where(x => x.Id == runId)
                .Select(x => (long?)x.UserId)
                .FirstOrDefaultAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the owner of run {RunId} for its risk event.", runId);
            return null;
        }
    }

    private static bool IsOpen(PaperPositionResponse pos)
        => string.Equals(pos.Status, "Open", StringComparison.OrdinalIgnoreCase);

    private TimeSpan MaxMarkAge()
        => TimeSpan.FromSeconds(Math.Max(1, _options.CurrentValue.RiskGuardMaxMarkAgeSeconds));

    /// <summary>A mark older than the limit. A position's UpdatedUtc is the time of the quote it was marked at.</summary>
    internal static bool IsStale(PaperPositionResponse pos, DateTime nowUtc, TimeSpan maxAge)
        => nowUtc - pos.UpdatedUtc > maxAge;

    /// <summary>
    /// Keeps one incident per run while any rule is skipped for a stale mark:
    /// logged and sent once when it starts, logged when every mark is fresh
    /// again. A run whose stale positions carry no rule to skip (only overall
    /// rules, no levels of their own) has nothing to report.
    /// </summary>
    private async Task TrackStaleMarksAsync(
        RunningStrategy entry,
        IReadOnlyList<PaperPositionResponse> stale,
        DateTime nowUtc,
        TimeSpan maxAge,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        long runId = entry.RunId;
        var rules = entry.Risk;
        bool skipsSomething = stale.Count > 0
            && (rules.Leg is { HasAnyRule: true }
                || rules.Group is { HasAnyRule: true }
                || stale.Any(x => x.StopLossPrice is not null || x.TargetPrice is not null));

        if (!skipsSomething)
        {
            if (_staleSince.Remove(runId, out var since))
            {
                _logger.LogInformation("Run {RunId}: marks are fresh again after {Seconds:0}s; leg and group rules resume.",
                    runId, (nowUtc - since).TotalSeconds);
                _registry.AppendLog(runId, "risk guard: marks are fresh again; leg and group rules resume");
            }
            return;
        }

        if (_staleSince.ContainsKey(runId)) return;
        _staleSince[runId] = nowUtc;

        double oldest = stale.Max(x => (nowUtc - x.UpdatedUtc).TotalSeconds);
        string legs = string.Join(", ", stale.Select(x => ContractLabel(x.Symbol)).Distinct());
        string limit = maxAge.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        string age = oldest.ToString("0", CultureInfo.InvariantCulture);

        _logger.LogWarning(
            "Risk guard pausing leg and group rules of strategy {StrategyId} ({Name}) run {RunId} on {Underlying}: "
            + "{Count} open position(s) marked from quotes older than {Limit}s (oldest {Age}s): {Legs}",
            entry.StrategyId, entry.Name, runId, entry.Underlying, stale.Count, limit, age, legs);
        _registry.AppendLog(runId, $"risk guard: leg and group rules paused — no price newer than {limit}s for {legs} (oldest {age}s)");

        var notifier = services.GetService<ISystemNotifier>();
        if (notifier is null) return;

        try
        {
            // The account first, as in every run alert: the same plan runs in
            // several accounts, and without the name two alerts read as one run
            // reported twice.
            string? owner = null;
            if (services.GetService<TradingDbContext>() is { } db)
            {
                owner = await db.AppUsers.AsNoTracking()
                    .Where(u => u.Id == entry.UserId)
                    .Select(u => u.UserName)
                    .FirstOrDefaultAsync(cancellationToken);
            }
            string tag = string.IsNullOrWhiteSpace(owner) ? string.Empty : $"[{owner}] ";

            await notifier.NotifyAsync(
                NotificationCategory.StrategyRun,
                NotificationSeverity.Warning,
                $"{tag}Risk rules paused — {entry.Name} on {entry.Underlying}",
                $"Run #{runId}: no price newer than {limit}s for {legs} (oldest {age}s). Their leg and group "
                + "stop-losses and targets are not checked until the quotes move again. The overall rules and the "
                + "market-close square-off still apply.",
                underlying: entry.Underlying,
                simulationRunId: runId,
                cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Telling the owner must never cost the sweep.
            _logger.LogWarning(ex, "Could not send the paused-rules alert for run {RunId}.", runId);
        }
    }

    /// <summary>"BANKNIFTY 57500 CE" from the FYERS symbol grammar; the raw symbol when it is not an option.</summary>
    internal static string ContractLabel(string symbol)
    {
        var parsed = UnderlyingCatalog.ParseOptionSymbol(symbol);
        if (parsed is null) return symbol;
        return $"{parsed.Underlying} {parsed.Strike.ToString("0.##", CultureInfo.InvariantCulture)} {parsed.OptionType}";
    }

    /// <summary>"−1,240" / "1,240": rupees with a typographic minus, no decimals.</summary>
    private static string Money(decimal value)
        => (value < 0 ? "−" : string.Empty) + Math.Abs(value).ToString("#,##0", CultureInfo.InvariantCulture);

    /// <summary>"₹5,000" / "−₹1,240": rupees with the sign in front of the symbol, no decimals.</summary>
    private static string Rupees(decimal value)
        => (value < 0 ? "−₹" : "₹") + Math.Abs(value).ToString("#,##0", CultureInfo.InvariantCulture);

    /// <summary>"+6.2" / "−21.4": one decimal with an explicit sign.</summary>
    private static string Signed(decimal value)
        => (value < 0 ? "−" : "+") + Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>"13.8": how far a value has fallen from its peak, unsigned, one decimal.</summary>
    private static string Drop(decimal peak, decimal value)
        => Math.Abs(peak - value).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Number(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
