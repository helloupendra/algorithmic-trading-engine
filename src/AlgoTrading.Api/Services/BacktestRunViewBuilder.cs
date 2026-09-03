// src/AlgoTrading.Api/Services/BacktestRunViewBuilder.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Backtest;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Shapes OfflineReplay runs for the API: the list rows (one grouped query
/// over paper positions) and the full results view built from the database
/// plus the registry while the runner is alive. Never marks to market from
/// live quotes — a replay's marks are the bar closes the runner stored.
/// </summary>
public sealed class BacktestRunViewBuilder
{
    public const string OfflineReplayMode = "OfflineReplay";
    public const string SkippedEntryActivityType = "SKIPPED_ENTRY";

    private const int MaxListRows = 200;
    private const int ActivityLimit = 400;
    private const int MaxDetailedSkipNotes = 50;

    // The view is polled every 2 s while a run replays and a 1m year is ~94k
    // snapshots, so the curve is thinned to at most this many points (metrics
    // are still computed over every snapshot; the full series stays available
    // at GET /api/Simulator/runs/{id}/equity-curve).
    private const int MaxCurvePoints = 2000;

    private readonly TradingDbContext _dbContext;
    private readonly BacktestProcessRegistry _registry;
    private readonly IPaperTradingService _paperTrading;
    private readonly ILotSizeResolver _lotSizeResolver;
    private readonly PositionViewBuilder _positionViews;

    public BacktestRunViewBuilder(
        TradingDbContext dbContext,
        BacktestProcessRegistry registry,
        IPaperTradingService paperTrading,
        ILotSizeResolver lotSizeResolver,
        PositionViewBuilder positionViews)
    {
        _dbContext = dbContext;
        _registry = registry;
        _paperTrading = paperTrading;
        _lotSizeResolver = lotSizeResolver;
        _positionViews = positionViews;
    }

    // ------------------------------------------------------------------
    // List
    // ------------------------------------------------------------------

    public async Task<List<BacktestRunSummaryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var runs = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Mode == OfflineReplayMode)
            .OrderByDescending(x => x.CreatedUtc)
            .ThenByDescending(x => x.Id)
            .Take(MaxListRows)
            .ToListAsync(cancellationToken);

        if (runs.Count == 0) return new List<BacktestRunSummaryResponse>();

        var runIds = runs.Select(x => x.Id).ToList();

        var stats = await _dbContext.PaperPositions.AsNoTracking()
            .Where(p => runIds.Contains(p.SimulationRunId))
            .GroupBy(p => p.SimulationRunId)
            .Select(g => new
            {
                RunId = g.Key,
                NetPnl = g.Sum(x => x.RealizedPnl),
                Closed = g.Count(x => x.Status == "Closed"),
                Wins = g.Count(x => x.Status == "Closed" && x.RealizedPnl > 0)
            })
            .ToListAsync(cancellationToken);
        var statsByRun = stats.ToDictionary(x => x.RunId);

        // Filled lots per run, so netPnl can be netted of charges exactly like
        // the detail view's pnl.total (chargesPerLot x filled lots).
        var filledLots = await _dbContext.PaperOrders.AsNoTracking()
            .Where(o => runIds.Contains(o.SimulationRunId) && o.Status == "Filled")
            .GroupBy(o => o.SimulationRunId)
            .Select(g => new { RunId = g.Key, Lots = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.RunId, x => x.Lots, cancellationToken);

        var stopReasons = await LoadStopReasonsAsync(runIds, cancellationToken);

        var userIds = runs.Select(x => x.UserId).Distinct().ToList();
        var userNames = await _dbContext.AppUsers.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);

        var rows = new List<BacktestRunSummaryResponse>(runs.Count);
        foreach (var run in runs)
        {
            var p = BacktestRunParameters.Parse(run.ParametersJson);
            var running = _registry.Get(run.Id);
            statsByRun.TryGetValue(run.Id, out var s);

            var underlying = running?.Underlying
                             ?? p.Underlying
                             ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                             ?? UnderlyingCatalog.InferUnderlying(run.Symbol);

            decimal charges = p.ChargesPerLot is > 0 && filledLots.TryGetValue(run.Id, out var lotsFilled)
                ? p.ChargesPerLot.Value * lotsFilled
                : 0m;

            rows.Add(new BacktestRunSummaryResponse
            {
                RunId = run.Id,
                StrategyName = run.StrategyName,
                StrategyId = StrategyCatalogService.StableId(run.StrategyName),
                Underlying = underlying,
                SpotSymbol = run.Symbol,
                Resolution = ResolutionCodes.ToCandle(run.Resolution),
                FromDate = run.FromUtc.HasValue ? IstTime.DateString(run.FromUtc.Value) : string.Empty,
                ToDate = run.ToUtc.HasValue ? IstTime.DateString(run.ToUtc.Value) : string.Empty,
                Lots = running?.Lots ?? p.Lots ?? 0,
                StopLoss = running?.StopLoss ?? p.StopLoss,
                Target = running?.Target ?? p.Target,
                Status = run.Status,
                ProgressPercent = ProgressPercentOf(run, running),
                NetPnl = (s?.NetPnl ?? 0m) - charges,
                Trades = s?.Closed ?? 0,
                WinRatePercent = s is { Closed: > 0 } ? Math.Round((decimal)s.Wins / s.Closed * 100m, 2) : 0m,
                StopReason = stopReasons.TryGetValue(run.Id, out var stopReason) ? stopReason : null,
                CreatedUtc = run.CreatedUtc,
                StartedUtc = run.StartedUtc,
                CompletedUtc = run.CompletedUtc,
                StartedBy = running?.StartedBy ?? (userNames.TryGetValue(run.UserId, out var name) ? name : null),
                LastError = string.IsNullOrWhiteSpace(run.LastError) ? null : run.LastError
            });
        }

        return rows;
    }

    private static decimal ProgressPercentOf(SimulationRun run, RunningBacktest? running)
    {
        if (running is not null) return running.Progress.Percent;
        return run.Status == BacktestRunControl.RunStatusCompleted ? 100m : 0m;
    }

    /// <summary>
    /// Why each run ended early, with the same precedence as the detail view:
    /// the BACKTEST_SUMMARY stopReason (SL/target trip), else the RUN_STOPPED
    /// reason (user stop, runner exit). Summaries are only pulled when they
    /// carry a string stopReason — the JSON is big (skipped entries) and most
    /// runs replayed every bar.
    /// </summary>
    private async Task<Dictionary<long, string>> LoadStopReasonsAsync(List<long> runIds, CancellationToken cancellationToken)
    {
        const string summaryType = PaperTradingService.BacktestSummarySignalType;
        const string stoppedType = StrategyRunControl.RunStoppedSignalType;

        var signals = await _dbContext.SimulationSignals.AsNoTracking()
            .Where(x => runIds.Contains(x.SimulationRunId)
                        && (x.SignalType == stoppedType
                            || (x.SignalType == summaryType
                                && (EF.Functions.Like(x.MetadataJson, "%\"stopReason\": \"%")
                                    || EF.Functions.Like(x.MetadataJson, "%\"stopReason\":\"%")))))
            .OrderBy(x => x.Id)
            .Select(x => new { x.SimulationRunId, x.SignalType, x.MetadataJson })
            .ToListAsync(cancellationToken);

        var fromSummary = new Dictionary<long, string>();
        var fromStop = new Dictionary<long, string>();
        foreach (var s in signals)
        {
            if (s.SignalType == summaryType)
            {
                var reason = ParseSummary(s.MetadataJson)?.StopReason;
                if (!string.IsNullOrWhiteSpace(reason)) fromSummary[s.SimulationRunId] = reason;
            }
            else
            {
                var reason = SignalMetadata.ReadReason(s.MetadataJson);
                if (!string.IsNullOrWhiteSpace(reason)) fromStop[s.SimulationRunId] = reason;
            }
        }

        var result = new Dictionary<long, string>(fromStop);
        foreach (var (runId, reason) in fromSummary) result[runId] = reason;
        return result;
    }

    // ------------------------------------------------------------------
    // Detail
    // ------------------------------------------------------------------

    /// <summary>Null when the run does not exist or is not an OfflineReplay run.</summary>
    public async Task<BacktestRunViewResponse?> BuildAsync(long runId, CancellationToken cancellationToken)
    {
        var run = await _dbContext.SimulationRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId && x.Mode == OfflineReplayMode, cancellationToken);
        if (run is null) return null;

        var p = BacktestRunParameters.Parse(run.ParametersJson);
        var running = _registry.Get(run.Id);

        var underlying = running?.Underlying
                         ?? p.Underlying
                         ?? UnderlyingCatalog.UnderlyingForSpot(run.Symbol)
                         ?? UnderlyingCatalog.InferUnderlying(run.Symbol);

        // The lot size frozen into the run at start (what the runner and the
        // paper engine booked with); older rows without it fall back to today's.
        var lot = p.LotSize is > 0
            ? new LotSizeInfo(p.LotSize.Value, string.IsNullOrWhiteSpace(p.LotSizeSource) ? LotSizeInfo.SourceMaster : p.LotSizeSource, underlying)
            : await _lotSizeResolver.ResolveForUnderlyingAsync(underlying, cancellationToken);

        var view = new BacktestRunViewResponse
        {
            RunId = run.Id,
            StrategyId = StrategyCatalogService.StableId(run.StrategyName),
            StrategyName = run.StrategyName,
            Underlying = underlying,
            SpotSymbol = run.Symbol,
            Resolution = ResolutionCodes.ToCandle(run.Resolution),
            FromDate = run.FromUtc.HasValue ? IstTime.DateString(run.FromUtc.Value) : string.Empty,
            ToDate = run.ToUtc.HasValue ? IstTime.DateString(run.ToUtc.Value) : string.Empty,
            Lots = running?.Lots ?? p.Lots ?? 0,
            LotSize = lot.LotSize,
            LotSizeSource = lot.Source,
            StopLoss = running?.StopLoss ?? p.StopLoss,
            Target = running?.Target ?? p.Target,
            EodSquareOffIst = p.EodSquareOffIst ?? BacktestRunParameters.DefaultEodSquareOffIst,
            ChargesPerLot = p.ChargesPerLot ?? 0m,
            InitialCapital = run.InitialCapital,
            ParametersJson = run.ParametersJson,
            Status = run.Status,
            LastError = string.IsNullOrWhiteSpace(run.LastError) ? null : run.LastError,
            StartedUtc = run.StartedUtc,
            CompletedUtc = run.CompletedUtc
        };

        view.StartedBy = running?.StartedBy ?? await _dbContext.AppUsers.AsNoTracking()
            .Where(x => x.Id == run.UserId)
            .Select(x => x.UserName)
            .FirstOrDefaultAsync(cancellationToken);

        // Positions: stored marks only (the runner's bar closes), never live quotes.
        var paperPositions = await _paperTrading.GetPaperPositionsAsync(run.Id, cancellationToken);
        var built = await _positionViews.BuildAsync<BacktestPosition>(
            paperPositions, useLiveQuotes: false, spotSymbol: null, cancellationToken, lotSizeOverride: lot.LotSize);
        view.Positions = built.Positions;

        var orders = await _dbContext.PaperOrders.AsNoTracking()
            .Where(x => x.SimulationRunId == run.Id)
            .OrderBy(x => x.FilledUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        var signals = await _dbContext.SimulationSignals.AsNoTracking()
            .Where(x => x.SimulationRunId == run.Id)
            .OrderBy(x => x.TimestampUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var signalById = signals.ToDictionary(x => x.Id);

        DecorateExits(view.Positions, orders, signalById);

        // Charges: flat rupees per lot per fill. The runner's ledger nets them
        // into the P&L it watches for SL/target and into every equity snapshot;
        // the stored positions carry price P&L only, so they are netted here.
        decimal chargesPerLot = view.ChargesPerLot;
        decimal charges = chargesPerLot > 0
            ? chargesPerLot * orders.Where(o => o.Status == "Filled").Sum(o => o.Quantity)
            : 0m;

        decimal realized = paperPositions.Sum(x => x.RealizedPnl);
        decimal unrealized = paperPositions
            .Where(x => string.Equals(x.Status, "Open", StringComparison.OrdinalIgnoreCase))
            .Sum(x => x.UnrealizedPnl);
        decimal total = realized + unrealized - charges;

        view.Pnl = new BacktestPnl
        {
            Realized = realized,
            Unrealized = unrealized,
            Charges = charges,
            Total = total,
            ReturnPercent = run.InitialCapital > 0 ? Math.Round(total / run.InitialCapital * 100m, 4) : 0m
        };

        // Equity curve (historical SnapshotUtc, posted by the runner).
        var snapshots = await _dbContext.SimulationEquitySnapshots.AsNoTracking()
            .Where(x => x.SimulationRunId == run.Id)
            .OrderBy(x => x.SnapshotUtc)
            .ThenBy(x => x.Id)
            .Select(x => new BacktestEquityPoint
            {
                AtUtc = x.SnapshotUtc,
                Equity = x.CurrentEquity,
                Realized = x.RealizedPnl,
                Unrealized = x.UnrealizedPnl
            })
            .ToListAsync(cancellationToken);

        view.Daily = BuildDaily(paperPositions);
        view.Metrics = BuildMetrics(paperPositions, snapshots, view.Daily);
        view.EquityCurve = ThinCurve(snapshots, MaxCurvePoints);

        // Summary (BACKTEST_SUMMARY) and stop reason (RUN_STOPPED).
        var summarySignal = signals.LastOrDefault(x => x.SignalType == PaperTradingService.BacktestSummarySignalType);
        var summary = summarySignal is null ? null : ParseSummary(summarySignal.MetadataJson);

        var stopped = signals.LastOrDefault(x => x.SignalType == StrategyRunControl.RunStoppedSignalType);
        view.StopReason = summary?.StopReason
                          ?? (stopped is null ? null : SignalMetadata.ReadReason(stopped.MetadataJson) ?? stopped.SignalType);

        view.Progress = BuildProgress(run, running, summary, paperPositions.Count(x => x.Status == "Closed"));
        view.DataNotes = BuildDataNotes(summary, lot);
        view.Activity = BuildActivity(signals, summary);

        return view;
    }

    // ------------------------------------------------------------------
    // Pieces
    // ------------------------------------------------------------------

    /// <summary>
    /// exitPrice = fill of the latest opposite-side order for the position's
    /// (group, symbol); exitReason = reason of the signal that order belongs to.
    /// </summary>
    private static void DecorateExits(List<BacktestPosition> positions, List<PaperOrder> orders, Dictionary<long, SimulationSignal> signalById)
    {
        var ordersByKey = orders
            .GroupBy(o => (o.GroupId, o.Symbol))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var pos in positions)
        {
            if (pos.Status != "Closed") continue;
            if (!ordersByKey.TryGetValue((pos.GroupId, pos.Symbol), out var candidates)) continue;

            var closingSide = pos.Side == "BUY" ? "SELL" : "BUY";
            var closing = candidates
                .Where(o => o.Side == closingSide && o.Status == "Filled")
                .Where(o => !pos.ClosedUtc.HasValue || o.FilledUtc is null || o.FilledUtc <= pos.ClosedUtc.Value.AddSeconds(1))
                .OrderByDescending(o => o.FilledUtc)
                .ThenByDescending(o => o.Id)
                .FirstOrDefault();
            if (closing is null) continue;

            pos.ExitPrice = closing.FillPrice;
            if (closing.SimulationSignalId.HasValue && signalById.TryGetValue(closing.SimulationSignalId.Value, out var signal))
            {
                pos.ExitReason = SignalMetadata.ReadReason(signal.MetadataJson) ?? signal.SignalType;
            }
        }
    }

    /// <summary>
    /// Every point when the curve is short; otherwise a fixed stride plus the
    /// last point and the peak/trough pair of the maximum drawdown, so the
    /// chart still shows the worst excursion the metrics report.
    /// </summary>
    private static List<BacktestEquityPoint> ThinCurve(List<BacktestEquityPoint> curve, int maxPoints)
    {
        if (curve.Count <= maxPoints) return curve;

        int stride = (int)Math.Ceiling(curve.Count / (double)maxPoints);
        var keep = new SortedSet<int>();
        for (int i = 0; i < curve.Count; i += stride) keep.Add(i);
        keep.Add(curve.Count - 1);

        decimal peak = decimal.MinValue;
        int peakIdx = 0, ddPeakIdx = -1, ddTroughIdx = -1;
        decimal maxDd = 0m;
        for (int i = 0; i < curve.Count; i++)
        {
            if (curve[i].Equity > peak)
            {
                peak = curve[i].Equity;
                peakIdx = i;
            }
            decimal dd = peak - curve[i].Equity;
            if (dd > maxDd)
            {
                maxDd = dd;
                ddPeakIdx = peakIdx;
                ddTroughIdx = i;
            }
        }
        if (ddPeakIdx >= 0) keep.Add(ddPeakIdx);
        if (ddTroughIdx >= 0) keep.Add(ddTroughIdx);

        return keep.Select(i => curve[i]).ToList();
    }

    private static List<BacktestDailyPnl> BuildDaily(IReadOnlyList<Contracts.Simulator.PaperPositionResponse> positions)
        => positions
            .Where(x => x.Status == "Closed" && x.ClosedUtc.HasValue)
            .GroupBy(x => IstTime.DateOf(x.ClosedUtc!.Value))
            .OrderBy(g => g.Key)
            .Select(g => new BacktestDailyPnl
            {
                Date = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Pnl = g.Sum(x => x.RealizedPnl),
                Trades = g.Count()
            })
            .ToList();

    private static BacktestMetrics BuildMetrics(
        IReadOnlyList<Contracts.Simulator.PaperPositionResponse> positions,
        List<BacktestEquityPoint> curve,
        List<BacktestDailyPnl> daily)
    {
        var closed = positions.Where(x => x.Status == "Closed").ToList();
        var wins = closed.Where(x => x.RealizedPnl > 0).ToList();
        var losses = closed.Where(x => x.RealizedPnl < 0).ToList();

        decimal grossProfit = wins.Sum(x => x.RealizedPnl);
        decimal grossLoss = losses.Sum(x => Math.Abs(x.RealizedPnl));

        decimal peak = decimal.MinValue;
        decimal maxDdAmount = 0m;
        decimal maxDdPercent = 0m;
        foreach (var point in curve)
        {
            if (point.Equity > peak) peak = point.Equity;
            decimal dd = peak - point.Equity;
            if (dd > maxDdAmount)
            {
                maxDdAmount = dd;
                maxDdPercent = peak > 0 ? dd / peak * 100m : 0m;
            }
        }

        var tradingDays = curve.Count > 0
            ? curve.Select(x => IstTime.DateOf(x.AtUtc)).Distinct().Count()
            : daily.Count;

        return new BacktestMetrics
        {
            ClosedPositions = closed.Count,
            Winning = wins.Count,
            Losing = losses.Count,
            WinRatePercent = closed.Count > 0 ? Math.Round((decimal)wins.Count / closed.Count * 100m, 2) : 0m,
            GrossProfit = grossProfit,
            GrossLoss = grossLoss,
            ProfitFactor = grossLoss > 0 ? Math.Round(grossProfit / grossLoss, 4) : 0m,
            AverageWin = wins.Count > 0 ? wins.Average(x => x.RealizedPnl) : 0m,
            AverageLoss = losses.Count > 0 ? losses.Average(x => Math.Abs(x.RealizedPnl)) : 0m,
            Expectancy = closed.Count > 0 ? closed.Sum(x => x.RealizedPnl) / closed.Count : 0m,
            MaxDrawdownPercent = Math.Round(maxDdPercent, 4),
            MaxDrawdownAmount = maxDdAmount,
            LargestWin = wins.Count > 0 ? wins.Max(x => x.RealizedPnl) : 0m,
            LargestLoss = losses.Count > 0 ? losses.Min(x => x.RealizedPnl) : 0m,
            TradingDays = tradingDays,
            ProfitableDays = daily.Count(d => d.Pnl > 0)
        };
    }

    private static BacktestProgress? BuildProgress(SimulationRun run, RunningBacktest? running, RunSummary? summary, int closedPositions)
    {
        if (running is not null)
        {
            var s = running.Progress;
            return new BacktestProgress
            {
                Percent = s.Percent,
                BarsProcessed = s.BarsProcessed,
                TotalBars = s.TotalBars,
                CurrentUtc = s.CurrentUtc,
                Trades = s.Trades,
                Message = s.Message
            };
        }

        if (summary is null)
        {
            return run.Status == BacktestRunControl.RunStatusCompleted
                ? new BacktestProgress { Percent = 100m, Trades = closedPositions, CurrentUtc = run.ToUtc, Message = "Completed" }
                : null;
        }

        bool completed = run.Status == BacktestRunControl.RunStatusCompleted;
        return new BacktestProgress
        {
            Percent = completed ? 100m : 0m,
            BarsProcessed = summary.TotalBars ?? 0,
            TotalBars = summary.TotalBars ?? 0,
            CurrentUtc = run.ToUtc,
            Trades = summary.Trades ?? closedPositions,
            Message = summary.StopReason ?? run.Status
        };
    }

    /// <summary>
    /// The runner phrases a reason per contract ("no premium history for
    /// NSE:…57700PE"); strip the contract so entries group by the kind of
    /// problem rather than one line per symbol.
    /// </summary>
    private static string ReasonFamily(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "no price";
        var text = reason.Trim();
        int forIdx = text.IndexOf(" for ", StringComparison.Ordinal);
        if (forIdx > 0 && text.IndexOf(':', forIdx) > forIdx)
        {
            text = text[..forIdx];
        }
        return text;
    }

    private static List<string> BuildDataNotes(RunSummary? summary, LotSizeInfo lot)
    {
        var notes = new List<string>
        {
            $"Lot size {lot.LotSize} ({lot.Source}) applied to every contract over the whole range; historical lot-size changes are not modelled."
        };

        if (summary is null) return notes;

        notes.AddRange(summary.DataNotes.Where(n => !string.IsNullOrWhiteSpace(n)));

        if (summary.SkippedEntries.Count > 0)
        {
            // One line per reason with the contracts it hit; the per-entry
            // detail (time + contract) is already in the activity feed as
            // SKIPPED_ENTRY rows, so it is not repeated here.
            foreach (var group in summary.SkippedEntries
                         .GroupBy(x => ReasonFamily(x.Reason))
                         .OrderByDescending(g => g.Count()))
            {
                int n = group.Count();
                var symbols = group
                    .Select(x => x.Symbol)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList();
                var shown = symbols.Take(MaxDetailedSkipNotes).ToList();
                var suffix = symbols.Count > shown.Count ? $" … and {symbols.Count - shown.Count} more" : string.Empty;
                var subject = symbols.Count == 0
                    ? string.Empty
                    : $" ({symbols.Count} {(symbols.Count == 1 ? "contract" : "contracts")}: {string.Join(", ", shown)}{suffix})";
                notes.Add($"Skipped {n} {(n == 1 ? "entry" : "entries")} — {group.Key}{subject}");
            }
        }

        if (summary.EodSquareOffs is > 0)
        {
            notes.Add($"{summary.EodSquareOffs} end-of-day square-off(s) were applied.");
        }

        return notes;
    }

    private static List<LiveActivityResponse> BuildActivity(List<SimulationSignal> signals, RunSummary? summary)
    {
        var items = new List<LiveActivityResponse>(signals.Count + (summary?.SkippedEntries.Count ?? 0));

        foreach (var s in signals)
        {
            if (s.SignalType == PaperTradingService.BacktestSummarySignalType) continue;

            var reason = SignalMetadata.ReadReason(s.MetadataJson);
            items.Add(new LiveActivityResponse
            {
                AtUtc = s.TimestampUtc,
                Type = s.SignalType,
                Text = string.IsNullOrWhiteSpace(reason) ? s.SignalType : reason,
                GroupId = s.GroupId
            });
        }

        if (summary is not null)
        {
            foreach (var entry in summary.SkippedEntries.Where(x => x.AtUtc.HasValue))
            {
                items.Add(new LiveActivityResponse
                {
                    AtUtc = entry.AtUtc!.Value,
                    Type = SkippedEntryActivityType,
                    Text = $"Skipped entry {entry.Symbol}: {entry.Reason}",
                    GroupId = string.Empty
                });
            }
        }

        return items
            .OrderByDescending(x => x.AtUtc)
            .ThenBy(x => x.Type == SkippedEntryActivityType ? 1 : 0)
            .Take(ActivityLimit)
            .ToList();
    }

    private static string StampOf(DateTime? utc)
        => utc.HasValue ? IstTime.ShortStamp(utc.Value) : "unknown time";

    // ------------------------------------------------------------------
    // BACKTEST_SUMMARY metadata
    // ------------------------------------------------------------------

    private sealed record SkippedEntry(DateTime? AtUtc, string Symbol, string? Reason);

    private sealed class RunSummary
    {
        public long? TotalBars { get; set; }
        public int? Sessions { get; set; }
        public int? Trades { get; set; }
        public int? EodSquareOffs { get; set; }
        public string? StopReason { get; set; }
        public List<string> DataNotes { get; } = new();
        public List<SkippedEntry> SkippedEntries { get; } = new();
    }

    private static RunSummary? ParseSummary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = doc.RootElement;

            var summary = new RunSummary
            {
                TotalBars = ReadLong(root, "totalBars"),
                Sessions = (int?)ReadLong(root, "sessions"),
                Trades = (int?)ReadLong(root, "trades"),
                EodSquareOffs = (int?)ReadLong(root, "eodSquareOffs"),
                StopReason = ReadString(root, "stopReason")
            };

            if (TryGet(root, "dataNotes", out var notes) && notes.ValueKind == JsonValueKind.Array)
            {
                foreach (var n in notes.EnumerateArray())
                {
                    if (n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                        summary.DataNotes.Add(n.GetString()!);
                }
            }

            if (TryGet(root, "skippedEntries", out var skipped) && skipped.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in skipped.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    DateTime? at = null;
                    var atText = ReadString(e, "atUtc");
                    if (!string.IsNullOrWhiteSpace(atText)
                        && DateTime.TryParse(atText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        at = parsed;
                    }
                    summary.SkippedEntries.Add(new SkippedEntry(at, ReadString(e, "symbol") ?? string.Empty, ReadString(e, "reason")));
                }
            }

            return summary;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText()
        };
    }

    private static long? ReadLong(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt64(out var n) => n,
            JsonValueKind.Number when el.TryGetDecimal(out var d) => (long)d,
            JsonValueKind.String when long.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        };
    }
}
