// src/AlgoTrading.Api/Services/LiveRunHistoryBuilder.cs
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Filters of GET /api/Strategy/runs. <see cref="UserId"/> is already resolved
/// by the controller (a trader always gets their own id); null lists every user.
/// Dates are IST calendar days applied to StartedUtc ?? CreatedUtc.
/// </summary>
public sealed record LiveRunHistoryFilter(
    long? UserId,
    int? StrategyId,
    string? Underlying,
    string? Status,
    DateOnly? FromDate,
    DateOnly? ToDate,
    int Take,
    int Skip)
{
    public const int DefaultTake = 100;
    public const int MaxTake = 500;

    /// <summary>"any" (or empty) means no status filter.</summary>
    public const string AnyStatus = "any";
}

/// <summary>
/// Shapes the per-user history of LivePaper runs: the list rows (one grouped
/// query over paper positions per page of runs, one query for the RUN_STOPPED
/// reasons, user names from AppUsers) and the per-user rollup. Active runs
/// take isActive and their unrealized P&amp;L from the registry plus the same
/// mark-to-market the live view uses (latest live quote, stored mark as the
/// fallback). Nothing here writes: the history is read-only by design.
/// </summary>
public sealed class LiveRunHistoryBuilder
{
    private const string LivePaperMode = StrategyRunControl.LivePaperMode;
    private const string OpenStatus = "Open";
    private const string ClosedStatus = "Closed";

    /// <summary>
    /// What a finished run's stop reason groups under when nothing recorded one
    /// — a run the API lost track of across a restart, most often. It is its own
    /// bucket rather than dropped, because "we do not know why 40 runs ended" is
    /// the row most worth seeing.
    /// </summary>
    private const string UnknownStopReason = "Not recorded";

    /// <summary>
    /// Where a RUN_STOPPED reason stops being the reason and starts being its
    /// detail. Kept in the order the API writes them.
    /// </summary>
    private static readonly string[] StopReasonSeparators = { ":", " (", ";", " — " };

    /// <summary>Canonical spellings of SimulationRun.Status a live run can carry.</summary>
    private static readonly string[] KnownStatuses =
    {
        StrategyRunControl.RunStatusRunning,
        StrategyRunControl.RunStatusStopping,
        StrategyRunControl.RunStatusStopped,
        "Failed",
        "Completed",
        "Pending"
    };

    private readonly TradingDbContext _dbContext;
    private readonly StrategyProcessRegistry _registry;
    private readonly StrategyCatalogService _catalog;
    private readonly ILotSizeResolver _lotSizeResolver;

    public LiveRunHistoryBuilder(
        TradingDbContext dbContext,
        StrategyProcessRegistry registry,
        StrategyCatalogService catalog,
        ILotSizeResolver lotSizeResolver)
    {
        _dbContext = dbContext;
        _registry = registry;
        _catalog = catalog;
        _lotSizeResolver = lotSizeResolver;
    }

    // ------------------------------------------------------------------
    // List
    // ------------------------------------------------------------------

    public async Task<List<LiveRunSummaryResponse>> ListAsync(LiveRunHistoryFilter filter, CancellationToken cancellationToken)
    {
        var catalog = await _catalog.GetAllAsync(cancellationToken);
        var catalogByName = catalog
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var query = _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Mode == LivePaperMode);

        if (filter.UserId.HasValue)
        {
            long userId = filter.UserId.Value;
            query = query.Where(x => x.UserId == userId);
        }

        if (filter.StrategyId.HasValue)
        {
            var names = await StrategyNamesForIdAsync(filter.StrategyId.Value, catalog, cancellationToken);
            if (names.Count == 0) return new List<LiveRunSummaryResponse>();
            query = query.Where(x => names.Contains(x.StrategyName));
        }

        string? underlyingFilter = string.IsNullOrWhiteSpace(filter.Underlying)
            ? null
            : filter.Underlying.Trim().ToUpperInvariant();

        var status = NormalizeStatus(filter.Status);
        if (status is not null)
        {
            if (status == StrategyRunControl.RunStatusRunning)
            {
                // A run being ended still reads as running until its row is closed.
                query = query.Where(x => x.Status == StrategyRunControl.RunStatusRunning
                                         || x.Status == StrategyRunControl.RunStatusStopping);
            }
            else
            {
                query = query.Where(x => x.Status == status);
            }
        }

        if (filter.FromDate.HasValue)
        {
            var fromUtc = IstTime.StartOfDayUtc(filter.FromDate.Value);
            query = query.Where(x => (x.StartedUtc ?? x.CreatedUtc) >= fromUtc);
        }

        if (filter.ToDate.HasValue)
        {
            var toUtc = IstTime.EndOfDayUtc(filter.ToDate.Value);
            query = query.Where(x => (x.StartedUtc ?? x.CreatedUtc) <= toUtc);
        }

        int take = Math.Clamp(filter.Take <= 0 ? LiveRunHistoryFilter.DefaultTake : filter.Take, 1, LiveRunHistoryFilter.MaxTake);
        int skip = Math.Max(0, filter.Skip);

        var ordered = query
            .OrderByDescending(x => x.StartedUtc ?? x.CreatedUtc)
            .ThenByDescending(x => x.Id);

        List<SimulationRun> runs;
        if (underlyingFilter is null)
        {
            runs = await ordered
                .Skip(skip)
                .Take(take)
                .ToListAsync(cancellationToken);
        }
        else
        {
            // The underlying of a run is derived (registry entry, remembered
            // exit, "underlying" in parametersJson, spot symbol) and not stored
            // as a column, so the page is cut AFTER the exact check: every
            // candidate's derivation keys are read (id, symbol, parameters),
            // the matching ids are paged in memory, then only that page's rows
            // are loaded. Filtering after Skip/Take would return short pages
            // and skip offsets over rows the caller never sees.
            var candidates = await ordered
                .Select(x => new { x.Id, x.Symbol, x.ParametersJson })
                .ToListAsync(cancellationToken);

            var pageIds = candidates
                .Where(c => string.Equals(
                    DeriveUnderlying(c.Id, c.Symbol, LiveRunParameters.Parse(c.ParametersJson)),
                    underlyingFilter,
                    StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Id)
                .Skip(skip)
                .Take(take)
                .ToList();

            if (pageIds.Count == 0) return new List<LiveRunSummaryResponse>();

            var byId = await query
                .Where(x => pageIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);

            runs = pageIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
        }

        if (runs.Count == 0) return new List<LiveRunSummaryResponse>();

        var runIds = runs.Select(x => x.Id).ToList();

        var stats = await _dbContext.PaperPositions.AsNoTracking()
            .Where(p => runIds.Contains(p.SimulationRunId))
            .GroupBy(p => p.SimulationRunId)
            .Select(g => new
            {
                RunId = g.Key,
                Realized = g.Sum(x => x.RealizedPnl),
                Closed = g.Count(x => x.Status == ClosedStatus),
                Open = g.Count(x => x.Status == OpenStatus),
                StoredUnrealized = g.Where(x => x.Status == OpenStatus).Sum(x => x.UnrealizedPnl)
            })
            .ToDictionaryAsync(x => x.RunId, cancellationToken);

        var groupsByRun = (await _dbContext.PaperPositions.AsNoTracking()
                .Where(p => runIds.Contains(p.SimulationRunId))
                .Select(p => new { p.SimulationRunId, p.GroupId })
                .Distinct()
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.SimulationRunId)
            .ToDictionary(g => g.Key, g => g.Count());

        var stops = await LoadStopsAsync(runIds, cancellationToken);

        var userIds = runs.Select(x => x.UserId).Distinct().ToList();
        var userNames = await _dbContext.AppUsers.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);

        var activeRunIds = runIds.Where(_registry.Contains).ToList();
        var liveMarks = await MarkActiveRunsAsync(activeRunIds, cancellationToken);

        var lotSizeByUnderlying = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        var rows = new List<LiveRunSummaryResponse>(runs.Count);

        foreach (var run in runs)
        {
            var p = LiveRunParameters.Parse(run.ParametersJson);
            var running = _registry.Get(run.Id);
            var exit = running is null ? _registry.GetExitByRun(run.Id) : null;

            var underlying = DeriveUnderlying(running, exit, p, run.Symbol);

            if (!lotSizeByUnderlying.TryGetValue(underlying, out var lotSize))
            {
                var lot = await _lotSizeResolver.ResolveForUnderlyingAsync(underlying, cancellationToken);
                lotSize = lot.LotSize;
                lotSizeByUnderlying[underlying] = lotSize;
            }

            stats.TryGetValue(run.Id, out var s);
            stops.TryGetValue(run.Id, out var stop);
            catalogByName.TryGetValue(run.StrategyName, out var entry);

            // A strategy run is live when its runner process is. The manual
            // book has no runner and never will — it is a container for orders
            // placed by hand — so asking the registry about it always answers
            // "not running", and the console painted an open book, with open
            // positions in it, as Stopped. Its own status is the truth there.
            bool isActive = running is not null
                || (run.StrategyName == ManualOrdersController.BookStrategyName
                    && run.Status == "Running");
            var startedUtc = running?.StartedUtc ?? run.StartedUtc ?? run.CreatedUtc;
            DateTime? stoppedUtc = isActive ? null : run.CompletedUtc ?? stop?.AtUtc ?? exit?.AtUtc;

            string? stopReason = null;
            string? stoppedBy = null;
            if (!isActive)
            {
                stopReason = stop?.Reason
                             ?? exit?.Reason
                             ?? (string.IsNullOrWhiteSpace(run.LastError) ? null : run.LastError);
                stoppedBy = stop?.By;
            }

            decimal realized = s?.Realized ?? 0m;
            decimal unrealized = 0m;
            decimal? capitalUsed = null;
            if (isActive)
            {
                if (liveMarks.TryGetValue(run.Id, out var mark))
                {
                    unrealized = mark.Unrealized;
                    capitalUsed = mark.CapitalUsed;
                }
                else
                {
                    unrealized = s?.StoredUnrealized ?? 0m;
                    capitalUsed = 0m;
                }
            }

            rows.Add(new LiveRunSummaryResponse
            {
                RunId = run.Id,
                UserId = run.UserId,
                UserName = userNames.TryGetValue(run.UserId, out var userName) ? userName : null,
                StrategyId = running?.StrategyId ?? exit?.StrategyId ?? entry?.Id ?? StrategyCatalogService.StableId(run.StrategyName),
                StrategyName = entry?.Name ?? running?.Name ?? run.StrategyName,
                Category = entry?.Category ?? string.Empty,
                Underlying = underlying,
                SpotSymbol = running?.SpotSymbol ?? exit?.SpotSymbol ?? run.Symbol,
                Lots = running?.Lots ?? exit?.Lots ?? p.Lots ?? 0,
                Role = LiveRunParameters.ReadRole(run.ParametersJson),
                LotSize = lotSize,
                Risk = running?.Risk ?? exit?.Risk ?? p.Risk,
                Status = run.Status,
                IsActive = isActive,
                StartedUtc = startedUtc,
                StoppedUtc = stoppedUtc,
                StopReason = stopReason,
                StoppedBy = stoppedBy,
                DurationSeconds = DurationSeconds(startedUtc, isActive ? now : stoppedUtc ?? now),
                NetPnl = realized,
                RealizedPnl = realized,
                UnrealizedPnl = unrealized,
                Trades = s?.Closed ?? 0,
                OpenPositions = s?.Open ?? 0,
                Groups = groupsByRun.TryGetValue(run.Id, out var groups) ? groups : 0,
                ChargesPerLot = 0m,
                CapitalUsed = capitalUsed
            });
        }

        return rows;
    }

    // ------------------------------------------------------------------
    // Per-user rollup
    // ------------------------------------------------------------------

    /// <summary>
    /// One row per user who started a live run (every user when
    /// <paramref name="userId"/> is null), newest activity first. A user with
    /// no runs still gets a zero row when asked for by id, so the history
    /// header can name them.
    /// </summary>
    public async Task<List<LiveRunUserSummaryResponse>> SummarizeAsync(long? userId, CancellationToken cancellationToken)
    {
        var runs = _dbContext.SimulationRuns.AsNoTracking().Where(x => x.Mode == LivePaperMode);
        if (userId.HasValue)
        {
            long id = userId.Value;
            runs = runs.Where(x => x.UserId == id);
        }

        var counts = await runs
            .GroupBy(x => x.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Runs = g.Count(),
                LastRunUtc = g.Max(x => x.StartedUtc ?? x.CreatedUtc)
            })
            .ToListAsync(cancellationToken);

        var pnlByUser = await (
                from p in _dbContext.PaperPositions.AsNoTracking()
                join r in runs on p.SimulationRunId equals r.Id
                group p by r.UserId into g
                select new { UserId = g.Key, NetPnl = g.Sum(x => x.RealizedPnl) })
            .ToDictionaryAsync(x => x.UserId, x => x.NetPnl, cancellationToken);

        var activeByUser = _registry.List()
            .Where(x => !userId.HasValue || x.UserId == userId.Value)
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        var userIds = counts.Select(x => x.UserId).ToHashSet();
        if (userId.HasValue) userIds.Add(userId.Value);

        var userNames = await _dbContext.AppUsers.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);

        var rows = counts
            .Select(c => new LiveRunUserSummaryResponse
            {
                UserId = c.UserId,
                UserName = userNames.TryGetValue(c.UserId, out var name) ? name : null,
                Runs = c.Runs,
                Active = activeByUser.TryGetValue(c.UserId, out var active) ? active : 0,
                NetPnl = pnlByUser.TryGetValue(c.UserId, out var pnl) ? pnl : 0m,
                LastRunUtc = c.LastRunUtc
            })
            .OrderByDescending(x => x.Active)
            .ThenByDescending(x => x.LastRunUtc)
            .ThenBy(x => x.UserName)
            .ToList();

        if (userId.HasValue && rows.Count == 0)
        {
            rows.Add(new LiveRunUserSummaryResponse
            {
                UserId = userId.Value,
                UserName = userNames.TryGetValue(userId.Value, out var name) ? name : null,
                Runs = 0,
                Active = activeByUser.TryGetValue(userId.Value, out var active) ? active : 0,
                NetPnl = 0m,
                LastRunUtc = null
            });
        }

        return rows;
    }

    // ------------------------------------------------------------------
    // Per-strategy lifetime record
    // ------------------------------------------------------------------

    /// <summary>
    /// The lifetime record of one strategy: every live run of it in scope
    /// folded into counts, P&amp;L, its best and worst run, and breakdowns by
    /// underlying and by how the runs ended. <paramref name="userId"/> is
    /// already resolved by the controller — a trader always gets their own runs
    /// — and null covers every user. Null comes back only when neither the
    /// catalog nor the run history has heard of the id.
    /// </summary>
    /// <remarks>
    /// Where <see cref="ListAsync"/> pages, this deliberately does not: a record
    /// that stopped at the newest 500 runs would be a window wearing the word
    /// "lifetime". The cost is kept down instead by reading the runs narrow and
    /// leaving every aggregate that SQL can do in SQL, joined against the same
    /// query so no list of run ids is ever sent to the database.
    /// </remarks>
    public async Task<StrategyTrackRecordResponse?> SummarizeStrategyAsync(
        int strategyId,
        long? userId,
        CancellationToken cancellationToken)
    {
        var catalog = await _catalog.GetAllAsync(cancellationToken);
        var names = await StrategyNamesForIdAsync(strategyId, catalog, cancellationToken);
        var entry = catalog.FirstOrDefault(x => x.Id == strategyId);
        if (entry is null && names.Count == 0) return null;

        var record = new StrategyTrackRecordResponse
        {
            StrategyId = strategyId,
            StrategyName = entry?.Name ?? names.FirstOrDefault() ?? string.Empty,
            Scope = userId.HasValue ? StrategyTrackRecordResponse.ScopeOwn : StrategyTrackRecordResponse.ScopeAll,
            ScopeUserId = userId
        };

        // In the catalog but never started: an empty record, not a 404. "This
        // has never run" is an answer the page needs to be able to give.
        if (names.Count == 0) return record;

        var runQuery = _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Mode == LivePaperMode && names.Contains(x.StrategyName));

        if (userId.HasValue)
        {
            long scopeUserId = userId.Value;
            runQuery = runQuery.Where(x => x.UserId == scopeUserId);
        }

        // The underlying and the role of a run are derived from its parameters
        // rather than stored as columns, so the rows come back narrow and the
        // derivation happens here — the same chain ListAsync uses, so a row on
        // the history page and this rollup can never disagree about a run.
        var rows = await runQuery
            .Select(x => new
            {
                x.Id,
                x.Symbol,
                x.ParametersJson,
                x.StartedUtc,
                x.CreatedUtc,
                x.CompletedUtc,
                x.LastError
            })
            .ToListAsync(cancellationToken);

        if (rows.Count == 0) return record;

        var stats = await (
                from p in _dbContext.PaperPositions.AsNoTracking()
                join r in runQuery on p.SimulationRunId equals r.Id
                group p by p.SimulationRunId into g
                select new
                {
                    RunId = g.Key,
                    Realized = g.Sum(x => x.RealizedPnl),
                    Closed = g.Count(x => x.Status == ClosedStatus)
                })
            .ToDictionaryAsync(x => x.RunId, cancellationToken);

        var stops = await LoadStopsAsync(runQuery, cancellationToken);

        var activeRunIds = rows.Where(x => _registry.Contains(x.Id)).Select(x => x.Id).ToList();
        var liveMarks = await MarkActiveRunsAsync(activeRunIds, cancellationToken);

        var byUnderlying = new Dictionary<string, StrategyTrackRecordUnderlying>(StringComparer.OrdinalIgnoreCase);
        var byReason = new Dictionary<string, StrategyTrackRecordStopReason>(StringComparer.OrdinalIgnoreCase);
        var days = new HashSet<DateOnly>();
        var now = DateTime.UtcNow;

        foreach (var row in rows)
        {
            var p = LiveRunParameters.Parse(row.ParametersJson);
            var running = _registry.Get(row.Id);
            var exit = running is null ? _registry.GetExitByRun(row.Id) : null;
            var underlying = DeriveUnderlying(running, exit, p, row.Symbol);

            bool isActive = running is not null;
            var startedUtc = running?.StartedUtc ?? row.StartedUtc ?? row.CreatedUtc;
            stops.TryGetValue(row.Id, out var stop);
            DateTime? stoppedUtc = isActive ? null : row.CompletedUtc ?? stop?.AtUtc ?? exit?.AtUtc;

            record.Runs++;
            if (isActive) record.ActiveRuns++;
            days.Add(IstTime.DateOf(startedUtc));
            record.TotalRuntimeSeconds += DurationSeconds(startedUtc, isActive ? now : stoppedUtc ?? now);

            if (record.FirstRunUtc is null || startedUtc < record.FirstRunUtc) record.FirstRunUtc = startedUtc;
            if (record.LastRunUtc is null || startedUtc > record.LastRunUtc) record.LastRunUtc = startedUtc;

            // An alerter never opens a position, so counting it among the
            // trading runs would dilute every figure below with runs that could
            // not have made or lost a rupee. It is counted, and then left out.
            if (string.Equals(LiveRunParameters.ReadRole(row.ParametersJson), AlertsSupervisor.RoleAlerts, StringComparison.OrdinalIgnoreCase))
            {
                record.AlertRuns++;
                continue;
            }

            record.TradingRuns++;

            stats.TryGetValue(row.Id, out var s);
            decimal realized = s?.Realized ?? 0m;
            record.NetPnl += realized;
            record.Trades += s?.Closed ?? 0;

            if (isActive && liveMarks.TryGetValue(row.Id, out var mark))
                record.OpenPnl += mark.Unrealized;

            if (!byUnderlying.TryGetValue(underlying, out var u))
            {
                u = new StrategyTrackRecordUnderlying { Underlying = underlying };
                byUnderlying[underlying] = u;
            }
            u.Runs++;
            u.NetPnl += realized;

            // A run still going has not won or lost yet — its open legs can
            // still turn either way — so the win rate is counted only over the
            // ones that are finished.
            if (isActive) continue;

            record.DecidedRuns++;
            u.DecidedRuns++;

            if (realized > 0m)
            {
                record.Wins++;
                u.Wins++;
                record.GrossProfit += realized;
            }
            else if (realized < 0m)
            {
                record.Losses++;
                record.GrossLoss += realized;
            }
            else
            {
                record.Flat++;
            }

            if (record.BestRun is null || realized > record.BestRun.NetPnl)
                record.BestRun = RunRef(row.Id, underlying, startedUtc, realized);

            if (record.WorstRun is null || realized < record.WorstRun.NetPnl)
                record.WorstRun = RunRef(row.Id, underlying, startedUtc, realized);

            var reason = ShortStopReason(stop?.Reason ?? exit?.Reason ?? row.LastError) ?? UnknownStopReason;
            if (!byReason.TryGetValue(reason, out var r))
            {
                r = new StrategyTrackRecordStopReason { Reason = reason };
                byReason[reason] = r;
            }
            r.Runs++;
            r.NetPnl += realized;
        }

        if (record.DecidedRuns > 0)
        {
            record.WinRate = Math.Round(100d * record.Wins / record.DecidedRuns, 1);
            record.AveragePnlPerRun = Math.Round((record.GrossProfit + record.GrossLoss) / record.DecidedRuns, 2);
        }

        if (record.Runs > 0)
            record.AverageRuntimeSeconds = record.TotalRuntimeSeconds / record.Runs;

        record.TradingDays = days.Count;
        record.ByUnderlying = byUnderlying.Values
            .OrderByDescending(x => x.NetPnl)
            .ThenBy(x => x.Underlying, StringComparer.Ordinal)
            .ToList();
        record.StopReasons = byReason.Values
            .OrderByDescending(x => x.Runs)
            .ThenBy(x => x.Reason, StringComparer.Ordinal)
            .ToList();

        return record;
    }

    private static StrategyTrackRecordRunRef RunRef(long runId, string underlying, DateTime startedUtc, decimal netPnl)
        => new() { RunId = runId, Underlying = underlying, StartedUtc = startedUtc, NetPnl = netPnl };

    // ------------------------------------------------------------------
    // Pieces
    // ------------------------------------------------------------------

    /// <summary>
    /// The catalog id, or "any" / empty for no filter. Case-insensitive; an
    /// unknown status is passed through as typed (it simply matches no row).
    /// </summary>
    public static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var text = status.Trim();
        if (string.Equals(text, LiveRunHistoryFilter.AnyStatus, StringComparison.OrdinalIgnoreCase)) return null;
        return KnownStatuses.FirstOrDefault(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase)) ?? text;
    }

    /// <summary>
    /// The underlying a run row reads as, upper-cased: the registry entry while
    /// active, else the remembered exit, else the "underlying" key of its
    /// parameters, else its (spot) symbol. The same chain serves the filter and
    /// the row so the two can never disagree.
    /// </summary>
    private string DeriveUnderlying(long runId, string? symbol, LiveRunParameters p)
    {
        var running = _registry.Get(runId);
        var exit = running is null ? _registry.GetExitByRun(runId) : null;
        return DeriveUnderlying(running, exit, p, symbol);
    }

    private static string DeriveUnderlying(RunningStrategy? running, LastExit? exit, LiveRunParameters p, string? symbol)
    {
        var underlying = running?.Underlying
                         ?? exit?.Underlying
                         ?? p.Underlying
                         ?? UnderlyingCatalog.UnderlyingForSpot(symbol)
                         ?? UnderlyingCatalog.InferUnderlying(symbol);
        return underlying.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// The strategy names a catalog id refers to: the catalog entry with that
    /// id, plus any name in the run history whose stable id matches (a strategy
    /// that has since left the catalog still has its runs).
    /// </summary>
    private async Task<List<string>> StrategyNamesForIdAsync(
        int strategyId,
        IReadOnlyList<StrategyCatalogEntry> catalog,
        CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Where(x => x.Id == strategyId))
        {
            names.Add(entry.Name);
        }

        var historyNames = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.Mode == LivePaperMode)
            .Select(x => x.StrategyName)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var name in historyNames)
        {
            if (StrategyCatalogService.StableId(name) == strategyId
                || catalog.Any(c => c.Id == strategyId && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                names.Add(name);
            }
        }

        return names.ToList();
    }

    private sealed record StopInfo(string? Reason, string? By, DateTime AtUtc);

    private sealed record StopSignalRow(long RunId, DateTime TimestampUtc, string? MetadataJson);

    /// <summary>
    /// The short form of a RUN_STOPPED reason, so the reasons that mean the same
    /// thing land in the same bucket. The API writes them as
    /// "&lt;what&gt;: &lt;numbers&gt;", "&lt;what&gt; (&lt;detail&gt;)" or
    /// "&lt;what&gt;; &lt;detail&gt;", and it is the part before the detail that
    /// repeats:
    /// <code>
    /// "Stop loss hit: P&amp;L −₹5,120 ≤ −₹5,000" → "Stop loss hit"
    /// "Market closed (15:30 IST)"               → "Market closed"
    /// "Runner exited (code 1)"                  → "Runner exited"
    /// </code>
    /// The console's <c>shortStopReason()</c> cuts at the same separators, so a
    /// badge on the history page and a row in the record read alike.
    /// </summary>
    public static string? ShortStopReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;

        var text = reason.Trim();
        int cut = text.Length;
        foreach (var separator in StopReasonSeparators)
        {
            int i = text.IndexOf(separator, StringComparison.Ordinal);
            if (i > 0 && i < cut) cut = i;
        }

        var head = text[..cut].Trim();
        return head.Length == 0 ? null : head;
    }

    /// <summary>The last RUN_STOPPED signal per run: { reason, by } and when.</summary>
    private async Task<Dictionary<long, StopInfo>> LoadStopsAsync(List<long> runIds, CancellationToken cancellationToken)
    {
        const string stoppedType = StrategyRunControl.RunStoppedSignalType;

        var signals = await _dbContext.SimulationSignals.AsNoTracking()
            .Where(x => runIds.Contains(x.SimulationRunId) && x.SignalType == stoppedType)
            .OrderBy(x => x.Id)
            .Select(x => new { x.SimulationRunId, x.TimestampUtc, x.MetadataJson })
            .ToListAsync(cancellationToken);

        return FoldStops(signals.Select(x => new StopSignalRow(x.SimulationRunId, x.TimestampUtc, x.MetadataJson)));
    }

    /// <summary>
    /// The same thing over a whole query of runs rather than one page of ids —
    /// the lifetime record's form, joined in the database so a strategy with
    /// thousands of runs never turns into an IN clause with thousands of terms.
    /// </summary>
    private async Task<Dictionary<long, StopInfo>> LoadStopsAsync(IQueryable<SimulationRun> runs, CancellationToken cancellationToken)
    {
        const string stoppedType = StrategyRunControl.RunStoppedSignalType;

        var signals = await (
                from s in _dbContext.SimulationSignals.AsNoTracking()
                join r in runs on s.SimulationRunId equals r.Id
                where s.SignalType == stoppedType
                orderby s.Id
                select new { s.SimulationRunId, s.TimestampUtc, s.MetadataJson })
            .ToListAsync(cancellationToken);

        return FoldStops(signals.Select(x => new StopSignalRow(x.SimulationRunId, x.TimestampUtc, x.MetadataJson)));
    }

    /// <summary>
    /// Signals ordered by id folded to one entry per run — the last one wins,
    /// because a run stopped twice (a stop that raced a market close, say) is
    /// described by how it actually ended.
    /// </summary>
    private static Dictionary<long, StopInfo> FoldStops(IEnumerable<StopSignalRow> signals)
    {
        var result = new Dictionary<long, StopInfo>();
        foreach (var s in signals)
        {
            var reason = SignalMetadata.ReadReason(s.MetadataJson);
            var by = SignalMetadata.ReadString(s.MetadataJson, "by");
            result[s.RunId] = new StopInfo(
                string.IsNullOrWhiteSpace(reason) ? null : reason,
                string.IsNullOrWhiteSpace(by) ? null : by,
                s.TimestampUtc);
        }
        return result;
    }

    private sealed record LiveMark(decimal Unrealized, decimal CapitalUsed);

    /// <summary>
    /// Unrealized P&amp;L and capital used of the active runs' open legs, marked
    /// against the latest live quote exactly as the live view does (stored mark
    /// when no quote is known). Read-only: nothing is written back.
    /// </summary>
    private async Task<Dictionary<long, LiveMark>> MarkActiveRunsAsync(List<long> activeRunIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, LiveMark>();
        if (activeRunIds.Count == 0) return result;

        var open = await _dbContext.PaperPositions.AsNoTracking()
            .Where(p => activeRunIds.Contains(p.SimulationRunId) && p.Status == OpenStatus)
            .Select(p => new { p.SimulationRunId, p.Symbol, p.Direction, p.Quantity, p.AveragePrice, p.UnrealizedPnl })
            .ToListAsync(cancellationToken);

        foreach (var runId in activeRunIds)
        {
            result[runId] = new LiveMark(0m, 0m);
        }

        if (open.Count == 0) return result;

        var symbols = open.Select(x => x.Symbol).Distinct(StringComparer.Ordinal).ToList();

        var quotes = await _dbContext.LiveQuotesLatest.AsNoTracking()
            .Where(q => symbols.Contains(q.Symbol))
            .Select(q => new { q.Symbol, q.LastTradedPrice })
            .ToListAsync(cancellationToken);
        var ltpBySymbol = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var q in quotes)
        {
            if (q.LastTradedPrice.HasValue) ltpBySymbol.TryAdd(q.Symbol, q.LastTradedPrice.Value);
        }

        var lotSizes = await _lotSizeResolver.ResolveManyAsync(symbols, cancellationToken);

        foreach (var pos in open)
        {
            int lotSize = lotSizes.TryGetValue(pos.Symbol, out var info) && info.LotSize > 0 ? info.LotSize : 1;
            // The same function the fills use. Written out by hand here, this
            // was the second copy of the number that says how much a run made.
            decimal unrealized = ltpBySymbol.TryGetValue(pos.Symbol, out var ltp)
                ? PaperPnl.Unrealized(pos.Direction, pos.AveragePrice, ltp, pos.Quantity, lotSize)
                : pos.UnrealizedPnl;

            decimal used = PaperTradingService.UsedCapitalOf(pos.Direction, pos.Symbol, pos.AveragePrice, pos.Quantity, lotSize);

            var current = result[pos.SimulationRunId];
            result[pos.SimulationRunId] = new LiveMark(current.Unrealized + unrealized, current.CapitalUsed + used);
        }

        return result;
    }

    private static long DurationSeconds(DateTime startedUtc, DateTime endUtc)
    {
        var seconds = (long)Math.Floor((endUtc - startedUtc).TotalSeconds);
        return seconds < 0 ? 0 : seconds;
    }
}
