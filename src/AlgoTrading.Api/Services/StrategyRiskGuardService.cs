// src/AlgoTrading.Api/Services/StrategyRiskGuardService.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Enforces each running strategy's risk rules from the API side, so the
/// guard works even when the Python runner is wedged. Every sweep, per run:
/// leg rules (close that leg only) → group rules (close every open leg of
/// that group; the run keeps going) → overall rules (flatten everything and
/// end the run). A position is never closed twice in one sweep; every trip is
/// logged; a failure on one run never skips the next.
/// </summary>
public sealed class StrategyRiskGuardService : BackgroundService
{
    public const string By = "risk-guard";

    private readonly StrategyProcessRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<StrategyRunnerOptions> _options;
    private readonly ILogger<StrategyRiskGuardService> _logger;

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

    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        var guarded = _registry.List()
            .Where(x => x.Risk.HasAnyRule && !x.StopRequested)
            .ToList();

        if (guarded.Count == 0) return;

        foreach (var entry in guarded)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var paperTrading = scope.ServiceProvider.GetRequiredService<IPaperTradingService>();
                var control = scope.ServiceProvider.GetRequiredService<StrategyRunControl>();

                await SweepAsync(entry, paperTrading, control, cancellationToken);
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
    }

    /// <summary>One run, one sweep: leg → group → overall.</summary>
    private async Task SweepAsync(
        RunningStrategy entry,
        IPaperTradingService paperTrading,
        StrategyRunControl control,
        CancellationToken cancellationToken)
    {
        // The registry entry may have been replaced by a risk update since the
        // list was taken; always enforce the newest rules.
        var current = _registry.Get(entry.RunId) ?? entry;
        if (current.StopRequested) return;

        var rules = current.Risk;
        long runId = current.RunId;

        // a. Marks open positions to market from the latest live quotes.
        var positions = await paperTrading.GetPaperPositionsAsync(runId, cancellationToken);
        var open = positions.Where(IsOpen).ToList();

        var closedThisSweep = new HashSet<long>();

        // b. Leg rules → one close per tripped leg, each with its own reason.
        if (rules.Leg is { HasAnyRule: true } leg && open.Count > 0)
        {
            foreach (var pos in open)
            {
                var reason = EvaluateLeg(pos, leg);
                if (reason is null) continue;

                closedThisSweep.Add(pos.Id);
                await CloseAsync(current, paperTrading, new[] { pos.Id }, reason, cancellationToken);
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

                decimal groupPnl = g.Sum(x => x.RealizedPnl) + openLegs.Sum(x => x.UnrealizedPnl);
                var reason = EvaluateGroup(g.Key, groupPnl, group);
                if (reason is null) continue;

                var ids = openLegs.Select(x => x.Id).Where(id => !closedThisSweep.Contains(id)).ToList();
                if (ids.Count == 0)
                {
                    _logger.LogInformation("Run {RunId}: {Reason} — every leg of the group was already closed by a leg rule this sweep.", runId, reason);
                    continue;
                }

                foreach (var id in ids) closedThisSweep.Add(id);
                await CloseAsync(current, paperTrading, ids, reason, cancellationToken);
            }
        }

        // e. Overall — a fresh summary, so what the leg/group closes realized
        //    counts, and the run ends when the total crosses the line.
        if (rules.Overall is { HasAnyRule: true } overall)
        {
            var summary = await paperTrading.GetPortfolioSummaryAsync(runId, cancellationToken);
            decimal totalPnl = summary.RealizedPnl + summary.UnrealizedPnl;

            string? reason = null;
            if (overall.StopLoss.HasValue && totalPnl <= -overall.StopLoss.Value)
            {
                reason = $"Stop loss hit: P&L {Money(totalPnl)} ≤ −{Money(overall.StopLoss.Value)}";
            }
            else if (overall.Target.HasValue && totalPnl >= overall.Target.Value)
            {
                reason = $"Target hit: P&L {Money(totalPnl)} ≥ {Money(overall.Target.Value)}";
            }

            if (reason is null) return;

            _logger.LogWarning("Risk guard tripping strategy {StrategyId} ({Name}) run {RunId} on {Underlying}: {Reason}",
                current.StrategyId, current.Name, runId, current.Underlying, reason);

            await control.StopAsync(runId, reason, flatten: true, by: By, cancellationToken);
        }
    }

    /// <summary>
    /// Leg rule for one open position. Adverse move = BUY: entry − ltp, SELL:
    /// ltp − entry; percent of entry. Checked SL points, SL percent, target
    /// points, target percent — whichever trips first wins. Null when nothing trips.
    /// </summary>
    internal static string? EvaluateLeg(PaperPositionResponse pos, LegRiskDto leg)
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

        if (leg.StopLossPoints.HasValue && adverse >= leg.StopLossPoints.Value)
        {
            return $"Leg stop-loss hit: {label} {Signed(pnlPoints)} pts ({Signed(pnlPct)}%) ≤ −{Number(leg.StopLossPoints.Value)} pts";
        }

        if (leg.StopLossPercent.HasValue && adversePct >= leg.StopLossPercent.Value)
        {
            return $"Leg stop-loss hit: {label} {Signed(pnlPoints)} pts ({Signed(pnlPct)}%) ≤ −{Number(leg.StopLossPercent.Value)}%";
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

    /// <summary>Group rule on the group's realized + open unrealized P&amp;L. Null when nothing trips.</summary>
    internal static string? EvaluateGroup(string groupId, decimal groupPnl, GroupRiskDto group)
    {
        var name = string.IsNullOrWhiteSpace(groupId) ? "(no group)" : groupId;

        if (group.StopLoss.HasValue && groupPnl <= -group.StopLoss.Value)
        {
            return $"Group stop-loss hit: {name} P&L {Money(groupPnl)} ≤ −{Money(group.StopLoss.Value)}";
        }

        if (group.Target.HasValue && groupPnl >= group.Target.Value)
        {
            return $"Group target hit: {name} P&L {Money(groupPnl)} ≥ {Money(group.Target.Value)}";
        }

        return null;
    }

    private async Task CloseAsync(
        RunningStrategy entry,
        IPaperTradingService paperTrading,
        IReadOnlyList<long> positionIds,
        string reason,
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

    private static bool IsOpen(PaperPositionResponse pos)
        => string.Equals(pos.Status, "Open", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>"+6.2" / "−21.4": one decimal with an explicit sign.</summary>
    private static string Signed(decimal value)
        => (value < 0 ? "−" : "+") + Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Number(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}
