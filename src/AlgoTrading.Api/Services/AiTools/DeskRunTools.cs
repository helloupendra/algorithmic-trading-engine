using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Contracts.Simulator;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

/// <summary>
/// <c>get_runs</c>: the day's live paper runs with their net P&amp;L, as the
/// Desk's "Runs · net P&amp;L" grid shows them.
/// </summary>
/// <remarks>
/// Net is realized − charges, plus the open legs marked to the latest quote
/// while a run is active: the Desk's figure (web/src/lib/desk.ts runFigures),
/// so the model and the page agree. For today it also takes runs started on an
/// earlier day that are still running (an open manual book, a carried run),
/// which a date filter alone would miss. Alert-only runs are left out, as the
/// Desk leaves them out.
/// </remarks>
public sealed class RunsTool(LiveRunHistoryBuilder history, TimeProvider? time = null) : IAiTool
{
    private const int MaxRuns = 40;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.Runs;

    public string Description =>
        "The desk's live paper runs for one IST day (default today, including runs still running from earlier days): " +
        "strategy, account, underlying, status, start and stop times, lots, and P&L in rupees (net = realized − charges " +
        "+ open legs marked to market while active). Use it first for any question about how runs or accounts did.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("date", AiToolSchema.Text("IST day as yyyy-MM-dd, or 'today' / 'yesterday'. Default today."), false),
        ("account", AiToolSchema.Text("Only this account's runs (user name)."), false),
        ("strategy", AiToolSchema.Text("Only runs whose strategy name contains this."), false),
        ("status", AiToolSchema.OneOf("Which runs. Default any.", "any", "running", "stopped"), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        var date = args.Date("date", today) ?? today;
        if (date > today) throw new AiToolArgumentException("date is in the future.");
        string? account = args.String("account");
        string? strategy = args.String("strategy");
        string? status = args.String("status", 10)?.ToLowerInvariant() switch
        {
            null or "any" => null,
            "running" => StrategyRunControl.RunStatusRunning,
            "stopped" => "Stopped",
            var other => throw new AiToolArgumentException($"status is any, running or stopped, not {other}."),
        };

        var runs = await history.ListAsync(new LiveRunHistoryFilter(null, null, null, status, date, date, LiveRunHistoryFilter.MaxTake, 0), cancellationToken);
        if (date == today && status != "Stopped")
        {
            var stillRunning = await history.ListAsync(
                new LiveRunHistoryFilter(null, null, null, StrategyRunControl.RunStatusRunning, null, null, LiveRunHistoryFilter.MaxTake, 0), cancellationToken);
            runs = runs.Concat(stillRunning).GroupBy(r => r.RunId).Select(g => g.First()).ToList();
        }

        var rows = runs
            .Where(r => r.Role != "alerts")
            .Where(r => account is null || string.Equals(r.UserName, account, StringComparison.OrdinalIgnoreCase))
            .Where(r => strategy is null || r.StrategyName.Contains(strategy, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.StartedUtc)
            .ToList();

        // Past the cap, the runs that moved most: the ones a question is about.
        var listed = rows.Count <= MaxRuns ? rows : rows.OrderByDescending(r => Math.Abs(Net(r))).Take(MaxRuns).OrderBy(r => r.StartedUtc).ToList();
        var shown = listed.Select(r => new
        {
            runId = r.RunId,
            strategy = r.StrategyName,
            account = r.UserName,
            underlying = r.Underlying,
            status = r.Status,
            active = r.IsActive,
            started = Ist(r.StartedUtc),
            stopped = Ist(r.StoppedUtc),
            stopReason = Text(r.StopReason, 200),
            lots = r.Lots,
            lotSize = r.LotSize,
            netPnl = Rs(Net(r)),
            realizedPnl = Rs(r.GrossPnl),
            unrealizedPnl = r.IsActive ? Rs(r.UnrealizedPnl) : 0m,
            charges = Rs(r.Charges),
            closedTrades = r.Trades,
            openLegs = r.OpenPositions,
        }).ToList();

        decimal net = rows.Sum(Net);
        var data = new
        {
            date = date.ToString("yyyy-MM-dd"),
            runs = shown,
            omitted = rows.Count > MaxRuns ? $"{rows.Count - MaxRuns} runs with the smallest P&L are left out; totals cover all." : null,
            totals = new
            {
                runs = rows.Count,
                active = rows.Count(r => r.IsActive),
                netPnl = Rs(net),
                winners = rows.Count(r => Net(r) > 0),
                losers = rows.Count(r => Net(r) < 0),
                byAccount = rows.GroupBy(r => r.UserName ?? "?")
                    .Select(g => new { account = g.Key, runs = g.Count(), netPnl = Rs(g.Sum(Net)) })
                    .OrderBy(g => g.account)
                    .ToList(),
            },
        };

        return new AiToolOutput(data, now, rows.Count, $"{rows.Count} runs on {date:dd MMM}, net ₹{Rs(net):N2}");
    }

    private static decimal Net(LiveRunSummaryResponse r) => r.GrossPnl - r.Charges + (r.IsActive ? r.UnrealizedPnl : 0m);
}

/// <summary>
/// <c>get_run</c>: one run — its settings and P&amp;L, and either a summary of
/// what happened (the default) or one section (legs, orders, signals) in a
/// time window.
/// </summary>
/// <remarks>
/// <para>
/// A summary first, because runs differ by orders of magnitude: most have a
/// dozen legs, and a busy one on 30 Sep had 344 legs, 688 orders and 9,636
/// signals, which no model should read whole. The summary counts everything,
/// shows P&amp;L by hour of the day, the best and worst legs, the open legs
/// and the last signals; a small run gets its full lists in it too. For the
/// rest, the model asks for a section and a window.
/// </para>
/// <para>
/// Read-only on purpose. The run page's own path marks open legs through the
/// paper trading service, which writes; here the legs are read with
/// AsNoTracking and priced by the same <see cref="PositionViewBuilder"/> the
/// Positions page uses, and the figures come from <see cref="RunPnl"/>, the
/// Desk's source.
/// </para>
/// </remarks>
public sealed class RunTool(TradingDbContext db, PositionViewBuilder views, RunPnl pnl, TimeProvider? time = null) : IAiTool
{
    /// <summary>A run this small is listed whole in its summary.</summary>
    private const int SmallLegs = 30, SmallOrders = 60, SmallSignals = 60;

    private const int DefaultLimit = 40, MaxLimit = 80;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.Run;

    public string Description =>
        "One run by runId (from get_runs). Always: strategy, account, underlying, lots, risk rules, status, stop " +
        "reason, and P&L (realized, unrealized, charges, net). Orders say how each paper fill was priced and flag a " +
        "stale quote. section=summary (default): counts of legs, orders and " +
        "signals, realized P&L by IST hour, the 5 best and 5 worst legs, the open legs and the last signals with " +
        "reasons; a small run's full lists too. section=legs|orders|signals: that list, optionally between from and to " +
        "(HH:mm IST on the run's day), up to limit rows. Use it to explain why a run made or lost money.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("runId", AiToolSchema.Integer("The run's id."), true),
        ("section", AiToolSchema.OneOf("What to list. Default summary.", "summary", "legs", "orders", "signals"), false),
        ("from", AiToolSchema.Text("Start of the window, HH:mm IST, for a section."), false),
        ("to", AiToolSchema.Text("End of the window, HH:mm IST, for a section."), false),
        ("limit", AiToolSchema.Integer("Rows for a section, 1 to 80. Default 40."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        long runId = args.Long("runId", 1) ?? throw new AiToolArgumentException("runId is required; get_runs lists them.");
        string section = args.String("section", 10)?.ToLowerInvariant() ?? "summary";
        if (section is not ("summary" or "legs" or "orders" or "signals")) throw new AiToolArgumentException("section is summary, legs, orders or signals.");
        int limit = args.Int("limit", 1, MaxLimit) ?? DefaultLimit;
        var now = _time.GetUtcNow().UtcDateTime;

        var run = await db.SimulationRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId && r.Mode == StrategyRunControl.LivePaperMode, cancellationToken)
            ?? throw new AiToolArgumentException($"There is no live run {runId}; get_runs lists them.");

        var day = IstTime.DateOf(run.StartedUtc ?? run.CreatedUtc);
        DateTime? from = Clock(args.String("from", 5), day, "from");
        DateTime? to = Clock(args.String("to", 5), day, "to");

        bool active = run.Status is StrategyRunControl.RunStatusRunning or "Stopping";
        var parameters = LiveRunParameters.Parse(run.ParametersJson);
        string? account = await db.AppUsers.AsNoTracking().Where(u => u.Id == run.UserId).Select(u => u.UserName).FirstOrDefaultAsync(cancellationToken);
        var figures = (await pnl.FiguresAsync([runId], active ? new HashSet<long> { runId } : new HashSet<long>(), cancellationToken))
            .GetValueOrDefault(runId);

        var positions = db.PaperPositions.AsNoTracking().Where(p => p.SimulationRunId == runId);
        var orders = db.PaperOrders.AsNoTracking().Where(o => o.SimulationRunId == runId);
        var signals = db.SimulationSignals.AsNoTracking().Where(s => s.SimulationRunId == runId);

        var legCounts = await positions.GroupBy(p => p.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(cancellationToken);
        var orderCounts = await orders.GroupBy(o => o.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(cancellationToken);
        var signalCounts = await signals.GroupBy(s => s.SignalType).Select(g => new { type = g.Key, count = g.Count() }).ToListAsync(cancellationToken);
        int legTotal = legCounts.Sum(c => c.count), orderTotal = orderCounts.Sum(c => c.count), signalTotal = signalCounts.Sum(c => c.count);

        string? stopReason = await signals.Where(s => s.SignalType == "RUN_STOPPED").OrderByDescending(s => s.TimestampUtc)
            .Select(s => s.MetadataJson).FirstOrDefaultAsync(cancellationToken) is { } stopMeta ? Reason(stopMeta) : null;
        stopReason ??= Text(run.LastError, 300);

        var head = new
        {
            runId,
            strategy = run.StrategyName,
            account,
            underlying = parameters.Underlying ?? run.Symbol,
            lots = parameters.Lots,
            status = run.Status,
            active,
            started = Ist(run.StartedUtc ?? run.CreatedUtc),
            stopped = Ist(run.CompletedUtc),
            stopReason,
            riskRules = parameters.Risk,
        };
        var money = figures is null ? null : new
        {
            realized = Rs(figures.Realized),
            unrealized = Rs(figures.Unrealized),
            charges = Rs(figures.Charges),
            net = Rs(figures.Net),
        };
        var counts = new
        {
            legs = legTotal,
            legsByStatus = legCounts,
            orders = orderTotal,
            ordersByStatus = orderCounts,
            signals = signalTotal,
            signalsByType = signalCounts,
        };

        object data;
        int rows;
        if (section == "summary")
        {
            bool small = legTotal <= SmallLegs && orderTotal <= SmallOrders && signalTotal <= SmallSignals;
            var closed = await positions.Where(p => p.Status != "Open" && p.ClosedUtc != null)
                .Select(p => new { p.Id, p.ClosedUtc, p.RealizedPnl })
                .ToListAsync(cancellationToken);

            var byHour = closed
                .GroupBy(p => IstTime.ToIst(DateTime.SpecifyKind(p.ClosedUtc!.Value, DateTimeKind.Utc)).Hour)
                .OrderBy(g => g.Key)
                .Select(g => new { hour = $"{g.Key:00}:00", closedLegs = g.Count(), realized = Rs(g.Sum(p => p.RealizedPnl)) })
                .ToList();

            var bestIds = closed.OrderByDescending(p => p.RealizedPnl).Take(5).Where(p => p.RealizedPnl > 0).Select(p => p.Id);
            var worstIds = closed.OrderBy(p => p.RealizedPnl).Take(5).Where(p => p.RealizedPnl < 0).Select(p => p.Id);
            var openIds = await positions.Where(p => p.Status == "Open").OrderBy(p => p.OpenedUtc).Take(20).Select(p => p.Id).ToListAsync(cancellationToken);

            var pick = small
                ? await positions.OrderBy(p => p.OpenedUtc).Select(p => p.Id).ToListAsync(cancellationToken)
                : bestIds.Concat(worstIds).Concat(openIds).Distinct().ToList();
            var legs = await LegsAsync(positions.Where(p => pick.Contains(p.Id)), active, cancellationToken);
            var legById = legs.ToDictionary(l => l.Id);

            data = new
            {
                run = head,
                pnl = money,
                counts,
                realizedByHour = byHour,
                legs = small ? legs.Select(l => l.Row).ToList() : null,
                bestLegs = small ? null : bestIds.Where(legById.ContainsKey).Select(id => legById[id].Row).ToList(),
                worstLegs = small ? null : worstIds.Where(legById.ContainsKey).Select(id => legById[id].Row).ToList(),
                openLegs = small ? null : openIds.Where(legById.ContainsKey).Select(id => legById[id].Row).ToList(),
                orders = small ? await OrdersAsync(orders, SmallOrders, cancellationToken) : null,
                signals = small
                    ? await SignalsAsync(signals.OrderBy(s => s.TimestampUtc), SmallSignals, cancellationToken)
                    : await SignalsAsync(signals.OrderByDescending(s => s.TimestampUtc), 10, cancellationToken),
                signalsNote = small ? null : "The last 10 signals, newest first.",
                more = small ? null : "For the full lists call get_run with section legs, orders or signals, and from/to (HH:mm IST) to narrow the window.",
            };
            rows = legs.Count;
        }
        else
        {
            object list;
            int total;
            switch (section)
            {
                case "legs":
                    var window = positions.Where(p => (from == null || (p.ClosedUtc ?? p.OpenedUtc) >= from) && (to == null || p.OpenedUtc <= to));
                    total = await window.CountAsync(cancellationToken);
                    var legs = await LegsAsync(window.OrderBy(p => p.OpenedUtc).Take(limit), active, cancellationToken);
                    list = legs.Select(l => l.Row).ToList();
                    break;
                case "orders":
                    var inWindow = orders.Where(o => (from == null || o.CreatedUtc >= from) && (to == null || o.CreatedUtc <= to));
                    total = await inWindow.CountAsync(cancellationToken);
                    list = await OrdersAsync(inWindow, limit, cancellationToken);
                    break;
                default:
                    var picked = signals.Where(s => (from == null || s.TimestampUtc >= from) && (to == null || s.TimestampUtc <= to));
                    total = await picked.CountAsync(cancellationToken);
                    list = await SignalsAsync(picked.OrderBy(s => s.TimestampUtc), limit, cancellationToken);
                    break;
            }

            data = new
            {
                run = head,
                pnl = money,
                counts,
                section,
                window = from is null && to is null ? null : new { from = Ist(from), to = Ist(to) },
                rows = list,
                inWindow = total,
                more = total > limit ? $"{total - limit} more in this window: narrow from/to." : null,
            };
            rows = Math.Min(total, limit);
        }

        string net = figures is null ? "no P&L" : $"net ₹{Rs(figures.Net):N2}";
        return new AiToolOutput(data, now, rows, $"Run {runId} ({run.StrategyName}) {section}: {legTotal} legs, {orderTotal} orders, {net}");
    }

    /// <summary>"HH:mm" on the run's IST day, as UTC; null when not given.</summary>
    private static DateTime? Clock(string? text, DateOnly day, string name)
    {
        if (text is null) return null;
        if (!TimeOnly.TryParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at))
        {
            throw new AiToolArgumentException($"{name} must be a time as HH:mm (IST).");
        }

        return IstTime.FromIst(day.ToDateTime(at));
    }

    private sealed record LegRow(long Id, object Row);

    /// <summary>Legs priced as the Positions page prices them: contract labels, lots × lot size, marks for open legs.</summary>
    private async Task<List<LegRow>> LegsAsync(IQueryable<Domain.Entities.PaperPosition> query, bool active, CancellationToken cancellationToken)
    {
        var positions = await query
            .Select(p => new PaperPositionResponse
            {
                Id = p.Id,
                SimulationRunId = p.SimulationRunId,
                StrategyName = p.StrategyName,
                GroupId = p.GroupId,
                Symbol = p.Symbol,
                Direction = p.Direction,
                Quantity = p.Quantity,
                AveragePrice = p.AveragePrice,
                LastMarkPrice = p.LastMarkPrice,
                RealizedPnl = p.RealizedPnl,
                UnrealizedPnl = p.UnrealizedPnl,
                Status = p.Status,
                OpenedUtc = p.OpenedUtc,
                ClosedUtc = p.ClosedUtc,
                StopLossPrice = p.StopLossPrice,
                TargetPrice = p.TargetPrice,
                CarryForward = p.CarryForward,
                CarriedFromPositionId = p.CarriedFromPositionId,
                UpdatedUtc = p.UpdatedUtc,
            })
            .ToListAsync(cancellationToken);
        if (positions.Count == 0) return [];

        var built = await views.BuildAsync<LivePositionResponse>(positions, useLiveQuotes: active, spotSymbol: null, cancellationToken);
        var byId = positions.ToDictionary(p => p.Id);
        return built.Positions.Select(v =>
        {
            var p = byId[v.Id];
            bool open = v.Status == "Open";
            decimal legPnl = open && v.Ltp is decimal mark ? PaperPnl.Unrealized(p.Direction, p.AveragePrice, mark, v.Lots, v.LotSize) : v.Pnl;
            return new LegRow(v.Id, new
            {
                group = v.GroupId,
                contract = v.Contract.Label,
                side = v.Side,
                lots = v.Lots,
                lotSize = v.LotSize,
                status = v.Status,
                entry = Rs(v.EntryPrice),
                exit = Rs(v.ExitPrice),
                last = open ? Rs(v.Ltp) : null,
                pnl = Rs(legPnl),
                stopLoss = Rs(p.StopLossPrice),
                target = Rs(p.TargetPrice),
                opened = Ist(p.OpenedUtc),
                closed = Ist(p.ClosedUtc),
                carriedForward = v.CarryForward ? true : (bool?)null,
            });
        }).ToList();
    }

    private static async Task<List<object>> OrdersAsync(IQueryable<Domain.Entities.PaperOrder> query, int limit, CancellationToken cancellationToken)
    {
        var rows = await query.OrderBy(o => o.CreatedUtc).Take(limit)
            .Select(o => new { o.CreatedUtc, o.FilledUtc, o.Side, o.Symbol, o.Quantity, o.Status, o.RequestedPrice, o.FillPrice, o.GroupId, o.MetadataJson })
            .ToListAsync(cancellationToken);
        return rows.Select(o =>
        {
            // How the paper fill was priced, as the Orders page shows it: a stale quote is a fill to distrust.
            var source = PaperFill.ReadSource(o.MetadataJson);
            return (object)new
            {
                at = Ist(o.FilledUtc ?? o.CreatedUtc),
                side = o.Side,
                symbol = o.Symbol,
                lots = o.Quantity,
                status = o.Status,
                requested = Rs(o.RequestedPrice),
                fill = Rs(o.FillPrice),
                group = o.GroupId,
                priceRule = source?.Rule,
                quoteAgeSeconds = source?.QuoteAgeSeconds,
                staleQuote = source?.StaleQuote == true ? true : (bool?)null,
            };
        }).ToList();
    }

    private static async Task<List<object>> SignalsAsync(IQueryable<Domain.Entities.SimulationSignal> ordered, int limit, CancellationToken cancellationToken)
    {
        var rows = await ordered.Take(limit)
            .Select(s => new { s.TimestampUtc, s.SignalType, s.Symbol, s.Price, s.GroupId, s.MetadataJson })
            .ToListAsync(cancellationToken);
        return rows.Select(s => (object)new
        {
            at = Ist(s.TimestampUtc),
            type = s.SignalType,
            symbol = string.IsNullOrEmpty(s.Symbol) ? null : s.Symbol,
            price = Rs(s.Price),
            group = string.IsNullOrEmpty(s.GroupId) ? null : s.GroupId,
            reason = Reason(s.MetadataJson),
        }).ToList();
    }

    /// <summary>The "reason" a signal carries in its metadata, masked; null when it has none.</summary>
    private static string? Reason(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            return JsonNode.Parse(metadataJson) is JsonObject meta && meta["reason"] is JsonValue v && v.TryGetValue(out string? reason)
                ? Text(reason, 200)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary><c>get_open_positions</c>: every open leg across runs and manual books, marked to the latest quote.</summary>
public sealed class OpenPositionsTool(OpenPositionsBuilder positions, TimeProvider? time = null) : IAiTool
{
    private const int MaxLegs = 150;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.OpenPositions;

    public string Description =>
        "Every open leg now, across strategy runs and manual books: account, strategy, contract, side, lots, entry, " +
        "mark price and its age in seconds, unrealized P&L, stop-loss and target, and whether it carries overnight. " +
        "A leg with no mark has no unrealized P&L.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("account", AiToolSchema.Text("Only this account's legs (user name)."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string? account = args.String("account");
        var now = _time.GetUtcNow().UtcDateTime;
        var book = await positions.BuildAsync(null, now, cancellationToken);

        var rows = book.Positions
            .Where(p => account is null || string.Equals(p.UserName, account, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var data = new
        {
            legs = rows.Take(MaxLegs).Select(p => new
            {
                runId = p.RunId,
                strategy = p.StrategyName,
                manualBook = p.IsManualBook ? true : (bool?)null,
                account = p.UserName,
                contract = p.Label,
                side = p.Direction,
                lots = p.Lots,
                lotSize = p.LotSize,
                entry = Rs(p.EntryPrice),
                mark = Rs(p.MarkPrice),
                markAgeSeconds = p.MarkAgeSeconds,
                unrealizedPnl = Rs(p.UnrealizedPnl),
                stopLoss = Rs(p.StopLossPrice),
                target = Rs(p.TargetPrice),
                carriesOvernight = p.CarryForward ? true : (bool?)null,
                opened = Ist(p.OpenedUtc),
            }).ToList(),
            omitted = rows.Count > MaxLegs ? rows.Count - MaxLegs : (int?)null,
            totals = new
            {
                legs = rows.Count,
                unrealizedPnl = Rs(rows.Sum(p => p.UnrealizedPnl ?? 0m)),
                unmarked = rows.Count(p => p.UnrealizedPnl is null),
            },
        };

        return new AiToolOutput(data, book.AsOfUtc, rows.Count, $"{rows.Count} open legs, unrealized ₹{Rs(rows.Sum(p => p.UnrealizedPnl ?? 0m)):N2}");
    }
}
