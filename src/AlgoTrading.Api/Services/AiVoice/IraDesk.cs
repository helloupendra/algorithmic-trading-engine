using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.Today;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiVoice;

/// <summary>Something Ira tells the owners first: what it was, when it happened, and the words.</summary>
/// <param name="Kind"><c>ai-trader</c>, <c>risk</c>, <c>incident</c> or <c>compliance</c>.</param>
/// <param name="Key">The record it came from: <c>decision:118</c>, <c>signal:40512</c>, <c>incident:206</c>, <c>compliance:3</c>.</param>
public sealed record IraEvent(string Kind, string Key, DateTime AtUtc, string Text);

/// <summary>The morning briefing's facts; a null part is missing and said so.</summary>
/// <param name="Technical">Each index the Technical Analyst graded, "bullish B"; null with no report today.</param>
/// <param name="Options">Each index the Options/OI Analyst read, its stance; null with no report today.</param>
/// <param name="Plan">The morning plan's runs per account, started of planned; null when the plan could not be read.</param>
public sealed record IraMorning(IraMacro? Macro, IReadOnlyList<IraIndexView>? Technical, IReadOnlyList<IraIndexView>? Options,
    IReadOnlyList<IraPlanAccount>? Plan);

/// <summary>The Macro Analyst's bias band and its checked size modifier, as its report states them ("0.80").</summary>
public sealed record IraMacro(string Band, string Size);

public sealed record IraIndexView(string Index, string View);

public sealed record IraPlanAccount(string Account, int Started, int Planned);

/// <summary>The close briefing's facts, as Today states them (<see cref="TodayBuilder"/>).</summary>
/// <param name="Accounts">Each account's day net after charges, worst first.</param>
/// <param name="Runs">How many live runs the day had.</param>
/// <param name="Trader">The AI Trader's day; null when it was off and did not look.</param>
public sealed record IraClose(IReadOnlyList<IraAccountNet> Accounts, IraRunNet? Best, IraRunNet? Worst, int Runs, IraTraderDay? Trader);

public sealed record IraAccountNet(string Account, decimal Net);

public sealed record IraRunNet(long RunId, string Strategy, string Underlying, string Account, decimal Net);

/// <param name="Mode"><c>live</c> or <c>shadow</c>.</param>
/// <param name="Placed">Decisions it carried out in its own account (live), else its shadow trades.</param>
/// <param name="Positions">Positions in its live book today (live), else its shadow trades.</param>
/// <param name="Net">That book's net after charges.</param>
public sealed record IraTraderDay(string Mode, int Placed, int Positions, decimal Net);

/// <summary>What Ira reads of the desk: the events since its marks, and the two briefings' facts.</summary>
public interface IIraDesk
{
    /// <summary>The marks of a desk that never had any: every source at its newest row, so nothing already there is told.</summary>
    Task<IraMarks> StartAsync(CancellationToken cancellationToken);

    /// <summary>The events after <paramref name="marks"/> to tell now, oldest first; moves the marks past what was dealt with.</summary>
    Task<IReadOnlyList<IraEvent>> EventsAsync(IraMarks marks, DateTime nowUtc, CancellationToken cancellationToken);

    Task<IraMorning> MorningAsync(DateTime nowUtc, CancellationToken cancellationToken);

    Task<IraClose> CloseAsync(DateTime nowUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Ira's eyes on the desk (<see cref="IraAlertService"/>): the AI Trader's live decisions, the risk guard's stops and
/// group closes, Sentinel's incidents, the circulars that apply to the desk, and the facts of the two briefings, all
/// from the desk's own records.
/// </summary>
/// <remarks>
/// <para>
/// <b>What counts.</b> An AI Trader decision in <c>live</c> mode (not shadow, not a replay) that it carried out
/// (<see cref="AiTraderDecision.Executed"/>): a buy, an exit, a strategy started or stopped. A live strategy run (not a
/// recap, not a replay or backtest) whose overall rules stopped it (its <c>RUN_STOPPED</c> signal by
/// <c>risk-guard</c>) or whose group rules closed a group (a <c>CLOSE_GROUP</c> by <c>risk-guard</c> whose reason is a
/// group rule's); a leg's own stop is not told. A Sentinel incident at high or critical when it is first seen. A
/// circular's compliance alert (<see cref="ComplianceFollowUp"/> with an alert), once, when it went.
/// </para>
/// <para>
/// <b>The marks.</b> Each source keeps an <see cref="IraCursor"/>: every row at or below <c>through</c> is dealt with,
/// and the rows above it already told are listed. A row is passed only once it was in the table a pass earlier
/// (<c>seen</c>), so one whose insert was still committing when a higher one was read is not stepped over; and a live
/// buy or start still waiting for the Validator and the Risk Memo holds the mark until it is placed or refused. Nothing
/// first seen more than <see cref="IraAlertService.Freshness"/> ago is told: after a restart, the last half hour is told
/// and older history is not.
/// </para>
/// </remarks>
public sealed class IraDesk(TradingDbContext db, RunPnl pnl, TodayBuilder? today = null, DeskPlanBuilder? plan = null) : IIraDesk
{
    /// <summary>Rows read from a source in one pass at most; the rest wait for the next minute.</summary>
    public const int Batch = 200;

    public const string AiTraderKind = "ai-trader";
    public const string RiskKind = "risk";
    public const string IncidentKind = "incident";
    public const string ComplianceKind = "compliance";

    private const string CloseGroup = "CLOSE_GROUP";

    /// <summary>How the risk guard signs what it did: <c>{"reason":"…","by":"risk-guard"}</c>.</summary>
    private static readonly string RiskGuardMark = $"\"by\":\"{StrategyRiskGuardService.By}\"";

    public async Task<IraMarks> StartAsync(CancellationToken cancellationToken)
    {
        long decisions = await db.AiTraderDecisions.MaxAsync(d => (long?)d.Id, cancellationToken) ?? 0;
        long signals = await db.SimulationSignals.MaxAsync(s => (long?)s.Id, cancellationToken) ?? 0;
        long incidents = await db.Incidents.MaxAsync(i => (long?)i.Id, cancellationToken) ?? 0;
        long compliance = await db.ComplianceFollowUps.MaxAsync(f => (long?)f.Id, cancellationToken) ?? 0;
        return new IraMarks
        {
            Decisions = IraCursor.At(decisions),
            Signals = IraCursor.At(signals),
            Incidents = IraCursor.At(incidents),
            Compliance = IraCursor.At(compliance),
        };
    }

    public async Task<IReadOnlyList<IraEvent>> EventsAsync(IraMarks marks, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var fresh = nowUtc - IraAlertService.Freshness;
        var events = new List<IraEvent>();
        events.AddRange(await DecisionsAsync(marks.Decisions, fresh, cancellationToken));
        events.AddRange(await RiskAsync(marks.Signals, fresh, cancellationToken));
        events.AddRange(await IncidentsAsync(marks.Incidents, fresh, cancellationToken));
        events.AddRange(await ComplianceAsync(marks.Compliance, fresh, cancellationToken));
        return events.OrderBy(e => e.AtUtc).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
    }

    // ---------- the AI Trader ----------

    private sealed record DecisionRow(long Id, DateTime CreatedUtc, string Action, string Underlying, bool Allowed, bool Executed, string Error,
        string Reason, string ResultJson);

    private async Task<List<IraEvent>> DecisionsAsync(IraCursor cursor, DateTime fresh, CancellationToken cancellationToken)
    {
        long newest = await db.AiTraderDecisions.MaxAsync(d => (long?)d.Id, cancellationToken) ?? 0;
        var rows = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Id > cursor.Through && d.Mode == AiTraderModes.Live && d.ReplaySessionId == null)
            .OrderBy(d => d.Id)
            .Take(Batch)
            .Select(d => new DecisionRow(d.Id, d.CreatedUtc, d.Action, d.Underlying, d.Allowed, d.Executed, d.Error, d.Reason, d.ResultJson))
            .ToListAsync(cancellationToken);

        var tell = cursor.Pass(rows.Select(d => (d.Id, Verdict(d, fresh))).ToList(), rows.Count == Batch, newest);
        var events = new List<IraEvent>();
        foreach (var d in rows.Where(d => tell.Contains(d.Id)))
        {
            events.Add(new IraEvent(AiTraderKind, $"decision:{d.Id}", Utc(d.CreatedUtc), await DecisionTextAsync(d, cancellationToken)));
        }

        return events;
    }

    /// <summary>
    /// Carried out: told. Allowed to act and not carried out yet, with no outcome (a buy or start waiting for the
    /// Validator and the Risk Memo, an exit being booked): waited for. Anything else: dealt with, nothing to tell.
    /// </summary>
    private static IraVerdict Verdict(DecisionRow d, DateTime fresh)
    {
        if (Utc(d.CreatedUtc) < fresh) return IraVerdict.Skip;
        if (d.Executed) return IraVerdict.Tell;
        bool acts = d.Allowed && d.Action is not ("" or AiTraderPlan.None);
        return acts && (d.Error.Length == 0 || d.Error == AiTraderAgent.AwaitingChecks) ? IraVerdict.Wait : IraVerdict.Skip;
    }

    private async Task<string> DecisionTextAsync(DecisionRow d, CancellationToken cancellationToken)
    {
        var result = Parse(d.ResultJson);
        if (result?["placed"] is JsonObject p)
        {
            return IraAlertText.Bought(Contract(Str(p["symbol"])), (int)(Long(p["lots"]) ?? 0), Dec(p["price"]) ?? 0m, Dec(p["stopLoss"]), Dec(p["target"]), d.Reason);
        }

        if (result?["exit"] is JsonObject x)
        {
            int? lots = Long(x["orderId"]) is long orderId
                ? await db.PaperOrders.AsNoTracking().Where(o => o.Id == orderId).Select(o => (int?)o.Quantity).FirstOrDefaultAsync(cancellationToken)
                : null;
            return IraAlertText.Exited(Contract(Str(x["symbol"])), lots, Dec(x["price"]), d.Reason);
        }

        if (result?["started"] is JsonObject s)
        {
            return IraAlertText.Started(Str(s["strategy"]), Str(s["underlying"]).ToUpperInvariant(), (int)(Long(s["lots"]) ?? 1), Long(s["runId"]), d.Reason);
        }

        if (result?["stopped"] is JsonObject t)
        {
            long? runId = Long(t["runId"]);
            var run = runId is long id
                ? await db.SimulationRuns.AsNoTracking().Where(r => r.Id == id).Select(r => new { r.StrategyName, r.Symbol, r.ParametersJson })
                    .FirstOrDefaultAsync(cancellationToken)
                : null;
            return IraAlertText.StoppedRun(run?.StrategyName, run is null ? null : UnderlyingOf(run.ParametersJson, run.Symbol), runId,
                (int)(Long(t["flattened"]) ?? 0), d.Reason);
        }

        return IraAlertText.Acted(AiTraderResults.Placement(d.ResultJson)?.Text ?? $"{d.Action} {d.Underlying}".Trim(), d.Reason);
    }

    // ---------- the risk guard ----------

    private sealed record SignalRow(long Id, long RunId, string SignalType, string GroupId, string MetadataJson, DateTime CreatedUtc);

    private sealed record RunRow(long Id, long UserId, string Mode, string Status, string StrategyName, string Symbol, string? ParametersJson);

    private async Task<List<IraEvent>> RiskAsync(IraCursor cursor, DateTime fresh, CancellationToken cancellationToken)
    {
        long newest = await db.SimulationSignals.MaxAsync(s => (long?)s.Id, cancellationToken) ?? 0;
        var rows = await db.SimulationSignals.AsNoTracking()
            .Where(s => s.Id > cursor.Through
                        && (s.SignalType == StrategyRunControl.RunStoppedSignalType || s.SignalType == CloseGroup)
                        && s.MetadataJson.Contains(RiskGuardMark))
            .OrderBy(s => s.Id)
            .Take(Batch)
            .Select(s => new SignalRow(s.Id, s.SimulationRunId, s.SignalType, s.GroupId, s.MetadataJson, s.CreatedUtc))
            .ToListAsync(cancellationToken);

        var runIds = rows.Select(r => r.RunId).Distinct().ToList();
        var runs = runIds.Count == 0
            ? new Dictionary<long, RunRow>()
            : await db.SimulationRuns.AsNoTracking()
                .Where(r => runIds.Contains(r.Id))
                .Select(r => new RunRow(r.Id, r.UserId, r.Mode, r.Status, r.StrategyName, r.Symbol, r.ParametersJson))
                .ToDictionaryAsync(r => r.Id, cancellationToken);

        var tell = cursor.Pass(rows.Select(s => (s.Id, Verdict(s, runs, fresh))).ToList(), rows.Count == Batch, newest);
        var told = rows.Where(s => tell.Contains(s.Id)).ToList();
        if (told.Count == 0) return [];

        var userIds = told.Select(s => runs[s.RunId].UserId).Distinct().ToList();
        var users = await db.AppUsers.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);
        var events = new List<IraEvent>();
        foreach (var s in told)
        {
            var run = runs[s.RunId];
            bool open = run.Status is StrategyRunControl.RunStatusRunning or StrategyRunControl.RunStatusStopping;
            var figures = await pnl.FiguresAsync([run.Id], open ? new HashSet<long> { run.Id } : new HashSet<long>(), cancellationToken);
            decimal net = figures.TryGetValue(run.Id, out var f) ? f.Net : 0m;
            string account = users.GetValueOrDefault(run.UserId) ?? $"user {run.UserId}";
            string underlying = UnderlyingOf(run.ParametersJson, run.Symbol);
            string reason = ReasonOf(s.MetadataJson) ?? string.Empty;
            string text = s.SignalType == StrategyRunControl.RunStoppedSignalType
                ? IraAlertText.RunStopped(run.StrategyName, underlying, account, reason, net)
                : IraAlertText.GroupClosed(run.StrategyName, underlying, account, s.GroupId, reason, net);
            events.Add(new IraEvent(RiskKind, $"signal:{s.Id}", Utc(s.CreatedUtc), text));
        }

        return events;
    }

    /// <summary>
    /// A live run's overall stop by the risk guard, or its close of a group by a group rule: told. A leg's own stop, a
    /// recap's or a replay's: dealt with, nothing to tell.
    /// </summary>
    private static IraVerdict Verdict(SignalRow s, Dictionary<long, RunRow> runs, DateTime fresh)
    {
        if (Utc(s.CreatedUtc) < fresh) return IraVerdict.Skip;
        if (!runs.TryGetValue(s.RunId, out var run) || run.Mode != StrategyRunControl.LivePaperMode || IsRecap(run.ParametersJson)) return IraVerdict.Skip;
        string? reason = ReasonOf(s.MetadataJson);
        if (reason is null) return IraVerdict.Skip;
        return s.SignalType == StrategyRunControl.RunStoppedSignalType || reason.StartsWith("Group ", StringComparison.Ordinal)
            ? IraVerdict.Tell
            : IraVerdict.Skip;
    }

    /// <summary>The reason a signal's metadata gives, when the risk guard signed it; null otherwise.</summary>
    private static string? ReasonOf(string metadataJson) =>
        Parse(metadataJson) is JsonObject o && Str(o["by"]) == StrategyRiskGuardService.By && Str(o["reason"]) is { Length: > 0 } reason ? reason : null;

    /// <summary>A recap run, as <see cref="RecapRuns"/> reads its parameters.</summary>
    private static bool IsRecap(string? parametersJson)
    {
        string p = (parametersJson ?? string.Empty).ToLowerInvariant();
        return p.Contains(RecapRuns.Marker) || p.Contains(RecapRuns.SpacedMarker);
    }

    // ---------- Sentinel ----------

    private async Task<List<IraEvent>> IncidentsAsync(IraCursor cursor, DateTime fresh, CancellationToken cancellationToken)
    {
        long newest = await db.Incidents.MaxAsync(i => (long?)i.Id, cancellationToken) ?? 0;
        var rows = await db.Incidents.AsNoTracking()
            .Where(i => i.Id > cursor.Through && (i.Severity == IncidentSeverity.High || i.Severity == IncidentSeverity.Critical))
            .OrderBy(i => i.Id)
            .Take(Batch)
            .Select(i => new { i.Id, i.Severity, i.Title, i.Summary, i.FirstSeenUtc })
            .ToListAsync(cancellationToken);

        var tell = cursor.Pass(rows.Select(i => (i.Id, Utc(i.FirstSeenUtc) < fresh ? IraVerdict.Skip : IraVerdict.Tell)).ToList(), rows.Count == Batch, newest);
        return rows.Where(i => tell.Contains(i.Id))
            .Select(i => new IraEvent(IncidentKind, $"incident:{i.Id}", Utc(i.FirstSeenUtc), IraAlertText.Incident(i.Severity, i.Title, i.Summary)))
            .ToList();
    }

    // ---------- the Compliance Triage ----------

    /// <summary>
    /// Each circular's one compliance alert (<see cref="ComplianceAlerts"/>), told when it went: what applies, and the
    /// dates it takes effect from its triage. A row made by "Mark handled" with no alert is dealt with, nothing to tell.
    /// </summary>
    private async Task<List<IraEvent>> ComplianceAsync(IraCursor cursor, DateTime fresh, CancellationToken cancellationToken)
    {
        long newest = await db.ComplianceFollowUps.MaxAsync(f => (long?)f.Id, cancellationToken) ?? 0;
        var rows = await db.ComplianceFollowUps.AsNoTracking()
            .Where(f => f.Id > cursor.Through)
            .OrderBy(f => f.Id)
            .Take(Batch)
            .Select(f => new { f.Id, f.CircularId, f.ReportId, f.AlertedUtc })
            .ToListAsync(cancellationToken);

        var tell = cursor.Pass(rows.Select(f => (f.Id, f.AlertedUtc is DateTime at && Utc(at) >= fresh ? IraVerdict.Tell : IraVerdict.Skip)).ToList(),
            rows.Count == Batch, newest);
        var told = rows.Where(f => tell.Contains(f.Id)).ToList();
        if (told.Count == 0) return [];

        var circularIds = told.Select(f => f.CircularId).ToList();
        var circulars = await db.RegulatoryCirculars.AsNoTracking().Where(c => circularIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new { c.Source, c.Title }, cancellationToken);
        var reportIds = told.Where(f => f.ReportId != null).Select(f => f.ReportId!.Value).ToList();
        var data = await db.AiReports.AsNoTracking().Where(r => reportIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.DataJson, cancellationToken);
        var events = new List<IraEvent>();
        foreach (var f in told)
        {
            if (!circulars.TryGetValue(f.CircularId, out var c)) continue;
            var facts = f.ReportId is long id && data.TryGetValue(id, out var json) ? ComplianceTriageAgent.ReadFacts(json) : null;
            var dates = facts?.EffectiveDates.Select(d => d.Date).ToList() ?? [];
            events.Add(new IraEvent(ComplianceKind, $"compliance:{f.Id}", Utc(f.AlertedUtc!.Value),
                IraAlertText.Fit(ComplianceAlertText.Spoken(c.Source, c.Title, dates))));
        }

        return events;
    }

    // ---------- the briefings ----------

    /// <summary>
    /// The latest <c>ok</c> report of the day of each slot analyst written by now (<see cref="DeskAnalysts.LatestOkAsync"/>,
    /// what the AI Trader's brief reads), and the morning plan against what is live (<see cref="DeskPlanBuilder"/>, the
    /// Desk's own count).
    /// </summary>
    public async Task<IraMorning> MorningAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var latest = await DeskAnalysts.LatestOkAsync(db, nowUtc, cancellationToken);
        IraMacro? macro = latest.TryGetValue(AiCatalog.MacroAnalyst, out var m) && AiJson.Object(m.DataJson) is { } md
            ? new IraMacro(AiJson.Str(md, "bias") ?? "no band given", DeskAnalysts.Size(md))
            : null;
        var technical = Views(latest, AiCatalog.TechnicalAnalyst, i => $"{AiJson.Str(i, "bias")} {AiJson.Str(i, "grade")}");
        var options = Views(latest, AiCatalog.OptionsAnalyst, i => AiJson.Str(i, "stance") ?? string.Empty);
        return new IraMorning(macro, technical, options, await PlanAsync(cancellationToken));
    }

    private static List<IraIndexView>? Views(Dictionary<string, DeskAnalysts.LatestReport> latest, string agent, Func<JsonObject, string> view)
    {
        if (!latest.TryGetValue(agent, out var r) || AiJson.Object(r.DataJson) is not { } data) return null;
        return (data["indices"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Select(i => new IraIndexView(AiJson.Str(i, "underlying") ?? "?", Spaces(view(i))))
            .ToList();
    }

    private async Task<List<IraPlanAccount>?> PlanAsync(CancellationToken cancellationToken)
    {
        if (plan is null) return null;
        AlgoTrading.Contracts.Desk.DeskPlanResponse? response;
        try
        {
            response = await plan.BuildAsync(cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }

        if (response is null) return null;
        var accounts = response.Accounts.Concat(response.Runs.Select(r => r.Account)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return accounts
            .Select(a => new IraPlanAccount(a,
                response.Runs.Count(r => string.Equals(r.Account, a, StringComparison.OrdinalIgnoreCase) && r.IsLive),
                response.Runs.Count(r => string.Equals(r.Account, a, StringComparison.OrdinalIgnoreCase))))
            .ToList();
    }

    /// <summary>The day's trading and the AI Trader's day as Today states them: the same calls, so the two never differ.</summary>
    public async Task<IraClose> CloseAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (today is null) throw new InvalidOperationException("Today's builder is not available: the close briefing has nothing to read.");
        var day = IstTime.DateOf(nowUtc);
        var trading = await today.TradingAsync(day, cancellationToken);
        var trader = await today.AiTraderAsync(day, cancellationToken);

        // Today lists the runs worst first.
        var runs = trading.Runs.Select(r => new IraRunNet(r.RunId, r.Strategy, r.Underlying, r.UserName, r.Net)).ToList();
        IraTraderDay? traderDay = trader is null ? null
            : trader.Mode == AiTraderModes.Live
                ? new IraTraderDay(AiTraderModes.Live, trader.Placed, trader.Live?.Positions ?? 0, trader.Live?.Net ?? 0m)
                : new IraTraderDay(AiTraderModes.Shadow, trader.ShadowPositions, trader.ShadowPositions, trader.ShadowNet);
        return new IraClose(
            trading.Accounts.Select(a => new IraAccountNet(a.UserName, a.Net)).ToList(),
            runs.Count == 0 ? null : runs[^1],
            runs.Count == 0 ? null : runs[0],
            runs.Count,
            traderDay);
    }

    // ---------- reading ----------

    private static string UnderlyingOf(string? parametersJson, string symbol) =>
        (LiveRunParameters.Parse(parametersJson).Underlying ?? UnderlyingCatalog.InferUnderlying(symbol)).Trim().ToUpperInvariant();

    /// <summary>"NIFTY 25100 CE" for an option's symbol; else the symbol without its exchange.</summary>
    private static string Contract(string symbol)
    {
        var o = UnderlyingCatalog.ParseOptionSymbol(symbol);
        if (o is not null) return UnderlyingCatalog.ContractLabel(o.Underlying, o.Strike, o.OptionType, null);
        int colon = symbol.IndexOf(':');
        return colon >= 0 ? symbol[(colon + 1)..] : symbol;
    }

    private static string Spaces(string text) => string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? string.Empty : string.Empty;

    private static long? Long(JsonNode? n) => n is JsonValue v && v.TryGetValue(out long l) ? l : null;

    private static decimal? Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue(out decimal d) ? d : null;
}

/// <summary>What becomes of a row Ira reads: told now, dealt with and not told, or waited for (its outcome is not known yet).</summary>
public enum IraVerdict
{
    Tell,
    Skip,
    Wait,
}

/// <summary>
/// One source's high-water mark: every row at or below <see cref="Through"/> is dealt with; <see cref="Told"/> lists
/// the rows above it already told; <see cref="Seen"/> is the newest row a pass saw, the furthest the next pass may move
/// <see cref="Through"/>.
/// </summary>
public sealed class IraCursor
{
    public long Through { get; set; }

    public long Seen { get; set; }

    public List<long> Told { get; set; } = [];

    public static IraCursor At(long newest) => new() { Through = newest, Seen = newest };

    /// <summary>
    /// One pass over the rows after <see cref="Through"/>, oldest first, each with its verdict: the ids to tell now.
    /// <see cref="Through"/> moves to the newest row seen a pass ago, never past a row waited for, and never past the
    /// last row read when <paramref name="truncated"/>.
    /// </summary>
    public HashSet<long> Pass(IReadOnlyList<(long Id, IraVerdict Verdict)> rows, bool truncated, long newest)
    {
        var tell = new HashSet<long>();
        var dealt = new HashSet<long>(Told);
        long? firstWait = null;
        foreach (var (id, verdict) in rows)
        {
            if (id <= Through || dealt.Contains(id)) continue;
            switch (verdict)
            {
                case IraVerdict.Tell:
                    tell.Add(id);
                    dealt.Add(id);
                    break;
                case IraVerdict.Skip:
                    dealt.Add(id);
                    break;
                default:
                    firstWait ??= id;
                    break;
            }
        }

        long through = Seen;
        if (firstWait is long wait) through = Math.Min(through, wait - 1);
        if (truncated && rows.Count > 0) through = Math.Min(through, rows[^1].Id);
        Through = Math.Max(Through, through);
        Told = dealt.Where(id => id > Through).OrderBy(id => id).ToList();
        Seen = Math.Max(Seen, newest);
        return tell;
    }
}
